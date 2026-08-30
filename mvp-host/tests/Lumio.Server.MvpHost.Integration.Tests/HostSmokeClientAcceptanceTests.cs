using System;
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

public sealed class HostSmokeClientAcceptanceTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ScenarioTimeout = TimeSpan.FromSeconds(45);

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

        // Stop the host before taking the final snapshot so the append-only
        // trace is flushed and no writer is between JSON lines on Windows.
        await host.StopAsync(timeout.Token);

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
    }

    private static async Task WaitForTraceMessageAsync(
        string path,
        string messageType,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp()
            + (long)(Stopwatch.Frequency * ScenarioTimeout.TotalSeconds);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadTrace(path, out var records)
                && records.Any(record =>
                    record.MessageType is not null
                    && record.MessageType.Equals(messageType, StringComparison.Ordinal)
                    && record.Passed))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        var current = TryReadTrace(path, out var finalRecords)
            ? string.Join(", ", finalRecords.Select(record => record.MessageType ?? record.Assertion))
            : "<trace file missing or incomplete>";
        throw new Xunit.Sdk.XunitException(
            $"Timed out waiting for {messageType} in {path}; observed: {current}");
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
                effects.Add(root.GetProperty("effect").GetString()!);
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
        Assert.Contains("CommitSlot", effects);
        Assert.Contains("CreateSession", effects);
        Assert.Contains("BindConnection", effects);
        Assert.Contains("StartReplication", effects);
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
                    root.GetProperty("messageType").ValueKind == JsonValueKind.Null
                        ? null
                        : root.GetProperty("messageType").GetString(),
                    root.GetProperty("assertion").GetString() ?? string.Empty,
                    root.GetProperty("passed").GetBoolean()));
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
        string? MessageType,
        string Assertion,
        bool Passed);

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

        internal static async Task<MvpHostProcess> StartAsync(CancellationToken cancellationToken)
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

        internal Task StopAsync(CancellationToken cancellationToken)
            => process.StopAsync(cancellationToken);

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
                // The bounded wait prevents cleanup from hanging the test host.
            }
            catch (InvalidOperationException)
            {
                // No process handle remains.
            }

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
