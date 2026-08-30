using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
            "--shared-secret-file", secret);
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
            await Task.WhenAll(
                first.WaitForExitAsync(TestContext.Current.CancellationToken),
                second.WaitForExitAsync(TestContext.Current.CancellationToken),
                firstOutput,
                firstError,
                secondOutput,
                secondError);

            Assert.Equal(SmokeClient.SmokeClientExitCodes.Success, first.ExitCode);
            Assert.Equal(SmokeClient.SmokeClientExitCodes.Success, second.ExitCode);
            Assert.True(File.Exists(Path.Combine(root, "client-one.jsonl")));
            Assert.True(File.Exists(Path.Combine(root, "client-two.jsonl")));
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
