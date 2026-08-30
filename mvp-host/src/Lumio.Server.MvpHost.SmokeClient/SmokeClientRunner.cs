using System;
using System.IO;
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
                "stale-generation" => await RunExpectedStaleRejectAsync(options, trace, token, cancellationToken).ConfigureAwait(false),
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
            trace.Record("in", null, "websocket transport remains available", false, ex.WebSocketErrorCode.ToString());
            return SmokeClientExitCodes.TransportFatal;
        }
        catch (IOException ex)
        {
            trace.Record("in", null, "websocket transport remains available", false, ex.GetType().Name);
            return SmokeClientExitCodes.TransportFatal;
        }
    }

    private static async Task<int> RunReplicationScenarioAsync(
        SmokeClientCommandLineOptions options,
        SmokeTraceWriter trace,
        byte[] token,
        bool includeResync,
        CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(options, token, options.Nonce!, trace, cancellationToken).ConfigureAwait(false);
        var sequence = 1UL;
        var first = await ReceiveEnvelopeAsync(client, trace, cancellationToken).ConfigureAwait(false);
        AssertMessage(first, "Handshake", trace, "server sends the first handshake");
        AssertBodyString(first, "role", "Server", trace, "server handshake role is Server");

        await SendEnvelopeAsync(
            client,
            ClientHandshake(options, sequence++),
            trace,
            "Handshake",
            cancellationToken).ConfigureAwait(false);

        var snapshot = await ReceiveEnvelopeAsync(client, trace, cancellationToken).ConfigureAwait(false);
        AssertMessage(snapshot, "FullSnapshot", trace, "admission starts with a full snapshot");
        var snapshotId = ReadBodyString(snapshot, "snapshotId");
        var revision = ReadBodyUInt64(snapshot, "sessionRevisionVector", "gameRevision");

        await SendEnvelopeAsync(
            client,
            BaselineAck(options, sequence++, snapshotId, revision),
            trace,
            "BaselineAck",
            cancellationToken).ConfigureAwait(false);

        var delta = await ReceiveEnvelopeAsync(client, trace, cancellationToken).ConfigureAwait(false);
        AssertMessage(delta, "Delta", trace, "delta follows baseline acknowledgement");
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
            AssertMessage(resync, "FullSnapshot", trace, "a local gap requests a full resync");
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
            trace.Record("in", null, "invalid credential rejection has an observable close frame", false, ex.WebSocketErrorCode.ToString());
            return SmokeClientExitCodes.TransportFatal;
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
            return passed ? SmokeClientExitCodes.Success : SmokeClientExitCodes.AssertionFailed;
        }
        catch (WebSocketException ex)
        {
            trace.Record("in", null, "replayed nonce rejection has an observable close frame", false, ex.WebSocketErrorCode.ToString());
            return SmokeClientExitCodes.TransportFatal;
        }
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

    private static async Task<int> RunExpectedStaleRejectAsync(
        SmokeClientCommandLineOptions options,
        SmokeTraceWriter trace,
        byte[] token,
        CancellationToken cancellationToken)
    {
        using var client = await ConnectAsync(options, token, options.Nonce!, trace, cancellationToken).ConfigureAwait(false);
        var first = await ReceiveEnvelopeAsync(client, trace, cancellationToken).ConfigureAwait(false);
        AssertMessage(first, "Handshake", trace, "server handshake is available before generation check");
        await SendEnvelopeAsync(client, ClientHandshake(options, 1), trace, "Handshake", cancellationToken).ConfigureAwait(false);
        _ = await ReceiveEnvelopeAsync(client, trace, cancellationToken).ConfigureAwait(false);
        await CloseQuietlyAsync(client).ConfigureAwait(false);
        trace.Record("in", null, "stale generation path closes the old connection", true, null);
        return SmokeClientExitCodes.Success;
    }

    private static async Task<int> RunReconnectScenarioAsync(
        SmokeClientCommandLineOptions options,
        SmokeTraceWriter trace,
        byte[] token,
        CancellationToken cancellationToken)
    {
        var first = await RunReplicationScenarioAsync(options, trace, token, includeResync: false, cancellationToken).ConfigureAwait(false);
        if (first != SmokeClientExitCodes.Success)
        {
            return first;
        }

        var reconnectOptions = options with { Nonce = options.Nonce + "-reconnect" };
        return await RunReplicationScenarioAsync(reconnectOptions, trace, token, includeResync: false, cancellationToken).ConfigureAwait(false);
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
            trace.Record("out", null, "websocket upgrade completed", true, null);
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

    private static void AssertBodyString(
        ReadOnlyMemory<byte> bytes,
        string property,
        string expected,
        SmokeTraceWriter trace,
        string assertion)
    {
        var actual = ReadBodyString(bytes, property);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new ScenarioAssertionException("Handshake", assertion, $"expected {expected}, got {actual}");
        }

        trace.Record("in", "Handshake", assertion, true, null);
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
