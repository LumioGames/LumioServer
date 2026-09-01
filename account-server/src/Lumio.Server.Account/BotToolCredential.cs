using System;
using System.Security.Cryptography;

namespace Lumio.Server.Account;

internal readonly record struct BotToolCredentialPayload(
    string ToolId,
    string Scope,
    ulong IssuedAt,
    ulong ExpiresAt,
    byte[] Nonce);

internal static class BotToolCredential
{
    public static string Issue(
        ReadOnlySpan<byte> privateSeed,
        string toolId,
        ulong issuedAt,
        ulong expiresAt,
        byte[]? nonce = null)
    {
        if (nonce is null)
        {
            nonce = new byte[16];
            RandomNumberGenerator.Fill(nonce);
        }
        else if (nonce.Length != 16)
        {
            throw new ArgumentException("nonce must be 16 bytes.", nameof(nonce));
        }

        var payload = Encode(new BotToolCredentialPayload(toolId, AccountPort.BotToolScope, issuedAt, expiresAt, nonce));
        var signature = LumioSignature.Sign(
            privateSeed,
            AccountPort.BotToolTrustDomain,
            AccountPort.BotToolPayloadType,
            payload);
        var framed = new byte[payload.Length + Ed25519Keys.SignatureLength];
        payload.CopyTo(framed, 0);
        signature.CopyTo(framed.AsSpan(payload.Length));
        return Base64Url.Encode(framed);
    }

    public static bool TryVerify(
        string? wire,
        ReadOnlySpan<byte> publicKey,
        IAccountClock clock,
        out string errorCode)
    {
        errorCode = AccountErrorCode.BotToolCredentialMalformed;
        if (string.IsNullOrEmpty(wire) || !Base64Url.TryDecode(wire, out var framed)
            || framed.Length <= Ed25519Keys.SignatureLength)
        {
            return false;
        }

        var payloadBytes = framed[..^Ed25519Keys.SignatureLength];
        var signature = framed[^Ed25519Keys.SignatureLength..];
        if (!TryDecode(payloadBytes, out var payload))
        {
            errorCode = AccountErrorCode.BotToolCredentialMalformed;
            return false;
        }

        if (!LumioSignature.Verify(
                publicKey,
                AccountPort.BotToolTrustDomain,
                AccountPort.BotToolPayloadType,
                payloadBytes,
                signature)
            || !string.Equals(payload.Scope, AccountPort.BotToolScope, StringComparison.Ordinal))
        {
            errorCode = AccountErrorCode.BotToolCredentialInvalid;
            return false;
        }

        if (clock.UnixSeconds > payload.ExpiresAt)
        {
            errorCode = AccountErrorCode.BotToolCredentialExpired;
            return false;
        }

        errorCode = string.Empty;
        return true;
    }

    internal static byte[] Encode(BotToolCredentialPayload payload)
    {
        var writer = new LumioBinWriter();
        writer.WriteU16(AccountPort.BotToolPayloadVersion);
        writer.WriteAscii(payload.ToolId);
        writer.WriteAscii(payload.Scope);
        writer.WriteU64(payload.IssuedAt);
        writer.WriteU64(payload.ExpiresAt);
        writer.WriteFixedBytes(payload.Nonce);
        return writer.ToArray();
    }

    internal static bool TryDecode(byte[] bytes, out BotToolCredentialPayload payload)
    {
        payload = default;
        var reader = new LumioBinReader(bytes);
        if (!reader.TryReadU16(out var version) || version != AccountPort.BotToolPayloadVersion)
        {
            return false;
        }

        if (!reader.TryReadAscii(out var toolId)
            || !reader.TryReadAscii(out var scope)
            || !reader.TryReadU64(out var issuedAt)
            || !reader.TryReadU64(out var expiresAt)
            || !reader.TryReadFixedBytes(16, out var nonce)
            || reader.Remaining != 0)
        {
            return false;
        }

        payload = new BotToolCredentialPayload(toolId, scope, issuedAt, expiresAt, nonce);
        return true;
    }
}
