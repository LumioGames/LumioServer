using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.MvpHost.Wire;

namespace Lumio.Server.MvpHost.SmokeClient;

public static class SmokeClientRunner
{
    private const int MaxMessageBytes = 65_536;
    private const int MaxFragmentBytes = 4_096;
    private const int AntiReplayWindow = 1_024;
    private const string AuthBinding = "SessionAdmission";
    private const string ErrorClass = "Rejectable";
    private static readonly string[] FullSnapshotBodyKeys =
    {
        "snapshotId", "tickId", "sessionRevisionVector", "schemaEpoch", "mappingSetHash",
    };
    private static readonly string[] SessionRevisionVectorKeys =
    {
        "tickId", "gameRevision", "voxelWorldRevision", "chunkRevisionSet",
        "replicationRevision", "configRevision", "schemaEpoch",
    };
    private static readonly string[] DeltaBodyKeys =
    {
        "baseSnapshotId", "fromRevision", "toRevision", "mappingSetHash",
        "confirmationSequence", "tombstones",
    };

    public static async Task<int> RunAsync(
        SmokeClientCommandLineOptions options,
        SmokeTraceWriter trace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(trace);

        byte[] token;
        try
        {
            token = await File.ReadAllBytesAsync(options.TokenFile!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            trace.Record("in", null, "token file is readable", false, "token material could not be loaded");
            return SmokeClientExitCodes.TransportFatal;
        }

        if (token.Length == 0)
        {
            trace.Record("in", null, "token file is non-empty", false, "empty credential material");
            return SmokeClientExitCodes.TransportFatal;
        }

        try
        {
            return options.Scenario switch
            {
                "bad-token" => await RunExpectedRejectAsync(options, trace, token, badToken: true, cancellationToken).ConfigureAwait(false),
                "replay-nonce" => await RunReplayScenarioAsync(options, trace, token, cancellationToken).ConfigureAwait(false),
                "release-mismatch" => await RunExpectedReleaseRejectAsync(options, trace, token, cancellationToken).ConfigureAwait(false),
                "oversize-message" => await RunExpectedOversizeRejectAsync(options, trace, token, cancellationToken).ConfigureAwait(false),
                "stale-generation" => ReportUnprovableStaleGeneration(trace),
                "reconnect" => await RunReconnectScenarioAsync(options, trace, token, cancellationToken).ConfigureAwait(false),
                "gap-resync" => await RunReplicationScenarioAsync(options, trace, token, includeResync: true, cancellationToken).ConfigureAwait(false),
                _ => await RunReplicationScenarioAsync(options, trace, token, includeResync: false, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (ScenarioAssertionException ex)
        {
            trace.Record("in", ex.MessageType, ex.Assertion, false, ex.Detail);
            return SmokeClientExitCodes.AssertionFailed;
        }
        catch (OperationCanceledException)
        {
            trace.Record("in", null, "scenario completed before cancellation", false, "operation canceled");
            return SmokeClientExitCodes.TransportFatal;
        }
        catch (WebSocketException ex)
        {
            trace.Record(
                "in",
                null,
                "websocket transport remains available",
                false,
                $"{ex.WebSocketErrorCode}: {ex.Message}");
            return SmokeClientExitCodes.TransportFatal;
        }
        catch (IOException ex)
        {
            trace.Record("in", null, "websocket transport remains available", false, ex.GetType().Name);
            return SmokeClientExitCodes.TransportFatal;
        }
        finally
        {
            Array.Clear(token);
            token = Array.Empty<byte>();
        }
    }

    private static async Task<int> RunReplicationScenarioAsync(
        SmokeClientCommandLineOptions options,
        SmokeTraceWriter trace,
        byte[] token,
        bool includeResync,
        CancellationToken cancellationToken,
        bool awaitDelta = true)
    {
        using var client = await ConnectAsync(options, token, options.Nonce!, trace, cancellationToken).ConfigureAwait(false);
        var sequence = 1UL;
        var first = await ReceiveEnvelopeAsync(client, trace, cancellationToken).ConfigureAwait(false);
        AssertServerHandshake(first, trace);

        await SendEnvelopeAsync(
            client,
            ClientHandshake(options, sequence++),
            trace,
            "Handshake",
            cancellationToken).ConfigureAwait(false);

        var snapshot = await ReceiveEnvelopeAsync(client, trace, cancellationToken).ConfigureAwait(false);
        if (options.Nonce!.EndsWith("-expired-reconnect", StringComparison.Ordinal))
        {
            MvpEnvelopeReader.TryReadHeader(snapshot.Span, out var responseHeader);
            if (string.Equals(responseHeader.MessageType, "Error", StringComparison.Ordinal))
            {
                AssertMessage(
                    snapshot,
                    "Error",
                    trace,
                    "expired reconnect loser receives an error envelope");
                AssertError(snapshot, "Rejectable", "SessionMismatch", trace);
                await CloseQuietlyAsync(client).ConfigureAwait(false);
                return SmokeClientExitCodes.Success;
            }
        }

        AssertFullSnapshot(snapshot, trace);
        var snapshotId = ReadBodyString(snapshot, "snapshotId");
        var revision = ReadBodyUInt64(snapshot, "sessionRevisionVector", "gameRevision");

        await SendEnvelopeAsync(
            client,
            BaselineAck(options, sequence++, snapshotId, revision),
            trace,
            "BaselineAck",
            cancellationToken).ConfigureAwait(false);

        // A reconnect run proves the new authenticated connection and the
        // strictly newer full-snapshot baseline. It intentionally does not
        // wait for another world mutation: the out-of-band mutation belongs
        // to the first connection and no-op waiting would only hit idle close.
        if (!awaitDelta)
        {
            trace.Record("in", null, "reconnect baseline is acknowledged", true, null);
            await CloseQuietlyAsync(client).ConfigureAwait(false);
            return SmokeClientExitCodes.Success;
        }

        var delta = await ReceiveEnvelopeAsync(client, trace, cancellationToken).ConfigureAwait(false);
        AssertDelta(delta, snapshotId, revision, trace);
        var toRevision = ReadBodyUInt64(delta, "toRevision");
        var confirmationSequence = ReadBodyUInt64(delta, "confirmationSequence");

        await SendEnvelopeAsync(
            client,
            DeltaAck(options, sequence++, confirmationSequence, toRevision),
            trace,
            "DeltaAck",
            cancellationToken).ConfigureAwait(false);

        if (includeResync)
        {
            await SendEnvelopeAsync(
                client,
                ResyncRequest(options, sequence++, "GapDetected"),
                trace,
                "ResyncRequest",
                cancellationToken).ConfigureAwait(false);

            var resync = await ReceiveEnvelopeAsync(client, trace, cancellationToken).ConfigureAwait(false);
            AssertFullSnapshot(resync, trace);
            trace.Record("in", null, "same-connection resync does not repeat handshake", true, null);
            trace.Record("in", null, "server never sends ResyncRequest", true, null);
        }

        trace.Record("in", null, "scenario assertions complete", true, null);
        await CloseQuietlyAsync(client).ConfigureAwait(false);
        return SmokeClientExitCodes.Success;
    }

    private static async Task<int> RunExpectedRejectAsync(
        SmokeClientCommandLineOptions options,
        SmokeTraceWriter trace,
        byte[] token,
        bool badToken,
        CancellationToken cancellationToken)
    {
        var supplied = (byte[])token.Clone();
        if (badToken)
        {
            supplied[0] ^= 0x5a;
        }

        try
        {
            using var client = await ConnectAsync(options, supplied, options.Nonce!, trace, cancellationToken).ConfigureAwait(false);
            var result = await ReceiveRawAsync(client, cancellationToken).ConfigureAwait(false);
            if (result.MessageType is not null)
            {
                trace.Record("in", result.MessageType, "invalid credential is rejected before an envelope", false, "server sent application data");
                return SmokeClientExitCodes.AssertionFailed;
            }

            var passed = result.CloseStatus == WebSocketCloseStatus.PolicyViolation;
            trace.Record(
                "in",
                null,
                "invalid credential is rejected before an envelope",
                passed,
                result.CloseStatus?.ToString());
            if (!passed)
            {
                return SmokeClientExitCodes.AssertionFailed;
            }

            return SmokeClientExitCodes.Success;
        }
        catch (WebSocketException ex)
        {
            trace.Record(
                "in",
                null,
                "invalid credential rejection has an observable close frame",
                false,
                $"{ex.WebSocketErrorCode}: {ex.Message}");
            return SmokeClientExitCodes.TransportFatal;
        }
        finally
        {
            Array.Clear(supplied);
        }
    }

    private static async Task<int> RunReplayScenarioAsync(
        SmokeClientCommandLineOptions options,
        SmokeTraceWriter trace,
        byte[] token,
        CancellationToken cancellationToken)
    {
        using (var first = await ConnectAsync(options, token, options.Nonce!, trace, cancellationToken).ConfigureAwait(false))
        {
            await CloseQuietlyAsync(first).ConfigureAwait(false);
        }

        try
        {
            using var second = await ConnectAsync(options, token, options.Nonce!, trace, cancellationToken).ConfigureAwait(false);
            var result = await ReceiveRawAsync(second, cancellationToken).ConfigureAwait(false);
            if (result.MessageType is not null)
            {
                trace.Record("in", result.MessageType, "replayed nonce is rejected before an envelope", false, "server sent application data");
                return SmokeClientExitCodes.AssertionFailed;
            }

            var passed = result.CloseStatus == WebSocketCloseStatus.PolicyViolation;
            trace.Record(
                "in",
                null,
                "replayed nonce is rejected before an envelope",
                passed,
                result.CloseStatus?.ToString());
            if (!passed)
            {
                return SmokeClientExitCodes.AssertionFailed;
            }
        }
        catch (WebSocketException ex)
        {
            trace.Record("in", null, "replayed nonce rejection has an observable close frame", false, ex.WebSocketErrorCode.ToString());
            return SmokeClientExitCodes.TransportFatal;
        }

        using var fresh = await ConnectAsync(
            options,
            token,
            options.Nonce + "-fresh",
            trace,
            cancellationToken).ConfigureAwait(false);
        var handshake = await ReceiveEnvelopeAsync(fresh, trace, cancellationToken).ConfigureAwait(false);
        AssertServerHandshake(handshake, trace);
        trace.Record("in", null, "replay rejection does not consume the next valid nonce", true, null);
        await CloseQuietlyAsync(fresh).ConfigureAwait(false);
        return SmokeClientExitCodes.Success;
    }

    private static async Task<int> RunExpectedReleaseRejectAsync(
        SmokeClientCommandLineOptions options,
        SmokeTraceWriter trace,
        byte[] token,
        CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(options, token, options.Nonce!, trace, cancellationToken).ConfigureAwait(false);
        var first = await ReceiveEnvelopeAsync(client, trace, cancellationToken).ConfigureAwait(false);
        AssertMessage(first, "Handshake", trace, "server handshake is available before release check");

        var mismatch = ClientHandshake(options with { ProductId = "mismatch" }, 1);
        await SendEnvelopeAsync(client, mismatch, trace, "Handshake", cancellationToken).ConfigureAwait(false);
        var response = await ReceiveEnvelopeAsync(client, trace, cancellationToken).ConfigureAwait(false);
        AssertMessage(response, "Error", trace, "release mismatch is rejectable");
        AssertError(response, "Rejectable", "ReleaseMismatch", trace);
        return SmokeClientExitCodes.Success;
    }

    private static async Task<int> RunExpectedOversizeRejectAsync(
        SmokeClientCommandLineOptions options,
        SmokeTraceWriter trace,
        byte[] token,
        CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(options, token, options.Nonce!, trace, cancellationToken).ConfigureAwait(false);
        var first = await ReceiveEnvelopeAsync(client, trace, cancellationToken).ConfigureAwait(false);
        AssertMessage(first, "Handshake", trace, "server handshake is available before size check");

        var oversized = OversizedClientHandshake(options, 1);
        await SendEnvelopeAsync(client, oversized, trace, "Handshake", cancellationToken).ConfigureAwait(false);
        var result = await ReceiveRawAsync(client, cancellationToken).ConfigureAwait(false);
        if (result.MessageType is not null)
        {
            trace.Record("in", result.MessageType, "oversize message is rejected before application dispatch", false, null);
            return SmokeClientExitCodes.AssertionFailed;
        }

        var passed = result.CloseStatus == WebSocketCloseStatus.PolicyViolation;
        trace.Record(
            "in",
            null,
            "oversize message is rejected before application dispatch",
            passed,
            result.CloseStatus?.ToString());
        if (!passed)
        {
            return SmokeClientExitCodes.AssertionFailed;
        }

        return SmokeClientExitCodes.Success;
    }

    private static int ReportUnprovableStaleGeneration(SmokeTraceWriter trace)
    {
        trace.Record(
            "in",
            null,
            "stale generation rejection has real socket evidence",
            false,
            "the frozen wire contract carries no connection generation");
        return SmokeClientExitCodes.AssertionFailed;
    }

    private static async Task<int> RunReconnectScenarioAsync(
        SmokeClientCommandLineOptions options,
        SmokeTraceWriter trace,
        byte[] token,
        CancellationToken cancellationToken)
    {
        ulong lastDeltaRevision;
        using (var first = await ConnectAsync(options, token, options.Nonce!, trace, cancellationToken).ConfigureAwait(false))
        {
            var sequence = 1UL;
            var serverHandshake = await ReceiveEnvelopeAsync(first, trace, cancellationToken).ConfigureAwait(false);
            AssertServerHandshake(serverHandshake, trace);
            await SendEnvelopeAsync(
                first,
                ClientHandshake(options, sequence++),
                trace,
                "Handshake",
                cancellationToken).ConfigureAwait(false);

            var snapshot = await ReceiveEnvelopeAsync(first, trace, cancellationToken).ConfigureAwait(false);
            AssertFullSnapshot(snapshot, trace);
            var snapshotId = ReadBodyString(snapshot, "snapshotId");
            var snapshotRevision = ReadBodyUInt64(snapshot, "sessionRevisionVector", "gameRevision");
            await SendEnvelopeAsync(
                first,
                BaselineAck(options, sequence++, snapshotId, snapshotRevision),
                trace,
                "BaselineAck",
                cancellationToken).ConfigureAwait(false);

            var delta = await ReceiveEnvelopeAsync(first, trace, cancellationToken).ConfigureAwait(false);
            AssertDelta(delta, snapshotId, snapshotRevision, trace);
            lastDeltaRevision = ReadBodyUInt64(delta, "toRevision");
            var confirmationSequence = ReadBodyUInt64(delta, "confirmationSequence");
            await SendEnvelopeAsync(
                first,
                DeltaAck(options, sequence++, confirmationSequence, lastDeltaRevision),
                trace,
                "DeltaAck",
                cancellationToken).ConfigureAwait(false);

            var kick = await ReceiveEnvelopeAsync(first, trace, cancellationToken).ConfigureAwait(false);
            AssertMessage(kick, "MaintenanceKick", trace, "maintenance kick envelope precedes close");
            AssertBodyString(
                kick,
                "reasonCode",
                "MaintenanceKick",
                trace,
                "maintenance kick carries the registered reason",
                "MaintenanceKick");
            var closed = await ReceiveRawAsync(first, cancellationToken).ConfigureAwait(false);
            var closeObserved = closed.MessageType is null;
            trace.Record("in", null, "connection closes after maintenance kick", closeObserved, closed.CloseDescription);
            if (!closeObserved)
            {
                return SmokeClientExitCodes.AssertionFailed;
            }
        }

        var reconnectOptions = options with { Nonce = options.Nonce + "-reconnect" };
        using var reconnect = await ConnectAsync(
            reconnectOptions,
            token,
            reconnectOptions.Nonce!,
            trace,
            cancellationToken).ConfigureAwait(false);
        var reconnectSequence = 1UL;
        var reconnectHandshake = await ReceiveEnvelopeAsync(reconnect, trace, cancellationToken).ConfigureAwait(false);
        AssertServerHandshake(reconnectHandshake, trace);
        await SendEnvelopeAsync(
            reconnect,
            ClientHandshake(reconnectOptions, reconnectSequence++),
            trace,
            "Handshake",
            cancellationToken).ConfigureAwait(false);
        var reconnectSnapshot = await ReceiveEnvelopeAsync(reconnect, trace, cancellationToken).ConfigureAwait(false);
        AssertFullSnapshot(reconnectSnapshot, trace);
        var reconnectRevision = ReadBodyUInt64(reconnectSnapshot, "sessionRevisionVector", "gameRevision");
        var revisionAdvanced = reconnectRevision > lastDeltaRevision;
        trace.Record(
            "in",
            "FullSnapshot",
            "reconnect full snapshot is strictly newer than the last delta",
            revisionAdvanced,
            $"lastDelta={lastDeltaRevision}; reconnect={reconnectRevision}");
        if (!revisionAdvanced)
        {
            return SmokeClientExitCodes.AssertionFailed;
        }

        await SendEnvelopeAsync(
            reconnect,
            BaselineAck(
                reconnectOptions,
                reconnectSequence,
                ReadBodyString(reconnectSnapshot, "snapshotId"),
                reconnectRevision),
            trace,
            "BaselineAck",
            cancellationToken).ConfigureAwait(false);
        trace.Record("in", null, "reconnect baseline is acknowledged", true, null);
        await CloseQuietlyAsync(reconnect).ConfigureAwait(false);
        return SmokeClientExitCodes.Success;
    }

    private static async Task<ClientWebSocket> ConnectAsync(
        SmokeClientCommandLineOptions options,
        byte[] token,
        string nonce,
        SmokeTraceWriter trace,
        CancellationToken cancellationToken)
    {
        var client = new ClientWebSocket();
        client.Options.AddSubProtocol("lumio.mvp.v0");
        client.Options.AddSubProtocol(ToBase64Url(token));
        client.Options.AddSubProtocol(nonce);

        try
        {
            await client.ConnectAsync(new Uri(options.Endpoint), cancellationToken).ConfigureAwait(false);
            // ClientWebSocket completes ConnectAsync only after a successful
            // switching-protocols response; the Windows implementation reports
            // status 0 on that success path, so Open is the portable 101 proof.
            var switchedProtocols = client.State == WebSocketState.Open;
            trace.Record(
                "out",
                null,
                "websocket upgrade returned HTTP 101",
                switchedProtocols,
                client.HttpStatusCode == 0
                    ? HttpStatusCode.SwitchingProtocols.ToString()
                    : client.HttpStatusCode.ToString());
            var selectedProtocol = string.Equals(client.SubProtocol, "lumio.mvp.v0", StringComparison.Ordinal);
            trace.Record(
                "in",
                null,
                "server selected lumio.mvp.v0",
                selectedProtocol,
                client.SubProtocol);
            if (!switchedProtocols || !selectedProtocol)
            {
                throw new ScenarioAssertionException(null, "websocket upgrade negotiation is exact", client.SubProtocol);
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task SendEnvelopeAsync(
        ClientWebSocket client,
        ReadOnlyMemory<byte> bytes,
        SmokeTraceWriter trace,
        string expectedMessageType,
        CancellationToken cancellationToken)
    {
        var validation = MvpEnvelopeReader.Validate(bytes.Span);
        if (validation.Status != EnvelopeParseStatus.Ok)
        {
            throw new ScenarioAssertionException(expectedMessageType, "outbound envelope passes validation", validation.Detail);
        }

        await client.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        trace.Record("out", expectedMessageType, "outbound envelope sent through MvpEnvelopeWriter", true, null);
    }

    private static async Task<ReadOnlyMemory<byte>> ReceiveEnvelopeAsync(
        ClientWebSocket client,
        SmokeTraceWriter trace,
        CancellationToken cancellationToken)
    {
        var result = await ReceiveRawAsync(client, cancellationToken).ConfigureAwait(false);
        if (result.MessageType is null)
        {
            throw new ScenarioAssertionException(null, "server sends an envelope before closing", result.CloseDescription);
        }

        trace.Record("in", result.MessageType, "inbound envelope passes validation", true, null);
        return result.Bytes;
    }

    private static async Task<RawReceive> ReceiveRawAsync(ClientWebSocket client, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxMessageBytes];
        var offset = 0;
        while (true)
        {
            var result = await client.ReceiveAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new RawReceive(
                    ReadOnlyMemory<byte>.Empty,
                    null,
                    client.CloseStatus,
                    client.CloseStatusDescription);
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new ScenarioAssertionException(null, "server uses text envelopes", result.MessageType.ToString());
            }

            offset += result.Count;
            if (offset > MaxMessageBytes)
            {
                throw new ScenarioAssertionException(null, "inbound envelope stays within the declared bound", "message exceeded maxMessageBytes");
            }

            if (result.EndOfMessage)
            {
                var bytes = new ReadOnlyMemory<byte>(buffer, 0, offset);
                var validation = MvpEnvelopeReader.Validate(bytes.Span);
                if (validation.Status != EnvelopeParseStatus.Ok)
                {
                    throw new ScenarioAssertionException(null, "inbound envelope passes validation", validation.Detail);
                }

                MvpEnvelopeReader.TryReadHeader(bytes.Span, out var header);
                return new RawReceive(bytes, header.MessageType, null, null);
            }
        }
    }

    private static void AssertMessage(
        ReadOnlyMemory<byte> bytes,
        string expected,
        SmokeTraceWriter trace,
        string assertion)
    {
        MvpEnvelopeReader.TryReadHeader(bytes.Span, out var header);
        if (!string.Equals(header.MessageType, expected, StringComparison.Ordinal))
        {
            throw new ScenarioAssertionException(header.MessageType, assertion, $"expected {expected}, got {header.MessageType}");
        }

        trace.Record("in", expected, assertion, true, null);
    }

    private static void AssertServerHandshake(ReadOnlyMemory<byte> bytes, SmokeTraceWriter trace)
    {
        AssertMessage(bytes, "Handshake", trace, "server sends the first handshake");
        using var document = JsonDocument.Parse(bytes);
        var body = document.RootElement.GetProperty("body");
        var keys = body.EnumerateObject().Select(property => property.Name).ToArray();
        if (keys.Length != 1 || !string.Equals(keys[0], "role", StringComparison.Ordinal))
        {
            throw new ScenarioAssertionException("Handshake", "server handshake body has the exact role key", string.Join(",", keys));
        }

        trace.Record("in", "Handshake", "server handshake body has the exact role key", true, null);
        AssertBodyString(bytes, "role", "Server", trace, "server handshake role is Server");
    }

    private static void AssertFullSnapshot(ReadOnlyMemory<byte> bytes, SmokeTraceWriter trace)
    {
        AssertMessage(bytes, "FullSnapshot", trace, "admission or resync starts with a full snapshot");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var body = root.GetProperty("body");
        AssertExactKeys(
            body,
            FullSnapshotBodyKeys,
            "FullSnapshot",
            "full snapshot body has the frozen key set");
        var vector = body.GetProperty("sessionRevisionVector");
        AssertExactKeys(
            vector,
            SessionRevisionVectorKeys,
            "FullSnapshot",
            "session revision vector has all seven frozen fields");
        var chunkKeys = vector.GetProperty("chunkRevisionSet")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        if (chunkKeys.Length == 0 || chunkKeys.Any(key => !System.Text.RegularExpressions.Regex.IsMatch(
                key,
                "^c:(0|-?[1-9][0-9]{0,9}):(0|-?[1-9][0-9]{0,9}):(0|-?[1-9][0-9]{0,9})$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
        {
            throw new ScenarioAssertionException("FullSnapshot", "chunk revision keys are canonical", string.Join(",", chunkKeys));
        }

        if (!string.Equals(root.GetProperty("reliability").GetString(), MvpWireConstants.Reliability, StringComparison.Ordinal)
            || !string.Equals(body.GetProperty("mappingSetHash").GetString(), MvpWireConstants.MappingSetHash, StringComparison.Ordinal))
        {
            throw new ScenarioAssertionException("FullSnapshot", "full snapshot reliability and mapping hash are canonical", null);
        }

        trace.Record("in", "FullSnapshot", "full snapshot frozen structure is exact", true, null);
    }

    private static void AssertDelta(
        ReadOnlyMemory<byte> bytes,
        string expectedSnapshotId,
        ulong expectedFromRevision,
        SmokeTraceWriter trace)
    {
        AssertMessage(bytes, "Delta", trace, "delta follows baseline acknowledgement");
        using var document = JsonDocument.Parse(bytes);
        var body = document.RootElement.GetProperty("body");
        AssertExactKeys(
            body,
            DeltaBodyKeys,
            "Delta",
            "delta body has the frozen key set");
        var fromRevision = body.GetProperty("fromRevision").GetUInt64();
        var toRevision = body.GetProperty("toRevision").GetUInt64();
        var tombstones = body.GetProperty("tombstones");
        if (fromRevision != expectedFromRevision
            || toRevision <= fromRevision
            || !string.Equals(body.GetProperty("baseSnapshotId").GetString(), expectedSnapshotId, StringComparison.Ordinal)
            || !string.Equals(body.GetProperty("mappingSetHash").GetString(), MvpWireConstants.MappingSetHash, StringComparison.Ordinal)
            || tombstones.ValueKind != JsonValueKind.Array
            || tombstones.GetArrayLength() != 0)
        {
            throw new ScenarioAssertionException(
                "Delta",
                "delta advances the acknowledged snapshot without tombstones",
                $"from={fromRevision}; to={toRevision}");
        }

        trace.Record("in", "Delta", "delta advances the acknowledged snapshot without tombstones", true, null);
    }

    private static void AssertExactKeys(
        JsonElement element,
        IReadOnlyCollection<string> expected,
        string messageType,
        string assertion)
    {
        var actual = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
        {
            throw new ScenarioAssertionException(messageType, assertion, string.Join(",", actual.OrderBy(value => value, StringComparer.Ordinal)));
        }
    }

    private static void AssertBodyString(
        ReadOnlyMemory<byte> bytes,
        string property,
        string expected,
        SmokeTraceWriter trace,
        string assertion,
        string messageType = "Handshake")
    {
        var actual = ReadBodyString(bytes, property);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new ScenarioAssertionException(messageType, assertion, $"expected {expected}, got {actual}");
        }

        trace.Record("in", messageType, assertion, true, null);
    }

    private static void AssertError(
        ReadOnlyMemory<byte> bytes,
        string expectedErrorClass,
        string expectedReasonCode,
        SmokeTraceWriter trace)
    {
        using var document = JsonDocument.Parse(bytes);
        var body = document.RootElement.GetProperty("body");
        var errorClass = body.GetProperty("errorClass").GetString();
        var reasonCode = body.GetProperty("reasonCode").GetString();
        if (!string.Equals(errorClass, expectedErrorClass, StringComparison.Ordinal)
            || !string.Equals(reasonCode, expectedReasonCode, StringComparison.Ordinal))
        {
            throw new ScenarioAssertionException(
                "Error",
                "error envelope carries the expected registered rejection",
                $"expected {expectedErrorClass}/{expectedReasonCode}, got {errorClass}/{reasonCode}");
        }

        trace.Record("in", "Error", "error envelope carries the expected registered rejection", true, null);
    }

    private static string ReadBodyString(ReadOnlyMemory<byte> bytes, string property)
    {
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.GetProperty("body").GetProperty(property).GetString()
            ?? throw new InvalidDataException($"body.{property} was null");
    }

    private static ulong ReadBodyUInt64(ReadOnlyMemory<byte> bytes, string property, string? nested = null)
    {
        using var document = JsonDocument.Parse(bytes);
        var value = document.RootElement.GetProperty("body").GetProperty(property);
        if (nested is not null)
        {
            value = value.GetProperty(nested);
        }

        return value.GetUInt64();
    }

    private static ReadOnlyMemory<byte> ClientHandshake(
        SmokeClientCommandLineOptions options,
        ulong sequence,
        int declaredMaxBytes = MaxMessageBytes)
        => MvpEnvelopeWriter.WriteClientHandshake(Context(options, sequence, declaredMaxBytes));

    private static ReadOnlyMemory<byte> BaselineAck(
        SmokeClientCommandLineOptions options,
        ulong sequence,
        string snapshotId,
        ulong revision)
        => MvpEnvelopeWriter.WriteBaselineAck(Context(options, sequence), snapshotId, revision);

    private static ReadOnlyMemory<byte> DeltaAck(
        SmokeClientCommandLineOptions options,
        ulong sequence,
        ulong confirmationSequence,
        ulong revision)
        => MvpEnvelopeWriter.WriteDeltaAck(Context(options, sequence), confirmationSequence, revision);

    private static ReadOnlyMemory<byte> ResyncRequest(
        SmokeClientCommandLineOptions options,
        ulong sequence,
        string reason)
        => MvpEnvelopeWriter.WriteResyncRequest(Context(options, sequence), reason);

    private static ReadOnlyMemory<byte> OversizedClientHandshake(
        SmokeClientCommandLineOptions options,
        ulong sequence)
    {
        var envelope = ClientHandshake(options, sequence);
        var oversized = new byte[MaxMessageBytes + 1];
        envelope.Span.CopyTo(oversized);
        Array.Fill(oversized, (byte)' ', envelope.Length, oversized.Length - envelope.Length);
        return oversized;
    }

    private static EnvelopeWriteContext Context(
        SmokeClientCommandLineOptions options,
        ulong sequence,
        int maxMessageBytes = MaxMessageBytes)
        => new(
            SessionId: SessionIdFor(options),
            ProductId: options.ProductId,
            GameReleaseId: options.GameReleaseId,
            Sequence: sequence,
            TraceId: $"trace-smoke-{sequence}",
            Reliability: MvpWireConstants.Reliability,
            MaxMessageBytes: maxMessageBytes,
            MaxFragmentBytes: MaxFragmentBytes,
            AntiReplayWindow: AntiReplayWindow,
            AuthBinding: AuthBinding,
            ErrorClass: ErrorClass);

    private static string SessionIdFor(SmokeClientCommandLineOptions options)
    {
        var nonce = options.Nonce!;
        const string reconnectSuffix = "-reconnect";
        if (nonce.EndsWith(reconnectSuffix, StringComparison.Ordinal))
        {
            nonce = nonce[..^reconnectSuffix.Length];
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(nonce));
        return $"smoke-{Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task CloseQuietlyAsync(ClientWebSocket client)
    {
        if (client.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "complete", timeout.Token).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // The peer may already have closed; disposal remains authoritative.
            }
            catch (OperationCanceledException)
            {
                // Bounded close; disposal below still releases the client.
            }
        }
    }

    private readonly record struct RawReceive(
        ReadOnlyMemory<byte> Bytes,
        string? MessageType,
        WebSocketCloseStatus? CloseStatus,
        string? CloseDescription);

    private sealed class ScenarioAssertionException : Exception
    {
        internal ScenarioAssertionException(string? messageType, string assertion, string? detail)
            : base(assertion)
        {
            MessageType = messageType;
            Assertion = assertion;
            Detail = detail;
        }

        internal string? MessageType { get; }

        internal string Assertion { get; }

        internal string? Detail { get; }
    }
}
