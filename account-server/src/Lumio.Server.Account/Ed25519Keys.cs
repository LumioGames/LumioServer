using System;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Lumio.Server.Account;

public static class Ed25519Keys
{
    public const int SeedLength = 32;
    public const int PublicKeyLength = 32;
    public const int SignatureLength = 64;

    public static (byte[] Seed, byte[] PublicKey) Generate()
    {
        var seed = new byte[SeedLength];
        RandomNumberGenerator.Fill(seed);
        return (seed, PublicKeyFromSeed(seed));
    }

    public static byte[] PublicKeyFromSeed(ReadOnlySpan<byte> seed)
    {
        var privateKey = CreatePrivate(seed);
        return privateKey.GeneratePublicKey().GetEncoded();
    }

    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        var privateKey = CreatePrivate(seed);
        var signer = new Ed25519Signer();
        signer.Init(true, privateKey);
        signer.BlockUpdate(message);
        return signer.GenerateSignature();
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeyLength || signature.Length != SignatureLength)
        {
            return false;
        }

        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey.ToArray()));
        verifier.BlockUpdate(message);
        return verifier.VerifySignature(signature.ToArray());
    }

    private static Ed25519PrivateKeyParameters CreatePrivate(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedLength)
        {
            throw new ArgumentException("Ed25519 seed must be 32 bytes.", nameof(seed));
        }

        return new Ed25519PrivateKeyParameters(seed);
    }
}
