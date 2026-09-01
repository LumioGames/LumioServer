using System;
using System.Buffers;
using System.Buffers.Text;

namespace Lumio.Server.Account;

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var paddedLength = text.Length + ((4 - (text.Length % 4)) % 4);
        Span<char> buffer = paddedLength <= 256 ? stackalloc char[paddedLength] : new char[paddedLength];
        text.AsSpan().CopyTo(buffer);
        buffer[text.Length..].Fill('=');
        for (var i = 0; i < text.Length; i++)
        {
            if (buffer[i] == '-')
            {
                buffer[i] = '+';
            }
            else if (buffer[i] == '_')
            {
                buffer[i] = '/';
            }
        }

        var utf8 = new byte[paddedLength];
        for (var i = 0; i < paddedLength; i++)
        {
            utf8[i] = (byte)buffer[i];
        }

        var max = Base64.GetMaxDecodedFromUtf8Length(paddedLength);
        var decoded = new byte[max];
        if (Base64.DecodeFromUtf8(utf8, decoded, out _, out var written) != OperationStatus.Done)
        {
            return false;
        }

        bytes = decoded[..written];
        return true;
    }
}
