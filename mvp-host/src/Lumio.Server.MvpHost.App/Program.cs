using System;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace Lumio.Server.MvpHost.App;

public static class Program
{
    public static int Main(string[] args)
    {
        var parsed = HostCommandLineParser.Parse(args);
        if (!parsed.IsValid || parsed.Options is null)
        {
            if (!string.Equals(parsed.Error, "help", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(parsed.Error ?? "invalid arguments");
            }

            return HostExitCodes.InvalidArguments;
        }

        return RunAsync(parsed.Options).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(HostCommandLineOptions options)
    {
        using var shutdown = new CancellationTokenSource();
        var stopped = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PosixSignalRegistration? sigint = null;
        PosixSignalRegistration? sigterm = null;
        try
        {
            sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, Signal);
            sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Signal);
        }
        catch (PlatformNotSupportedException)
        {
            // Console.CancelKeyPress remains available on platforms without POSIX signals.
            sigint?.Dispose();
            sigterm?.Dispose();
            sigint = null;
            sigterm = null;
        }
        Console.CancelKeyPress += OnCancel;

        HostComposition? composition = null;
        try
        {
            composition = HostComposition.Create(options);
            await composition.StartAsync(shutdown.Token).ConfigureAwait(false);
            Console.WriteLine(new HostReadyLine(composition.BoundListenUri, composition.BoundTestControlUri));
            var completed = await Task.WhenAny(stopped.Task, composition.FatalTask).ConfigureAwait(false);
            return completed == composition.FatalTask || composition.HasFatalFault
                ? HostExitCodes.Fatal
                : HostExitCodes.Success;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return HostExitCodes.Success;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return HostExitCodes.InvalidArguments;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return HostExitCodes.Fatal;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            sigint?.Dispose();
            sigterm?.Dispose();
            if (composition is not null)
            {
                try
                {
                    await composition.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"shutdown failed: {ex.Message}");
                }
            }
        }

        void OnCancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
            stopped.TrySetResult(null);
        }

        void Signal(PosixSignalContext context)
        {
            context.Cancel = true;
            shutdown.Cancel();
            stopped.TrySetResult(null);
        }
    }
}
