using System;
using System.Collections.Generic;

namespace Lumio.Server.MvpHost.SmokeClient;

public static class SmokeClientExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 64;
    public const int AssertionFailed = 65;
    public const int TransportFatal = 70;
}

public sealed record SmokeClientCommandLineOptions(
    string Endpoint,
    string? TokenFile,
    string? Nonce,
    string ProductId,
    string GameReleaseId,
    string Scenario,
    string? TraceFile)
{
    public const string DefaultProductId = "A";
    public const string DefaultGameReleaseId = "A-1.1.0";
    public const string DefaultScenario = "a1-alpha";
}

public readonly record struct SmokeParseResult(
    bool IsValid,
    SmokeClientCommandLineOptions? Options,
    string? Error)
{
    public static SmokeParseResult Invalid(string error) => new(false, null, error);

    public static SmokeParseResult Valid(SmokeClientCommandLineOptions options) => new(true, options, null);
}

public static class SmokeClientCommandLineParser
{
    private static readonly HashSet<string> Scenarios = new(StringComparer.Ordinal)
    {
        "a1-alpha",
        "bad-token",
        "replay-nonce",
        "oversize-message",
        "stale-generation",
        "release-mismatch",
        "gap-resync",
        "reconnect",
    };

    public static SmokeParseResult Parse(IReadOnlyList<string> args)
    {
        string? endpoint = null;
        string? tokenFile = null;
        string? nonce = null;
        var product = SmokeClientCommandLineOptions.DefaultProductId;
        var release = SmokeClientCommandLineOptions.DefaultGameReleaseId;
        var scenario = SmokeClientCommandLineOptions.DefaultScenario;
        string? traceFile = null;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--endpoint":
                    if (!TryReadValue(args, ref index, out endpoint))
                    {
                        return SmokeParseResult.Invalid("--endpoint requires a URI");
                    }

                    break;
                case "--token-file":
                    if (!TryReadValue(args, ref index, out tokenFile))
                    {
                        return SmokeParseResult.Invalid("--token-file requires a path");
                    }

                    break;
                case "--nonce":
                    if (!TryReadValue(args, ref index, out nonce))
                    {
                        return SmokeParseResult.Invalid("--nonce requires a value");
                    }

                    break;
                case "--product-id":
                    if (!TryReadValue(args, ref index, out product))
                    {
                        return SmokeParseResult.Invalid("--product-id requires a value");
                    }

                    break;
                case "--game-release-id":
                    if (!TryReadValue(args, ref index, out release))
                    {
                        return SmokeParseResult.Invalid("--game-release-id requires a value");
                    }

                    break;
                case "--scenario":
                    if (!TryReadValue(args, ref index, out scenario))
                    {
                        return SmokeParseResult.Invalid("--scenario requires a value");
                    }

                    break;
                case "--trace-file":
                    if (!TryReadValue(args, ref index, out traceFile))
                    {
                        return SmokeParseResult.Invalid("--trace-file requires a path");
                    }

                    break;
                case "--help":
                case "-h":
                    return SmokeParseResult.Invalid("help");
                default:
                    return SmokeParseResult.Invalid($"unknown option '{args[index]}'");
            }
        }

        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            || (!endpointUri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
                && !endpointUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)))
        {
            return SmokeParseResult.Invalid("--endpoint must be an absolute ws:// or wss:// URI");
        }

        if (string.IsNullOrWhiteSpace(tokenFile) || string.IsNullOrWhiteSpace(nonce))
        {
            return SmokeParseResult.Invalid("--token-file and --nonce are required");
        }

        if (string.IsNullOrWhiteSpace(product) || string.IsNullOrWhiteSpace(release))
        {
            return SmokeParseResult.Invalid("product and release identifiers must be non-empty");
        }

        if (!Scenarios.Contains(scenario!))
        {
            return SmokeParseResult.Invalid("unknown scenario");
        }

        return SmokeParseResult.Valid(new SmokeClientCommandLineOptions(
            endpoint,
            tokenFile,
            nonce,
            product,
            release,
            scenario!,
            traceFile));
    }

    private static bool TryReadValue(IReadOnlyList<string> args, ref int index, out string? value)
    {
        value = null;
        if (index + 1 >= args.Count
            || string.IsNullOrWhiteSpace(args[index + 1])
            || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        value = args[++index];
        return true;
    }
}
