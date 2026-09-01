using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.Account;

namespace Lumio.Server.Account.App;

public static class Program
{
    public static int Main(string[] args)
    {
        var parsed = AccountCommandLineParser.Parse(args, Environment.GetEnvironmentVariable);
        if (!parsed.IsValid || parsed.Options is null)
        {
            Console.Error.WriteLine(parsed.Error ?? "invalid arguments");
            return AccountExitCodes.InvalidArguments;
        }

        try
        {
            return RunAsync(parsed.Options).GetAwaiter().GetResult();
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return AccountExitCodes.InvalidArguments;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return AccountExitCodes.InitializationFailed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return AccountExitCodes.Fatal;
        }
    }

    private static async Task<int> RunAsync(AccountCommandLineOptions options)
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
            sigint?.Dispose();
            sigterm?.Dispose();
            sigint = null;
            sigterm = null;
        }

        Console.CancelKeyPress += OnCancel;
        AccountRuntime? runtime = null;
        AccountProtocolServer? server = null;
        try
        {
            runtime = AccountRuntime.Open(new AccountServerOptions
            {
                StorePath = options.StorePath,
                AdmissionPrivateSeed = options.AdmissionPrivateSeed,
                BotToolPublicKey = options.BotToolPublicKey,
                AdmissionKeyId = options.AdmissionKeyId,
                Clock = new SystemAccountClock(),
            });
            server = new AccountProtocolServer(runtime, options.ListenHost, options.ListenPort);
            await server.StartAsync(shutdown.Token).ConfigureAwait(false);
            Console.WriteLine(new AccountReadyLine(
                server.BoundPort,
                Environment.ProcessId,
                AccountPort.ContractId,
                runtime.StorePath));
            Console.Out.Flush();
            await stopped.Task.WaitAsync(shutdown.Token).ConfigureAwait(false);
            return AccountExitCodes.Success;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return AccountExitCodes.Success;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            sigint?.Dispose();
            sigterm?.Dispose();
            if (server is not null)
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }

            runtime?.Dispose();
        }

        void OnCancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            stopped.TrySetResult(null);
        }

        void Signal(PosixSignalContext context)
        {
            context.Cancel = true;
            stopped.TrySetResult(null);
        }
    }
}
