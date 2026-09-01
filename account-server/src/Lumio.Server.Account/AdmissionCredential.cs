using System;
using System.Security.Cryptography;

namespace Lumio.Server.Account;

public readonly record struct AdmissionCredentialPayload(
    byte KeyId,
    string AccountId,
    string LoginName,
    bool BotToolContext,
    ulong IssuedAt,
    ulong ExpiresAt,
    byte[] Nonce);

public abstract class AdmissionVerifyOutcome
{
    private AdmissionVerifyOutcome()
    {
    }

    public sealed class Accepted : AdmissionVerifyOutcome
    {
        public Accepted(AdmissionCredentialPayload payload)
        {
            Payload = payload;
        }

        public AdmissionCredentialPayload Payload { get; }
    }

    public sealed class Rejected : AdmissionVerifyOutcome
    {
        public Rejected(string code)
        {
            Code = code;
        }

        public string Code { get; }
    }
}

public static class AdmissionCredential
{
    public static string Issue(
        ReadOnlySpan<byte> privateSeed,
        byte keyId,
        string accountId,
        string loginName,
        bool botToolContext,
        ulong issuedAt,
        ulong expiresAt)
    {
        var nonce = new byte[16];
        RandomNumberGenerator.Fill(nonce);
        var payload = EncodePayload(new AdmissionCredentialPayload(
            keyId,
            accountId,
            loginName,
            botToolContext,
            issuedAt,
            expiresAt,
            nonce));
        var signature = LumioSignature.Sign(
            privateSeed,
            AccountPort.AdmissionTrustDomain,
            AccountPort.AdmissionPayloadType,
            payload);
        var framed = new byte[payload.Length + Ed25519Keys.SignatureLength];
        payload.CopyTo(framed, 0);
        signature.CopyTo(framed.AsSpan(payload.Length));
        return Base64Url.Encode(framed);
    }

    public static AdmissionVerifyOutcome Verify(
        string wire,
        byte expectedKeyId,
        ReadOnlySpan<byte> publicKey,
        IAccountClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (!TrySplit(wire, out var payloadBytes, out var signature))
        {
            return new AdmissionVerifyOutcome.Rejected(AccountErrorCode.AdmissionCredentialMalformed);
        }

        if (!TryDecodePayload(payloadBytes, out var payload))
        {
            return new AdmissionVerifyOutcome.Rejected(AccountErrorCode.AdmissionCredentialMalformed);
        }

        if (payload.KeyId != expectedKeyId
            || !LumioSignature.Verify(
                publicKey,
                AccountPort.AdmissionTrustDomain,
                AccountPort.AdmissionPayloadType,
                payloadBytes,
                signature))
        {
            return new AdmissionVerifyOutcome.Rejected(AccountErrorCode.AdmissionCredentialInvalidSignature);
        }

        if (clock.UnixSeconds > payload.ExpiresAt)
        {
            return new AdmissionVerifyOutcome.Rejected(AccountErrorCode.AdmissionCredentialExpired);
        }

        if (LoginNameRules.IsBotNamespace(payload.LoginName) && !payload.BotToolContext)
        {
            return new AdmissionVerifyOutcome.Rejected(AccountErrorCode.BotNamespaceAdmissionForbidden);
        }

        return new AdmissionVerifyOutcome.Accepted(payload);
    }

    internal static byte[] EncodePayload(AdmissionCredentialPayload payload)
    {
        var writer = new LumioBinWriter();
        writer.WriteU16(AccountPort.AdmissionPayloadVersion);
        writer.WriteU8(payload.KeyId);
        writer.WriteAscii(payload.AccountId);
        writer.WriteAscii(payload.LoginName);
        writer.WriteU8(payload.BotToolContext ? (byte)1 : (byte)0);
        writer.WriteU64(payload.IssuedAt);
        writer.WriteU64(payload.ExpiresAt);
        writer.WriteFixedBytes(payload.Nonce);
        return writer.ToArray();
    }

    internal static bool TryDecodePayload(byte[] bytes, out AdmissionCredentialPayload payload)
    {
        payload = default;
        var reader = new LumioBinReader(bytes);
        if (!reader.TryReadU16(out var version) || version != AccountPort.AdmissionPayloadVersion)
        {
            return false;
        }

        if (!reader.TryReadU8(out var keyId)
            || !reader.TryReadAscii(out var accountId)
            || !reader.TryReadAscii(out var loginName)
            || !reader.TryReadU8(out var bot)
            || (bot != 0 && bot != 1)
            || !reader.TryReadU64(out var issuedAt)
            || !reader.TryReadU64(out var expiresAt)
            || !reader.TryReadFixedBytes(16, out var nonce)
            || reader.Remaining != 0)
        {
            return false;
        }

        payload = new AdmissionCredentialPayload(keyId, accountId, loginName, bot == 1, issuedAt, expiresAt, nonce);
        return true;
    }

    internal static string IssueFromPayload(ReadOnlySpan<byte> privateSeed, AdmissionCredentialPayload payload)
    {
        var payloadBytes = EncodePayload(payload);
        var signature = LumioSignature.Sign(
            privateSeed,
            AccountPort.AdmissionTrustDomain,
            AccountPort.AdmissionPayloadType,
            payloadBytes);
        var framed = new byte[payloadBytes.Length + Ed25519Keys.SignatureLength];
        payloadBytes.CopyTo(framed, 0);
        signature.CopyTo(framed.AsSpan(payloadBytes.Length));
        return Base64Url.Encode(framed);
    }

    private static bool TrySplit(string wire, out byte[] payload, out byte[] signature)
    {
        payload = Array.Empty<byte>();
        signature = Array.Empty<byte>();
        if (string.IsNullOrEmpty(wire) || !Base64Url.TryDecode(wire, out var framed)
            || framed.Length <= Ed25519Keys.SignatureLength)
        {
            return false;
        }

        payload = framed[..^Ed25519Keys.SignatureLength];
        signature = framed[^Ed25519Keys.SignatureLength..];
        return true;
    }
}
