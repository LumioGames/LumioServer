using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.MvpHost.App;
using Lumio.Server.MvpHost.SmokeClient;
using Xunit;

namespace Lumio.Server.MvpHost.Integration.Tests;

public sealed class HostSmokeClientAcceptanceTests : IAsyncLifetime
{
    // Cold process startup can contend with antivirus and parallel test-host IO
    // on Windows. The protocol assertions retain their own bounded waits.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ScenarioTimeout = TimeSpan.FromSeconds(90);
    private static readonly HashSet<string> ClientTraceKeys = new(StringComparer.Ordinal)
    {
        "step", "direction", "messageType", "assertion", "passed", "detail",
    };
    private static readonly HashSet<string> AllowedClientUplink = new(StringComparer.Ordinal)
    {
        "Handshake", "BaselineAck", "DeltaAck", "ResyncRequest",
    };
    private static readonly string[] AdmissionEffects =
    {
        "ReadGate", "Authenticate", "MatchExactRelease", "ReserveSlot",
        "CommitSlot", "CreateSession", "BindConnection", "StartReplication",
    };
    private static readonly ConcurrentDictionary<int, Process> LiveChildProcesses = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        var live = LiveChildProcesses.Values
            .Where(process => !ChildProcess.HasExited(process))
            .Select(process => process.Id)
            .OrderBy(id => id)
            .ToArray();
        Assert.True(live.Length == 0, $"integration child processes are still alive: {string.Join(",", live)}");
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TwoIndependentClientsCompleteMutationAcceptanceAtProcessBoundary()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(ScenarioTimeout);

        await using var host = await MvpHostProcess.StartAsync(timeout.Token);
        await using var first = host.StartSmokeClient("integration-client-one", "a1-alpha");
        await using var second = host.StartSmokeClient("integration-client-two", "a1-alpha");

        await Task.WhenAll(
            WaitForTraceMessageAsync(first.TracePath, "BaselineAck", timeout.Token),
            WaitForTraceMessageAsync(second.TracePath, "BaselineAck", timeout.Token));

        var mutation = await host.InjectWorldMutationAsync(
            SessionIdForNonce(first.Nonce),
            new byte[] { 1, 2 },
            timeout.Token);
        Assert.True(mutation.Accepted, mutation.StableErrorId ?? "mutation was rejected");

        await Task.WhenAll(
            WaitForTraceMessageAsync(first.TracePath, "DeltaAck", timeout.Token),
            WaitForTraceMessageAsync(second.TracePath, "DeltaAck", timeout.Token));

        await Task.WhenAll(
            first.WaitForExitAsync(timeout.Token),
            second.WaitForExitAsync(timeout.Token));

        await QuiesceHostAsync(host, timeout.Token);

        Assert.Equal(SmokeClientExitCodes.Success, first.ExitCode);
        Assert.Equal(SmokeClientExitCodes.Success, second.ExitCode);

        AssertClientTrace(
            first.TracePath,
            "integration-client-one",
            "Handshake",
            "FullSnapshot",
            "BaselineAck",
            "Delta",
            "DeltaAck");
        AssertClientTrace(
            second.TracePath,
            "integration-client-two",
            "Handshake",
            "FullSnapshot",
            "BaselineAck",
            "Delta",
            "DeltaAck");
        AssertServerTrace(host.ServerTracePath);
    }

    [Fact]
    public async Task HostReadinessExposesLoopbackControlEndpointAtProcessBoundary()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(StartupTimeout);

        await using var host = await MvpHostProcess.StartAsync(timeout.Token);

        Assert.StartsWith("ws://127.0.0.1:", host.Ready.ListenUri, StringComparison.Ordinal);
        Assert.NotEqual(0, host.ListenPort);
        Assert.NotEqual("-", host.Ready.TestControlUri);
        Assert.Equal("http", host.ControlUri.Scheme);
        Assert.True(host.ControlUri.IsLoopback);
        Assert.NotEqual(0, host.ControlUri.Port);
        await QuiesceHostAsync(host, timeout.Token);
    }

    [Fact]
    public async Task ProcessesAreRealSubprocessesTest()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(ScenarioTimeout);

        await using var host = await MvpHostProcess.StartAsync(timeout.Token);
        await using var client = host.StartSmokeClient("pid-bad-token-a", "bad-token");

        Assert.NotEqual(Environment.ProcessId, host.ProcessId);
        Assert.NotEqual(Environment.ProcessId, client.ProcessId);
        Assert.NotEqual(host.ProcessId, client.ProcessId);
        await client.WaitForExitAsync(timeout.Token);
        Assert.Equal(SmokeClientExitCodes.Success, client.ExitCode);
        await QuiesceHostAsync(host, timeout.Token);
    }

    [Fact]
    public async Task InvalidCredentialAndReplayAreRejectedWithServerAuditEvidence()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(ScenarioTimeout);

        await using var host = await MvpHostProcess.StartAsync(timeout.Token);
        await using var badToken = host.StartSmokeClient("audit-bad-token", "bad-token");
        await badToken.WaitForExitAsync(timeout.Token);
        await using var replay = host.StartSmokeClient("audit-replay", "replay-nonce");
        await replay.WaitForExitAsync(timeout.Token);
        await QuiesceHostAsync(host, timeout.Token);

        Assert.Equal(SmokeClientExitCodes.Success, badToken.ExitCode);
        Assert.Equal(SmokeClientExitCodes.Success, replay.ExitCode);
        AssertClientTraceShape(badToken.TracePath);
        AssertClientTraceShape(replay.TracePath);
        AssertAuthenticationAuditTrace(host.ServerTracePath);
    }

    [Fact]
    public async Task SameConnectionGapRequestsFullResyncWithoutRehandshake()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(ScenarioTimeout);

        await using var host = await MvpHostProcess.StartAsync(timeout.Token);
        await using var client = host.StartSmokeClient("gap-resync-a", "gap-resync");
        await WaitForTraceMessageAsync(client.TracePath, "BaselineAck", timeout.Token);
        var mutation = await host.InjectWorldMutationAsync(
            SessionIdForNonce(client.Nonce),
            new byte[] { 7, 8 },
            timeout.Token);
        Assert.True(mutation.Accepted, mutation.StableErrorId ?? "mutation was rejected");
        await client.WaitForExitAsync(timeout.Token);
        await QuiesceHostAsync(host, timeout.Token);

        Assert.Equal(SmokeClientExitCodes.Success, client.ExitCode);
        Assert.True(TryReadTrace(client.TracePath, out var records));
        Assert.Contains(records, record => record.Direction == "out" && record.MessageType == "ResyncRequest");
        Assert.DoesNotContain(records, record => record.Direction == "in" && record.MessageType == "ResyncRequest");
        Assert.True(
            records.Count(record => record.Direction == "in" && record.Assertion == "server sends the first handshake") == 1,
            "same-connection resync must not repeat the handshake");
        Assert.True(
            records.Count(record => record.Direction == "in" && record.Assertion == "admission or resync starts with a full snapshot") >= 2,
            "gap resync must receive a fresh full snapshot");
        AssertNoGameplayUplink(records);
    }

    [Fact]
    public async Task ReleaseAndSizeRejectionScenariosRunAtProcessBoundary()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(ScenarioTimeout);

        await using var host = await MvpHostProcess.StartAsync(timeout.Token);
        await using var release = host.StartSmokeClient("reject-release", "release-mismatch");
        await using var oversize = host.StartSmokeClient("reject-oversize", "oversize-message");
        await Task.WhenAll(
            release.WaitForExitAsync(timeout.Token),
            oversize.WaitForExitAsync(timeout.Token));
        await QuiesceHostAsync(host, timeout.Token);

        Assert.True(
            release.ExitCode == SmokeClientExitCodes.Success,
            DescribeClientTrace(release.TracePath));
        Assert.True(
            oversize.ExitCode == SmokeClientExitCodes.Success,
            DescribeClientTrace(oversize.TracePath));
        Assert.True(TryReadTrace(release.TracePath, out var releaseRecords));
        Assert.Contains(releaseRecords, record =>
            record.MessageType == "Error"
            && record.Assertion == "error envelope carries the expected registered rejection");
        Assert.True(TryReadTrace(oversize.TracePath, out var oversizeRecords));
        Assert.Contains(oversizeRecords, record =>
            record.Assertion == "oversize message is rejected before application dispatch"
            && record.Passed);
    }

    [Fact]
    public async Task ReconnectScenarioCompletesAfterAnOutOfBandMutation()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));

        await using var host = await MvpHostProcess.StartAsync(timeout.Token);
        await using var client = host.StartSmokeClient("reconnect-aa", "reconnect");

        await WaitForTraceMessageAsync(client.TracePath, "BaselineAck", timeout.Token);
        var mutation = await host.InjectWorldMutationAsync(
            SessionIdForNonce(client.Nonce),
            new byte[] { 3, 4 },
            timeout.Token);
        Assert.True(mutation.Accepted, mutation.StableErrorId ?? "mutation was rejected");
        await WaitForTraceMessageAsync(client.TracePath, "DeltaAck", timeout.Token);
        var kick = await host.KickAsync(
            SessionIdForNonce(client.Nonce),
            "MaintenanceKick",
            timeout.Token);
        Assert.True(kick.Accepted, kick.StableErrorId ?? "maintenance kick was rejected");
        await WaitForSessionStateAsync(
            host.ServerTracePath,
            "ReconnectWindow",
            timeout.Token);
        var reconnectMutation = await host.InjectWorldMutationAsync(
            SessionIdForNonce(client.Nonce),
            new byte[] { 5, 6 },
            timeout.Token);
        Assert.True(
            reconnectMutation.Accepted,
            reconnectMutation.StableErrorId ?? "reconnect mutation was rejected");
        await WaitForAuthorityRevisionAsync(host.ServerTracePath, 2, timeout.Token);
        await client.WaitForExitAsync(timeout.Token);
        await QuiesceHostAsync(host, timeout.Token);

        Assert.Equal(SmokeClientExitCodes.Success, client.ExitCode);
        AssertClientTrace(
            client.TracePath,
            "integration-reconnect",
            "Handshake",
            "FullSnapshot",
            "BaselineAck",
            "Delta",
            "DeltaAck");
        AssertReconnectClientTrace(client.TracePath);
        AssertReconnectServerTrace(host.ServerTracePath);
    }

    [Fact]
    public async Task ReconnectWindowExpiryRejectsALosingReconnectWithStableError()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(ScenarioTimeout);

        await using var host = await MvpHostProcess.StartAsync(
            timeout.Token,
            reconnectWindowSeconds: 1);
        await using var client = host.StartSmokeClient("expiry-win-expired", "a1-alpha");
        await WaitForTraceMessageAsync(client.TracePath, "BaselineAck", timeout.Token);
        var mutation = await host.InjectWorldMutationAsync(
            SessionIdForNonce(client.Nonce),
            new byte[] { 9, 10 },
            timeout.Token);
        Assert.True(mutation.Accepted, mutation.StableErrorId ?? "mutation was rejected");
        await client.WaitForExitAsync(timeout.Token);
        await WaitForSessionStateAsync(host.ServerTracePath, "Expired", timeout.Token);
        await using var losingReconnect = host.StartSmokeClient(
            client.Nonce + "-reconnect",
            "a1-alpha");
        await losingReconnect.WaitForExitAsync(timeout.Token);
        await QuiesceHostAsync(host, timeout.Token);

        Assert.Equal(SmokeClientExitCodes.Success, client.ExitCode);
        Assert.Equal(SmokeClientExitCodes.Success, losingReconnect.ExitCode);
        AssertClientTraceShape(losingReconnect.TracePath);
        Assert.True(TryReadTrace(losingReconnect.TracePath, out var losingTrace));
        Assert.Contains(losingTrace, record =>
            record.MessageType == "Error"
            && record.Assertion == "error envelope carries the expected registered rejection"
            && record.Passed);
        AssertFinalSessionState(host.ServerTracePath, SessionIdForNonce(client.Nonce), "Expired");
    }

    [Fact]
    public async Task BeginDrainQuiescesInOrderAndExitsHostNormally()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        await using var host = await MvpHostProcess.StartAsync(timeout.Token);
        var result = await host.BeginDrainAsync(0, timeout.Token);
        Assert.True(result.Accepted, result.StableErrorId ?? "begin drain was rejected");

        await host.WaitForExitAsync(timeout.Token);

        Assert.Equal(0, host.ExitCode);
        AssertQuiesceTrace(host.ServerTracePath);
    }

    [Theory]
    [InlineData("TERM")]
    [InlineData("INT")]
    public async Task PosixSignalRoutesAChildProcessThroughOrderedQuiesce(string signal)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX signal delivery is not exposed by the Windows test host");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        await using var host = await MvpHostProcess.StartAsync(timeout.Token);
        using var sender = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "kill",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        sender.StartInfo.ArgumentList.Add($"-{signal}");
        sender.StartInfo.ArgumentList.Add(host.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(sender.Start());
        await sender.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, sender.ExitCode);

        await host.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, host.ExitCode);
        AssertQuiesceTrace(host.ServerTracePath);
    }

    private static async Task QuiesceHostAsync(
        MvpHostProcess host,
        CancellationToken cancellationToken)
    {
        var result = await host.BeginDrainAsync(0, cancellationToken).ConfigureAwait(false);
        Assert.True(result.Accepted, result.StableErrorId ?? "begin drain was rejected");
        await host.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        Assert.Equal(0, host.ExitCode);
        AssertQuiesceTrace(host.ServerTracePath);
    }

    private static void AssertClientTraceShape(string path)
    {
        var expectedStep = 1;
        foreach (var line in ReadSharedLines(path))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var keys = root.EnumerateObject()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert.Equal(ClientTraceKeys, keys);
            Assert.Equal(expectedStep++, root.GetProperty("step").GetInt32());
            Assert.True(root.GetProperty("direction").GetString() is "in" or "out");
            Assert.True(
                root.GetProperty("passed").GetBoolean(),
                root.GetProperty("assertion").GetString());
        }

        Assert.True(expectedStep > 1, $"client trace is empty: {path}");
    }

    private static string DescribeClientTrace(string path)
        => TryReadTrace(path, out var records)
            ? string.Join(
                " | ",
                records.Select(record =>
                    $"{record.Step}:{record.Assertion}:{record.Passed}:{record.Detail}"))
            : $"client trace is missing or invalid: {path}";

    private static void AssertNoGameplayUplink(IEnumerable<ClientTraceRecord> records)
    {
        Assert.DoesNotContain(records, record =>
            record.Direction == "out"
            && record.MessageType is not null
            && !AllowedClientUplink.Contains(record.MessageType));
    }

    private static void AssertAuthenticationAuditTrace(string path)
    {
        var audits = new List<JsonElement>();
        foreach (var line in ReadSharedLines(path))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("kind").GetString() == "audit")
            {
                audits.Add(root.Clone());
            }
        }

        Assert.NotEmpty(audits);
        Assert.Contains(audits, audit =>
            audit.GetProperty("category").GetString() == "Audit"
            && audit.GetProperty("severity").GetString() == "Warn"
            && audit.GetProperty("scope").GetString() == "Release"
            && audit.GetProperty("releasePoolId").ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(audit.GetProperty("releasePoolId").GetString())
            && audit.GetProperty("sessionId").ValueKind == JsonValueKind.Null
            && audit.GetProperty("eventId").ValueKind == JsonValueKind.String
            && System.Text.RegularExpressions.Regex.IsMatch(
                audit.GetProperty("eventId").GetString()!,
                "^[A-Za-z][A-Za-z0-9._:-]{0,127}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            && audit.GetProperty("timestamp").ValueKind == JsonValueKind.String
            && System.Text.RegularExpressions.Regex.IsMatch(
                audit.GetProperty("timestamp").GetString()!,
                "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,9})?Z$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant));
        Assert.Contains(audits, audit =>
            audit.GetProperty("reasonCode").ValueKind == JsonValueKind.String
            && audit.GetProperty("reasonCode").GetString() == "SessionAntiReplay");
    }

    private static async Task WaitForTraceMessageAsync(
        string path,
        string messageType,
        CancellationToken cancellationToken)
    {
        await WaitForTraceConditionAsync(
            path,
            () => TraceContainsPassedMessageOrThrows(path, messageType),
            cancellationToken).ConfigureAwait(false);

    }

    private static bool TraceContainsPassedMessageOrThrows(string path, string messageType)
    {
        if (!TryReadTrace(path, out var records))
        {
            return false;
        }

        var failed = records
            .Where(record => !record.Passed)
            .Select(record => (ClientTraceRecord?)record)
            .FirstOrDefault();
        if (failed is { } failure)
        {
            throw new Xunit.Sdk.XunitException(
                $"SmokeClient assertion failed before {messageType}: {failure.Assertion}");
        }

        return records.Any(record =>
            record.MessageType is not null
            && record.MessageType.Equals(messageType, StringComparison.Ordinal)
            && record.Passed);
    }

    private static async Task WaitForAuthorityRevisionAsync(
        string path,
        ulong revision,
        CancellationToken cancellationToken)
    {
        await WaitForTraceConditionAsync(
            path,
            () => TraceHasAuthorityRevision(path, revision),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForSessionStateAsync(
        string path,
        string state,
        CancellationToken cancellationToken)
    {
        await WaitForTraceConditionAsync(
            path,
            () => TraceHasSessionState(path, state),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForTraceConditionAsync(
        string path,
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new Xunit.Sdk.XunitException($"Trace timer stopped while waiting for {path}");
            }
        }
    }

    private static bool TraceHasAuthorityRevision(string path, ulong revision)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            foreach (var line in ReadSharedLines(path))
            {
                using var document = JsonDocument.Parse(line);
                var value = document.RootElement.GetProperty("authorityRevision");
                if (value.ValueKind == JsonValueKind.Number && value.GetUInt64() >= revision)
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool TraceHasSessionState(string path, string state)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            foreach (var line in ReadSharedLines(path))
            {
                using var document = JsonDocument.Parse(line);
                var value = document.RootElement.GetProperty("sessionState");
                if (value.ValueKind == JsonValueKind.String
                    && value.GetString()!.Equals(state, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static void AssertClientTrace(
        string path,
        string clientName,
        params string[] requiredMessageTypes)
    {
        Assert.True(File.Exists(path), $"{clientName} trace is missing: {path}");
        Assert.True(TryReadTrace(path, out var records), $"{clientName} trace is not valid JSONL");
        Assert.NotEmpty(records);

        var expectedStep = 1;
        foreach (var record in records)
        {
            Assert.Equal(expectedStep++, record.Step);
            Assert.True(record.Passed, $"{clientName} failed assertion: {record.Assertion}");
        }

        foreach (var messageType in requiredMessageTypes)
        {
            Assert.Contains(
                records,
                record => record.MessageType is not null
                    && record.MessageType.Equals(messageType, StringComparison.Ordinal)
                    && record.Passed);
        }

        var lastIndex = -1;
        foreach (var messageType in requiredMessageTypes)
        {
            var nextIndex = records.FindIndex(
                lastIndex + 1,
                record => record.MessageType is not null
                    && record.MessageType.Equals(messageType, StringComparison.Ordinal)
                    && record.Passed);
            Assert.True(
                nextIndex > lastIndex,
                $"{clientName} trace message order is invalid around {messageType}");
            lastIndex = nextIndex;
        }
    }

    private static void AssertServerTrace(string path)
    {
        Assert.True(File.Exists(path), $"server trace is missing: {path}");
        var lines = ReadSharedLines(path);
        Assert.NotEmpty(lines);

        var expectedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "seq", "kind", "eventId", "timestamp", "category", "severity", "scope",
            "releasePoolId", "sessionId", "reasonCode", "admissionAttemptId", "effect",
            "sessionState", "authorityRevision", "slotEpoch", "connectionEpoch", "grantEpoch",
        };
        var effects = new HashSet<string>(StringComparer.Ordinal);
        var admissionEffects = new Dictionary<ulong, List<string>>();
        var activeStates = 0;
        ulong previousSequence = 0;
        var first = true;
        var hasRevisionOne = false;

        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var keys = root.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(expectedKeys, keys);

            var sequence = root.GetProperty("seq").GetUInt64();
            if (!first)
            {
                Assert.True(sequence > previousSequence, "server trace sequence must be monotonic");
            }

            previousSequence = sequence;
            first = false;

            if (root.GetProperty("effect").ValueKind == JsonValueKind.String)
            {
                var effect = root.GetProperty("effect").GetString()!;
                effects.Add(effect);
                if (root.GetProperty("admissionAttemptId").ValueKind == JsonValueKind.Number)
                {
                    var attempt = root.GetProperty("admissionAttemptId").GetUInt64();
                    if (!admissionEffects.TryGetValue(attempt, out var ordered))
                    {
                        ordered = new List<string>();
                        admissionEffects.Add(attempt, ordered);
                    }

                    ordered.Add(effect);
                }
            }

            if (root.GetProperty("sessionState").GetString() == "Active")
            {
                activeStates++;
            }

            if (root.GetProperty("authorityRevision").ValueKind == JsonValueKind.Number
                && root.GetProperty("authorityRevision").GetUInt64() == 1)
            {
                hasRevisionOne = true;
            }
        }

        Assert.True(activeStates >= 2, "both independent clients must reach Active");
        Assert.True(hasRevisionOne, "mutation must advance the authority revision to one");
        Assert.Contains("ReadGate", effects);
        Assert.Contains("Authenticate", effects);
        Assert.Contains("MatchExactRelease", effects);
        Assert.Contains("ReserveSlot", effects);
        Assert.Contains("CommitSlot", effects);
        Assert.Contains("CreateSession", effects);
        Assert.Contains("BindConnection", effects);
        Assert.Contains("StartReplication", effects);
        Assert.DoesNotContain("TransportAccepted", effects);
        Assert.DoesNotContain("ServerHandshakeQueued", effects);
        Assert.DoesNotContain("ClientHandshakeReceived", effects);
        Assert.DoesNotContain("IngressReady", effects);
        Assert.DoesNotContain("TransportClosed", effects);
        Assert.DoesNotContain("TransportFaulted", effects);
        Assert.NotEmpty(admissionEffects);
        Assert.All(admissionEffects.Values, ordered => Assert.Equal(AdmissionEffects, ordered));
    }

    private static void AssertReconnectClientTrace(string path)
    {
        Assert.True(TryReadTrace(path, out var records));
        Assert.True(
            records.Count(record => record.MessageType == "Handshake") >= 2,
            "reconnect must perform a second full handshake");
        Assert.True(
            records.Count(record => record.MessageType == "FullSnapshot") >= 2,
            "reconnect must receive a new full snapshot");
        Assert.True(
            records.Count(record => record.MessageType == "BaselineAck") >= 2,
            "reconnect must acknowledge the new baseline");
        Assert.Contains(records, record =>
            record.MessageType == "FullSnapshot"
            && record.Assertion == "reconnect full snapshot is strictly newer than the last delta"
            && record.Passed);
        var kickIndex = records.FindIndex(record =>
            record.MessageType == "MaintenanceKick"
            && record.Assertion == "maintenance kick envelope precedes close");
        var reconnectUpgradeIndex = records.FindIndex(
            kickIndex + 1,
            record => record.Assertion == "websocket upgrade returned HTTP 101");
        Assert.True(kickIndex >= 0 && reconnectUpgradeIndex > kickIndex);
        AssertNoGameplayUplink(records);
    }

    private static void AssertReconnectServerTrace(string path)
    {
        var lines = ReadSharedLines(path);
        var syncingRevisions = new List<ulong>();
        var grantEpochs = new List<ulong>();
        var activeStates = 0;
        var highestMutationRevision = 0UL;

        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("authorityRevision").ValueKind == JsonValueKind.Number)
            {
                highestMutationRevision = Math.Max(
                    highestMutationRevision,
                    root.GetProperty("authorityRevision").GetUInt64());
            }

            if (root.GetProperty("kind").GetString() == "state"
                && root.GetProperty("sessionState").GetString() == "Syncing"
                && root.GetProperty("authorityRevision").ValueKind == JsonValueKind.Number)
            {
                syncingRevisions.Add(root.GetProperty("authorityRevision").GetUInt64());
            }

            if (root.GetProperty("kind").GetString() == "state"
                && root.GetProperty("grantEpoch").ValueKind == JsonValueKind.Number)
            {
                grantEpochs.Add(root.GetProperty("grantEpoch").GetUInt64());
            }

            if (root.GetProperty("kind").GetString() == "state"
                && root.GetProperty("sessionState").GetString() == "Active")
            {
                activeStates++;
            }
        }

        Assert.True(highestMutationRevision >= 2, "both real mutations must advance authority revision");
        Assert.True(syncingRevisions.Count >= 2, "server must trace both initial and reconnect sync states");
        Assert.True(
            syncingRevisions[^1] > 1,
            "reconnect FullSnapshot must be strictly newer than the last Delta");
        var distinctGrantEpochs = grantEpochs.Distinct().ToArray();
        Assert.True(distinctGrantEpochs.Length >= 2, "reconnect must derive a new grant epoch");
        Assert.True(distinctGrantEpochs[^1] > distinctGrantEpochs[0]);
        Assert.True(activeStates >= 2, "initial and reconnected sessions must both reach Active");
    }

    private static void AssertFinalSessionState(string path, string sessionId, string expected)
    {
        string? final = null;
        foreach (var line in ReadSharedLines(path))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("kind").GetString() == "state"
                && root.GetProperty("sessionId").ValueKind == JsonValueKind.String
                && root.GetProperty("sessionId").GetString() == sessionId
                && root.GetProperty("sessionState").ValueKind == JsonValueKind.String)
            {
                final = root.GetProperty("sessionState").GetString();
            }
        }

        Assert.Equal(expected, final);
    }

    private static void AssertQuiesceTrace(string path)
    {
        var required = new[] { "AdmissionClosed", "Drained", "SnapshotCut", "Stopped" };
        var observed = new List<string>();
        foreach (var line in ReadSharedLines(path))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("kind").GetString() != "ack"
                || root.GetProperty("effect").ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var effect = root.GetProperty("effect").GetString()!;
            if (!required.Contains(effect, StringComparer.Ordinal))
            {
                continue;
            }

            Assert.Equal(JsonValueKind.Number, root.GetProperty("slotEpoch").ValueKind);
            observed.Add(effect);
        }

        Assert.Equal(required, observed);
    }

    private static bool TryReadTrace(string path, out List<ClientTraceRecord> records)
    {
        records = new List<ClientTraceRecord>();
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            foreach (var line in ReadSharedLines(path))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                records.Add(new ClientTraceRecord(
                    root.GetProperty("step").GetInt32(),
                    root.GetProperty("direction").GetString() ?? string.Empty,
                    root.GetProperty("messageType").ValueKind == JsonValueKind.Null
                        ? null
                        : root.GetProperty("messageType").GetString(),
                    root.GetProperty("assertion").GetString() ?? string.Empty,
                    root.GetProperty("passed").GetBoolean(),
                    root.GetProperty("detail").ValueKind == JsonValueKind.Null
                        ? null
                        : root.GetProperty("detail").GetString()));
            }

            return true;
        }
        catch (IOException)
        {
            records.Clear();
            return false;
        }
        catch (JsonException)
        {
            records.Clear();
            return false;
        }
    }

    private static string[] ReadSharedLines(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                lines.Add(line);
            }
        }

        return lines.ToArray();
    }

    private static string SessionIdForNonce(string nonce)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(nonce));
        return $"smoke-{Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    private readonly record struct ClientTraceRecord(
        int Step,
        string Direction,
        string? MessageType,
        string Assertion,
        bool Passed,
        string? Detail);

    private sealed class MvpHostProcess : IAsyncDisposable
    {
        private readonly string root;
        private readonly ChildProcess process;
        private bool disposed;

        private MvpHostProcess(
            string root,
            ChildProcess process,
            HostReadyLine ready,
            Uri controlUri,
            string serverTracePath)
        {
            this.root = root;
            this.process = process;
            Ready = ready;
            ControlUri = controlUri;
            ServerTracePath = serverTracePath;
        }

        internal HostReadyLine Ready { get; }

        internal Uri ControlUri { get; }

        internal string ServerTracePath { get; }

        internal int ListenPort => new Uri(Ready.ListenUri).Port;

        internal int ExitCode => process.ExitCode;

        internal int ProcessId => process.ProcessId;

        internal static async Task<MvpHostProcess> StartAsync(
            CancellationToken cancellationToken,
            int reconnectWindowSeconds = 10)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"lumio-mvp-integration-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var secretPath = Path.Combine(root, "secret.bin");
            var serverTracePath = Path.Combine(root, "server.jsonl");
            File.WriteAllBytes(secretPath, Encoding.UTF8.GetBytes("integration-secret"));

            ChildProcess? process = null;
            try
            {
                process = ChildProcess.Start(
                    typeof(App.Program).Assembly.Location,
                    "--listen", "ws://127.0.0.1:0",
                    "--allow-insecure-loopback",
                    "--shared-secret-file", secretPath,
                    "--reconnect-window-seconds", reconnectWindowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--enable-test-control",
                    "--test-control-listen", "http://127.0.0.1:0",
                    "--audit-trace-file", serverTracePath);
                var ready = await process.ReadyAsync(cancellationToken).ConfigureAwait(false);
                if (!Uri.TryCreate(ready.TestControlUri, UriKind.Absolute, out var controlUri)
                    || controlUri is null)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"host readiness did not expose a control URI: {ready}");
                }

                return new MvpHostProcess(root, process, ready, controlUri, serverTracePath);
            }
            catch
            {
                if (process is not null)
                {
                    await process.DisposeAsync().ConfigureAwait(false);
                }

                TryDeleteDirectory(root);
                throw;
            }
        }

        internal ChildProcess StartSmokeClient(string nonce, string scenario)
        {
            var tracePath = Path.Combine(root, $"{nonce}.jsonl");
            return ChildProcess.Start(
                typeof(SmokeClient.Program).Assembly.Location,
                new[]
                {
                    "--endpoint", Ready.ListenUri,
                    "--token-file", Path.Combine(root, "secret.bin"),
                    "--nonce", nonce,
                    "--scenario", scenario,
                    "--trace-file", tracePath,
                },
                tracePath,
                nonce);
        }

        internal async Task<MutationResult> InjectWorldMutationAsync(
            string sessionId,
            ReadOnlyMemory<byte> command,
            CancellationToken cancellationToken)
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri(ControlUri.ToString().TrimEnd('/') + "/"),
                Timeout = ScenarioTimeout,
            };
            var payload = JsonSerializer.Serialize(new
            {
                sessionId,
                opaqueCommandBase64 = Convert.ToBase64String(command.ToArray()),
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(
                "test-control/inject-world-mutation",
                content,
                cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Assert.True(response.IsSuccessStatusCode, body);

            using var document = JsonDocument.Parse(body);
            var accepted = document.RootElement.GetProperty("accepted").GetBoolean();
            var stableErrorId = document.RootElement.GetProperty("stableErrorId").ValueKind
                == JsonValueKind.Null
                ? null
                : document.RootElement.GetProperty("stableErrorId").GetString();
            return new MutationResult(accepted, stableErrorId);
        }

        internal Task<MutationResult> KickAsync(
            string sessionId,
            string reasonCode,
            CancellationToken cancellationToken)
            => PostControlAsync(
                "test-control/kick",
                new { sessionId, reasonCode },
                cancellationToken);

        internal Task<MutationResult> BeginDrainAsync(
            int graceSeconds,
            CancellationToken cancellationToken)
            => PostControlAsync(
                "test-control/begin-drain",
                new { graceSeconds },
                cancellationToken);

        private async Task<MutationResult> PostControlAsync(
            string route,
            object payload,
            CancellationToken cancellationToken)
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri(ControlUri.ToString().TrimEnd('/') + "/"),
                Timeout = ScenarioTimeout,
            };
            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");
            using var response = await client.PostAsync(route, content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Assert.True(response.IsSuccessStatusCode, body);

            using var document = JsonDocument.Parse(body);
            return new MutationResult(
                document.RootElement.GetProperty("accepted").GetBoolean(),
                document.RootElement.GetProperty("stableErrorId").ValueKind == JsonValueKind.Null
                    ? null
                    : document.RootElement.GetProperty("stableErrorId").GetString());
        }

        internal Task StopAsync(CancellationToken cancellationToken)
            => process.StopAsync(cancellationToken);

        internal Task WaitForExitAsync(CancellationToken cancellationToken)
            => process.WaitForExitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            await process.DisposeAsync().ConfigureAwait(false);
            TryDeleteDirectory(root);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Process disposal is authoritative; a transient Windows file
                // handle must not hide the assertion that already ran.
            }
            catch (UnauthorizedAccessException)
            {
                // See the bounded cleanup note above.
            }
        }
    }

    private readonly record struct MutationResult(bool Accepted, string? StableErrorId);

    private sealed class ChildProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task<string> standardError;
        private readonly string? tracePath;
        private readonly string? nonce;
        private Task<string>? standardOutput;
        private bool disposed;

        private ChildProcess(
            Process process,
            Task<string> standardError,
            string? tracePath,
            string? nonce)
        {
            this.process = process;
            this.standardError = standardError;
            this.tracePath = tracePath;
            this.nonce = nonce;
        }

        internal string TracePath => tracePath ?? throw new InvalidOperationException("trace path is not configured");

        internal string Nonce => nonce ?? throw new InvalidOperationException("nonce is not configured");

        internal int ExitCode => process.ExitCode;

        internal int ProcessId => process.Id;

        internal static ChildProcess Start(
            string assembly,
            params string[] arguments)
            => Start(assembly, arguments, null, null);

        internal static ChildProcess Start(
            string assembly,
            string[] arguments,
            string? tracePath,
            string? nonce)
        {
            var directory = Path.GetDirectoryName(assembly)
                ?? throw new InvalidOperationException($"assembly directory is missing: {assembly}");
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    WorkingDirectory = directory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";
            process.StartInfo.ArgumentList.Add(assembly);
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException($"could not start child process: {assembly}");
            }

            LiveChildProcesses[process.Id] = process;

            var child = new ChildProcess(
                process,
                process.StandardError.ReadToEndAsync(),
                tracePath,
                nonce);
            if (tracePath is not null)
            {
                child.standardOutput = process.StandardOutput.ReadToEndAsync();
            }

            return child;
        }

        internal async Task<HostReadyLine> ReadyAsync(CancellationToken cancellationToken)
        {
            var deadline = Stopwatch.GetTimestamp()
                + (long)(Stopwatch.Frequency * StartupTimeout.TotalSeconds);
            while (Stopwatch.GetTimestamp() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken)
                    .AsTask()
                    .WaitAsync(StartupTimeout, cancellationToken)
                    .ConfigureAwait(false);
                if (line is null)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"host exited before readiness; exit={process.ExitCode}; stderr={await ReadErrorAsync()}");
                }

                if (HostReadyLine.TryParse(line, out var ready))
                {
                    standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
                    return ready;
                }
            }

            throw new Xunit.Sdk.XunitException(
                $"host readiness timed out; stderr={await ReadErrorAsync()}");
        }

        internal async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            MarkExited(process);
            await AwaitOutputAsync(cancellationToken).ConfigureAwait(false);
        }

        internal async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process may have exited between the state check and Kill.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The process may have exited between the state check and Kill.
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            MarkExited(process);
            await AwaitOutputAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                await StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (!HasExited(process))
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(5))
                        .ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
            {
                // No process handle remains.
            }

            if (!HasExited(process))
            {
                throw new Xunit.Sdk.XunitException(
                    $"child process {process.Id} remained alive after bounded cleanup");
            }

            MarkExited(process);
            try
            {
                await AwaitOutputAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Diagnostics are best effort during teardown.
            }

            process.Dispose();
        }

        internal static bool HasExited(Process candidate)
        {
            try
            {
                return candidate.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        private static void MarkExited(Process candidate)
            => LiveChildProcesses.TryRemove(candidate.Id, out _);

        private async Task AwaitOutputAsync(CancellationToken cancellationToken)
        {
            if (standardOutput is not null)
            {
                _ = await standardOutput.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false);
            }

            _ = await standardError.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<string> ReadErrorAsync()
        {
            try
            {
                var value = await standardError.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                return value.Length <= 2_000 ? value : value[..2_000];
            }
            catch (TimeoutException)
            {
                return "<stderr pending>";
            }
        }
    }
}
