using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Lumio.Server.MvpHost.App.Tests;

public sealed class ProcessAndExitCodeTests
{
    [Fact]
    public void ExitCodesMatchTheProcessContract()
    {
        Assert.Equal(0, App.HostExitCodes.Success);
        Assert.Equal(64, App.HostExitCodes.InvalidArguments);
        Assert.Equal(70, App.HostExitCodes.Fatal);
        Assert.Equal(0, SmokeClient.SmokeClientExitCodes.Success);
        Assert.Equal(64, SmokeClient.SmokeClientExitCodes.InvalidArguments);
        Assert.Equal(65, SmokeClient.SmokeClientExitCodes.AssertionFailed);
        Assert.Equal(70, SmokeClient.SmokeClientExitCodes.TransportFatal);
    }

    [Fact]
    public void SmokeClientCredentialBuffersAreClearedOnScenarioExit()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(SmokeClient.SmokeClientRunner).Assembly.Location)!,
            "..", "..", "..", "..", "..", "src",
            "Lumio.Server.MvpHost.SmokeClient",
            "SmokeClientRunner.cs"));
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("Array.Clear(token)", source, StringComparison.Ordinal);
        Assert.Contains("Array.Clear(supplied)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SmokeClientInvalidArgumentsRunInARealChildProcess()
    {
        var assembly = typeof(SmokeClient.Program).Assembly.Location;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(assembly)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(assembly);
        process.StartInfo.ArgumentList.Add("--unknown");

        Assert.True(process.Start());
        Assert.NotEqual(Environment.ProcessId, process.Id);
        Assert.True(process.WaitForExit(10_000));
        Assert.Equal(SmokeClient.SmokeClientExitCodes.InvalidArguments, process.ExitCode);
        Assert.True(process.HasExited, "child process must be reaped before the test exits");
    }

    [Fact]
    public async Task StaleGenerationScenarioCannotReportSuccessWithoutWireEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lumio-stale-scenario-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var token = Path.Combine(root, "token.bin");
        var tracePath = Path.Combine(root, "trace.jsonl");
        File.WriteAllBytes(token, Encoding.UTF8.GetBytes("test-secret"));

        try
        {
            var options = new SmokeClient.SmokeClientCommandLineOptions(
                "ws://127.0.0.1:1",
                token,
                "stale-proof",
                SmokeClient.SmokeClientCommandLineOptions.DefaultProductId,
                SmokeClient.SmokeClientCommandLineOptions.DefaultGameReleaseId,
                "stale-generation",
                tracePath);
            int exitCode;
            using (var trace = new SmokeClient.SmokeTraceWriter(tracePath))
            {
                exitCode = await SmokeClient.SmokeClientRunner.RunAsync(
                    options,
                    trace,
                    TestContext.Current.CancellationToken);
            }

            Assert.Equal(SmokeClient.SmokeClientExitCodes.AssertionFailed, exitCode);
            var lines = File.ReadAllLines(tracePath);
            Assert.NotEmpty(lines);
            using var last = JsonDocument.Parse(lines[^1]);
            Assert.False(last.RootElement.GetProperty("passed").GetBoolean());
            Assert.Contains(
                "connection generation",
                last.RootElement.GetProperty("detail").GetString(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HostPrintsOneParsableReadinessLineAsARealChildProcess()
    {
        var assembly = typeof(App.Program).Assembly.Location;
        var secret = Path.Combine(Path.GetTempPath(), $"lumio-host-secret-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(secret, System.Text.Encoding.UTF8.GetBytes("test-secret"));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(assembly)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(assembly);
        process.StartInfo.ArgumentList.Add("--listen");
        process.StartInfo.ArgumentList.Add("ws://127.0.0.1:0");
        process.StartInfo.ArgumentList.Add("--allow-insecure-loopback");
        process.StartInfo.ArgumentList.Add("--shared-secret-file");
        process.StartInfo.ArgumentList.Add(secret);

        try
        {
            Assert.True(process.Start());
            var read = process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask();
            var completed = await Task.WhenAny(
                read,
                Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Same(read, completed);
            var line = await read;
            Assert.True(App.HostReadyLine.TryParse(line, out var ready), line);
            Assert.StartsWith("ws://127.0.0.1:", ready.ListenUri, StringComparison.Ordinal);
            Assert.NotEqual("0", new Uri(ready.ListenUri).Port.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            }

            File.Delete(secret);
        }
    }

    [Fact]
    public async Task HostReadinessReportsAnActualTestControlPortWhenEnabled()
    {
        var assembly = typeof(App.Program).Assembly.Location;
        var secret = Path.Combine(Path.GetTempPath(), $"lumio-host-secret-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(secret, System.Text.Encoding.UTF8.GetBytes("test-secret"));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(assembly)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in new[]
                 {
                     assembly,
                     "--listen", "ws://127.0.0.1:0",
                     "--allow-insecure-loopback",
                     "--shared-secret-file", secret,
                     "--enable-test-control",
                     "--test-control-listen", "http://127.0.0.1:0",
                 })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            Assert.True(process.Start());
            var read = process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask();
            var completed = await Task.WhenAny(
                read,
                Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Same(read, completed);
            var line = await read;
            Assert.True(App.HostReadyLine.TryParse(line, out var ready), line);
            Assert.NotEqual("-", ready.TestControlUri);
            Assert.True(Uri.TryCreate(ready.TestControlUri, UriKind.Absolute, out var control));
            Assert.NotEqual(0, control!.Port);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            }

            File.Delete(secret);
        }
    }

    [Fact]
    public async Task TwoSmokeClientsRunAsIndependentChildProcessesAgainstOneHost()
    {
        var appAssembly = typeof(App.Program).Assembly.Location;
        var smokeAssembly = typeof(SmokeClient.Program).Assembly.Location;
        var root = Path.Combine(Path.GetTempPath(), $"lumio-dual-client-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var secret = Path.Combine(root, "secret.bin");
        File.WriteAllBytes(secret, System.Text.Encoding.UTF8.GetBytes("test-secret"));
        using var host = StartChild(
            appAssembly,
            "--listen", "ws://127.0.0.1:0",
            "--allow-insecure-loopback",
            "--shared-secret-file", secret,
            "--enable-test-control",
            "--test-control-listen", "http://127.0.0.1:0",
            "--audit-trace-file", Path.Combine(root, "server.jsonl"));
        Process? first = null;
        Process? second = null;

        try
        {
            Assert.True(host.Start());
            var readyRead = host.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask();
            var completed = await Task.WhenAny(
                readyRead,
                Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Same(readyRead, completed);
            Assert.True(App.HostReadyLine.TryParse(await readyRead, out var ready));
            Assert.True(Uri.TryCreate(ready.TestControlUri, UriKind.Absolute, out var controlUri));
            Assert.NotEqual(0, controlUri!.Port);

            first = StartChild(
                smokeAssembly,
                "--endpoint", ready.ListenUri,
                "--token-file", secret,
                "--nonce", "dual-client-one",
                "--scenario", "a1-alpha",
                "--trace-file", Path.Combine(root, "client-one.jsonl"));
            second = StartChild(
                smokeAssembly,
                "--endpoint", ready.ListenUri,
                "--token-file", secret,
                "--nonce", "dual-client-two",
                "--scenario", "a1-alpha",
                "--trace-file", Path.Combine(root, "client-two.jsonl"));

            Assert.True(first!.Start());
            Assert.True(second!.Start());
            Assert.NotEqual(Environment.ProcessId, first.Id);
            Assert.NotEqual(Environment.ProcessId, second.Id);
            Assert.NotEqual(first.Id, second.Id);

            var cancellationToken = TestContext.Current.CancellationToken;
            var firstOutput = first.StandardOutput.ReadToEndAsync(cancellationToken);
            var firstError = first.StandardError.ReadToEndAsync(cancellationToken);
            var secondOutput = second.StandardOutput.ReadToEndAsync(cancellationToken);
            var secondError = second.StandardError.ReadToEndAsync(cancellationToken);

            var firstTrace = Path.Combine(root, "client-one.jsonl");
            var secondTrace = Path.Combine(root, "client-two.jsonl");
            await WaitForTraceAsync(firstTrace, "\"messageType\":\"BaselineAck\"", cancellationToken);
            await WaitForTraceAsync(secondTrace, "\"messageType\":\"BaselineAck\"", cancellationToken);

            using var http = new HttpClient { BaseAddress = controlUri };
            using var mutationContent = new StringContent(
                "{\"sessionId\":\"admin-session\",\"opaqueCommandBase64\":\"AQI=\"}",
                Encoding.UTF8,
                "application/json");
            using var mutationResponse = await http.PostAsync(
                "/test-control/inject-world-mutation",
                mutationContent,
                cancellationToken);
            mutationResponse.EnsureSuccessStatusCode();
            using var mutationDocument = JsonDocument.Parse(
                await mutationResponse.Content.ReadAsStringAsync(cancellationToken));
            Assert.True(mutationDocument.RootElement.GetProperty("accepted").GetBoolean());

            await Task.WhenAll(
                first.WaitForExitAsync(TestContext.Current.CancellationToken),
                second.WaitForExitAsync(TestContext.Current.CancellationToken),
                firstOutput,
                firstError,
                secondOutput,
                secondError);

            // The host owns the append-only server trace for its lifetime. Stop
            // it before taking the final snapshot of that file on Windows.
            StopChild(host);

            Assert.Equal(SmokeClient.SmokeClientExitCodes.Success, first.ExitCode);
            Assert.Equal(SmokeClient.SmokeClientExitCodes.Success, second.ExitCode);
            Assert.Contains("\"messageType\":\"Delta\"", File.ReadAllText(firstTrace), StringComparison.Ordinal);
            Assert.Contains("\"messageType\":\"DeltaAck\"", File.ReadAllText(firstTrace), StringComparison.Ordinal);
            Assert.Contains("\"messageType\":\"Delta\"", File.ReadAllText(secondTrace), StringComparison.Ordinal);
            Assert.Contains("\"messageType\":\"DeltaAck\"", File.ReadAllText(secondTrace), StringComparison.Ordinal);
            var serverTrace = File.ReadAllText(Path.Combine(root, "server.jsonl"));
            Assert.Contains("\"authorityRevision\":1", serverTrace, StringComparison.Ordinal);
        }
        finally
        {
            StopChild(first);
            StopChild(second);
            StopChild(host);
            Directory.Delete(root, recursive: true);
        }
    }

    private static Process StartChild(string assembly, params string[] arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(assembly)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(assembly);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        return process;
    }

    private static async Task WaitForTraceAsync(
        string path,
        string marker,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp()
            + (long)(Stopwatch.Frequency * TimeSpan.FromSeconds(10).TotalSeconds);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                try
                {
                    if (ReadSharedText(path).Contains(marker, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                    // The writer may be between flushes; retry until the bound.
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        string current;
        try
        {
            current = File.Exists(path) ? ReadSharedText(path) : "<trace file missing>";
        }
        catch (IOException)
        {
            current = "<trace file locked>";
        }
        throw new TimeoutException(
            $"Timed out waiting for trace marker {marker} in {path}; current trace: {current}");
    }

    private static string ReadSharedText(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    private static void StopChild(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
        catch (InvalidOperationException)
        {
            // The child may not have been started after an assertion failure.
        }
    }
}
