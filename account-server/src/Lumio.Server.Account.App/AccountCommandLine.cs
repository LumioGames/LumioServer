using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lumio.Server.Account.App;

public sealed record AccountCommandLineOptions(
    string ListenHost,
    int ListenPort,
    string StorePath,
    byte[] AdmissionPrivateSeed,
    byte[] BotToolPublicKey,
    byte AdmissionKeyId);

public readonly record struct AccountParseResult(bool IsValid, AccountCommandLineOptions? Options, string? Error)
{
    public static AccountParseResult Invalid(string error) => new(false, null, error);

    public static AccountParseResult Valid(AccountCommandLineOptions options) => new(true, options, null);
}

public static class AccountCommandLineParser
{
    public const string AdmissionPrivateKeyEnv = "LUMIO_ACCOUNT_ADMISSION_PRIVATE_KEY_HEX";
    public const string BotToolPublicKeyEnv = "LUMIO_ACCOUNT_BOT_TOOL_PUBLIC_KEY_HEX";
    public const string AdmissionKeyIdEnv = "LUMIO_ACCOUNT_ADMISSION_KEY_ID";

    public static AccountParseResult Parse(IReadOnlyList<string> args, Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(env);

        var host = "127.0.0.1";
        var port = 0;
        string? storePath = null;
        byte admissionKeyId = 1;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--store-path":
                    if (!TryReadValue(args, ref i, out storePath))
                    {
                        return AccountParseResult.Invalid("--store-path requires a path");
                    }

                    break;
                case "--listen":
                    if (!TryReadValue(args, ref i, out var listen) || !TryParseListen(listen, out host, out port))
                    {
                        return AccountParseResult.Invalid("--listen requires host:port");
                    }

                    break;
                case "--admission-key-id":
                    if (!TryReadValue(args, ref i, out var keyIdText)
                        || !byte.TryParse(keyIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out admissionKeyId))
                    {
                        return AccountParseResult.Invalid("--admission-key-id requires 0-255");
                    }

                    break;
                default:
                    return AccountParseResult.Invalid("unknown argument: " + args[i]);
            }
        }

        if (string.IsNullOrWhiteSpace(storePath))
        {
            return AccountParseResult.Invalid("--store-path is required");
        }

        if (!string.Equals(host, "127.0.0.1", StringComparison.Ordinal))
        {
            return AccountParseResult.Invalid("non-loopback listen requires access control; this slice binds 127.0.0.1 only");
        }

        var envKeyId = env(AdmissionKeyIdEnv);
        if (!string.IsNullOrEmpty(envKeyId)
            && !byte.TryParse(envKeyId, NumberStyles.Integer, CultureInfo.InvariantCulture, out admissionKeyId))
        {
            return AccountParseResult.Invalid("LUMIO_ACCOUNT_ADMISSION_KEY_ID must be 0-255");
        }

        if (!TryDecodeKey(env(AdmissionPrivateKeyEnv), 32, out var admissionSeed))
        {
            return AccountParseResult.Invalid("LUMIO_ACCOUNT_ADMISSION_PRIVATE_KEY_HEX must be 32 lowercase hex bytes");
        }

        if (!TryDecodeKey(env(BotToolPublicKeyEnv), 32, out var botPublic))
        {
            return AccountParseResult.Invalid("LUMIO_ACCOUNT_BOT_TOOL_PUBLIC_KEY_HEX must be 32 lowercase hex bytes");
        }

        return AccountParseResult.Valid(new AccountCommandLineOptions(
            host,
            port,
            storePath,
            admissionSeed,
            botPublic,
            admissionKeyId));
    }

    private static bool TryReadValue(IReadOnlyList<string> args, ref int index, out string value)
    {
        if (index + 1 >= args.Count)
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return !string.IsNullOrEmpty(value);
    }

    private static bool TryParseListen(string listen, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var separator = listen.LastIndexOf(':');
        if (separator <= 0 || separator == listen.Length - 1)
        {
            return false;
        }

        host = listen[..separator];
        return int.TryParse(listen[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
            && port >= 0
            && port <= 65535;
    }

    private static bool TryDecodeKey(string? hex, int byteLength, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        return !string.IsNullOrEmpty(hex)
            && Account.Hex.TryDecode(hex, out bytes)
            && bytes.Length == byteLength;
    }
}
