using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Lumio.Server.MvpHost.App;

/// <summary>
/// Frozen C-1 <c>chat.input</c> decode. ChatInput is text-only after this envelope.
/// </summary>
internal static class ChatInputCommand
{
    public const string MappingId = "chat.input";
    public const int MaxTextUtf8Bytes = 512;

    internal static bool TryDecode(
        string? mappingId,
        string? payloadHex,
        string? payloadSha256,
        out string text,
        out string errorCode)
    {
        text = string.Empty;
        errorCode = "bad_envelope";
        if (!string.Equals(mappingId, MappingId, StringComparison.Ordinal))
        {
            errorCode = "unknown_command_type";
            return false;
        }

        if (!TryDecodeHex(payloadHex, out var payload))
        {
            errorCode = "undecodable_payload";
            return false;
        }

        if (!IsLowerSha256(payloadSha256)
            || !string.Equals(ToHex(SHA256.HashData(payload)), payloadSha256, StringComparison.Ordinal))
        {
            errorCode = "bad_payload_hash";
            return false;
        }

        if (!TryDecodeUtf8Prefixed(payload, out text))
        {
            errorCode = "undecodable_payload";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(text) > MaxTextUtf8Bytes)
        {
            errorCode = "chat_text_too_long";
            return false;
        }

        errorCode = string.Empty;
        return true;
    }

    private static bool TryDecodeUtf8Prefixed(byte[] payload, out string text)
    {
        text = string.Empty;
        if (payload.Length < 4)
        {
            return false;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (declared != (uint)(payload.Length - 4))
        {
            return false;
        }

        text = Encoding.UTF8.GetString(payload, 4, payload.Length - 4);
        return true;
    }

    private static bool IsLowerSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDecodeHex(string? hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(hex) || (hex.Length & 1) != 0)
        {
            return false;
        }

        bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var hi = FromNibble(hex[i * 2]);
            var lo = FromNibble(hex[(i * 2) + 1]);
            if (hi < 0 || lo < 0)
            {
                bytes = Array.Empty<byte>();
                return false;
            }

            bytes[i] = (byte)((hi << 4) | lo);
        }

        return true;
    }

    private static int FromNibble(char c)
    {
        if (c is >= '0' and <= '9')
        {
            return c - '0';
        }

        if (c is >= 'a' and <= 'f')
        {
            return c - 'a' + 10;
        }

        return -1;
    }

    private static string ToHex(byte[] bytes)
    {
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var value = bytes[i];
            chars[i * 2] = ToNibble(value >> 4);
            chars[(i * 2) + 1] = ToNibble(value & 0xF);
        }

        return new string(chars);
    }

    private static char ToNibble(int value) => (char)(value < 10 ? '0' + value : 'a' + (value - 10));
}
