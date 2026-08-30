using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lumio.Server.MvpHost.SmokeClient;

public static class Program
{
    public static int Main(string[] args)
    {
        var parsed = SmokeClientCommandLineParser.Parse(args);
        if (!parsed.IsValid || parsed.Options is null)
        {
            if (!string.Equals(parsed.Error, "help", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(parsed.Error ?? "invalid arguments");
            }

            return SmokeClientExitCodes.InvalidArguments;
        }

        using var cancellation = new CancellationTokenSource();
        using var trace = new SmokeTraceWriter(parsed.Options.TraceFile);
        try
        {
            return SmokeClientRunner.RunAsync(parsed.Options, trace, cancellation.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (ArgumentException)
        {
            return SmokeClientExitCodes.InvalidArguments;
        }
        catch (InvalidOperationException)
        {
            return SmokeClientExitCodes.TransportFatal;
        }
    }
}
