using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lumio.Server.MvpHost.App;

public static class HostExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 64;
    public const int Fatal = 70;
}

public sealed record HostCommandLineOptions(
    string ListenUri,
    bool AllowInsecureLoopback,
    string HostProfile,
    string ProductId,
    string GameReleaseId,
    string? SharedSecretFile,
    int ReconnectWindowSeconds,
    bool EnableTestControl,
    string? TestControlListenUri,
    string? AuditTraceFile,
    string? EngineNativePath = null)
{
    public const string DefaultListenUri = "ws://127.0.0.1:0";
    public const string DefaultHostProfile = "LocalSplitProcess";
    public const string DefaultProductId = "A";
    public const string DefaultGameReleaseId = "A-1.1.0";
    public const string DefaultReleasePoolId = "pool-a-1.1";
    public const int DefaultReconnectWindowSeconds = 300;
}

public readonly record struct HostParseResult(
    bool IsValid,
    HostCommandLineOptions? Options,
    string? Error)
{
    public static HostParseResult Invalid(string error) => new(false, null, error);

    public static HostParseResult Valid(HostCommandLineOptions options) => new(true, options, null);
}

public static class HostCommandLineParser
{
    private static readonly HashSet<string> Profiles = new(StringComparer.Ordinal)
    {
        "LocalSplitProcess",
        "LocalEmbedded",
    };

    public static HostParseResult Parse(IReadOnlyList<string> args)
    {
        var listen = HostCommandLineOptions.DefaultListenUri;
        var allowInsecure = false;
        var profile = HostCommandLineOptions.DefaultHostProfile;
        var product = HostCommandLineOptions.DefaultProductId;
        var release = HostCommandLineOptions.DefaultGameReleaseId;
        string? secretFile = null;
        var reconnectSeconds = HostCommandLineOptions.DefaultReconnectWindowSeconds;
        var enableControl = false;
        string? controlListen = null;
        string? traceFile = null;
        string? engineNativePath = null;

        for (var index = 0; index < args.Count; index++)
        {
            var name = args[index];
            switch (name)
            {
                case "--listen":
                    if (!TryReadValue(args, ref index, out listen))
                    {
                        return HostParseResult.Invalid("--listen requires a URI");
                    }

                    break;
                case "--allow-insecure-loopback":
                    if (!TrySetFlag(args, index))
                    {
                        return HostParseResult.Invalid("--allow-insecure-loopback does not take a value");
                    }

                    allowInsecure = true;
                    break;
                case "--host-profile":
                    if (!TryReadValue(args, ref index, out profile))
                    {
                        return HostParseResult.Invalid("--host-profile requires a value");
                    }

                    break;
                case "--product-id":
                    if (!TryReadValue(args, ref index, out product))
                    {
                        return HostParseResult.Invalid("--product-id requires a value");
                    }

                    break;
                case "--game-release-id":
                    if (!TryReadValue(args, ref index, out release))
                    {
                        return HostParseResult.Invalid("--game-release-id requires a value");
                    }

                    break;
                case "--shared-secret-file":
                    if (!TryReadValue(args, ref index, out secretFile))
                    {
                        return HostParseResult.Invalid("--shared-secret-file requires a path");
                    }

                    break;
                case "--reconnect-window-seconds":
                    if (!TryReadValue(args, ref index, out var reconnectText)
                        || !int.TryParse(reconnectText, NumberStyles.None, CultureInfo.InvariantCulture, out reconnectSeconds)
                        || reconnectSeconds <= 0)
                    {
                        return HostParseResult.Invalid("--reconnect-window-seconds must be a positive integer");
                    }

                    break;
                case "--enable-test-control":
                    if (!TrySetFlag(args, index))
                    {
                        return HostParseResult.Invalid("--enable-test-control does not take a value");
                    }

                    enableControl = true;
                    break;
                case "--test-control-listen":
                    if (!TryReadValue(args, ref index, out controlListen))
                    {
                        return HostParseResult.Invalid("--test-control-listen requires a URI");
                    }

                    break;
                case "--audit-trace-file":
                    if (!TryReadValue(args, ref index, out traceFile))
                    {
                        return HostParseResult.Invalid("--audit-trace-file requires a path");
                    }

                    break;
                case "--engine-native":
                    if (!TryReadValue(args, ref index, out engineNativePath))
                    {
                        return HostParseResult.Invalid("--engine-native requires a path");
                    }

                    break;
                case "--help":
                case "-h":
                    return HostParseResult.Invalid("help");
                default:
                    return HostParseResult.Invalid($"unknown option '{name}'");
            }
        }

        if (string.IsNullOrWhiteSpace(product) || string.IsNullOrWhiteSpace(release))
        {
            return HostParseResult.Invalid("product and release identifiers must be non-empty");
        }

        if (!Profiles.Contains(profile))
        {
            return HostParseResult.Invalid("host profile must be LocalSplitProcess or LocalEmbedded");
        }

        if (!TryParseWebSocketUri(listen, out var listenUri))
        {
            return HostParseResult.Invalid("--listen must be an absolute ws:// or wss:// URI");
        }

        if (listenUri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
            && (!allowInsecure || !IsLoopbackHost(listenUri.Host)))
        {
            return HostParseResult.Invalid("ws:// requires --allow-insecure-loopback and a loopback host");
        }

        if (allowInsecure && !Profiles.Contains(profile))
        {
            return HostParseResult.Invalid("--allow-insecure-loopback is only valid for local profiles");
        }

        if (controlListen is not null)
        {
            if (!enableControl)
            {
                return HostParseResult.Invalid("--test-control-listen requires --enable-test-control");
            }

            if (!TryParseHttpUri(controlListen, out var controlUri)
                || !IsLoopbackHost(controlUri.Host))
            {
                return HostParseResult.Invalid("--test-control-listen must be a loopback http:// URI");
            }
        }

        if (traceFile is not null && !enableControl)
        {
            return HostParseResult.Invalid("--audit-trace-file requires --enable-test-control");
        }

        return HostParseResult.Valid(new HostCommandLineOptions(
            listen,
            allowInsecure,
            profile,
            product,
            release,
            secretFile,
            reconnectSeconds,
            enableControl,
            controlListen,
            traceFile,
            engineNativePath));
    }

    private static bool TryReadValue(IReadOnlyList<string> args, ref int index, out string value)
    {
        value = string.Empty;
        if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        value = args[++index];
        return true;
    }

    private static bool TrySetFlag(IReadOnlyList<string> args, int index)
        => index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal);

    private static bool TryParseWebSocketUri(string value, out Uri uri)
        => Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && (uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
            && uri.Port >= 0;

    private static bool TryParseHttpUri(string value, out Uri uri)
        => Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && uri.Port >= 0;

    private static bool IsLoopbackHost(string host)
    {
        var normalized = host.Trim('[', ']');
        return normalized.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }
}
