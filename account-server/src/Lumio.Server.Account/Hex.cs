using System;
using System.Globalization;

namespace Lumio.Server.Account;

internal static class Hex
{
    public static string EncodeLower(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool TryDecode(string hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(hex) || (hex.Length & 1) != 0)
        {
            return false;
        }

        for (var i = 0; i < hex.Length; i++)
        {
            var c = hex[i];
            var hexDigit = Uri.IsHexDigit(c);
            if (!hexDigit || char.IsUpper(c))
            {
                return false;
            }
        }

        bytes = Convert.FromHexString(hex);
        return true;
    }

    public static byte[] Decode(string hex)
    {
        if (!TryDecode(hex, out var bytes))
        {
            throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"hex must be even-length lowercase: {hex.Length}"));
        }

        return bytes;
    }
}
