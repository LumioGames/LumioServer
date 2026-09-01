using System;
using System.Security.Cryptography;
using System.Text;

namespace Lumio.Server.Account;

internal static class LumioSignature
{
    public static byte[] Preimage(string trustDomain, string payloadType, ReadOnlySpan<byte> payload)
    {
        var digestHex = Hex.EncodeLower(SHA256.HashData(payload));
        var prefix = "LumioSignatureV1";
        var length = prefix.Length + 1 + trustDomain.Length + 1 + payloadType.Length + 1 + digestHex.Length;
        var preimage = new byte[length];
        var offset = 0;
        offset += Encoding.ASCII.GetBytes(prefix, preimage.AsSpan(offset));
        preimage[offset++] = 0;
        offset += Encoding.ASCII.GetBytes(trustDomain, preimage.AsSpan(offset));
        preimage[offset++] = 0;
        offset += Encoding.ASCII.GetBytes(payloadType, preimage.AsSpan(offset));
        preimage[offset++] = 0;
        Encoding.ASCII.GetBytes(digestHex, preimage.AsSpan(offset));
        return preimage;
    }

    public static byte[] Sign(ReadOnlySpan<byte> seed, string trustDomain, string payloadType, ReadOnlySpan<byte> payload)
    {
        return Ed25519Keys.Sign(seed, Preimage(trustDomain, payloadType, payload));
    }

    public static bool Verify(
        ReadOnlySpan<byte> publicKey,
        string trustDomain,
        string payloadType,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signature)
    {
        return Ed25519Keys.Verify(publicKey, Preimage(trustDomain, payloadType, payload), signature);
    }
}
