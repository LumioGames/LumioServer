using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Lumio.Server.Account;

namespace Lumio.Server.Account.Tests;

internal sealed class MutableClock : IAccountClock
{
    public MutableClock(ulong unixSeconds)
    {
        UnixSeconds = unixSeconds;
    }

    public ulong UnixSeconds { get; set; }
}

internal sealed class RecordingAudit : IAccountAuditSink
{
    private static readonly string[] Forbidden = ["password", "hash", "argon2", "argon2id", "botToolCredential", "admissionCredential", "credentialBytes"];

    public List<(string Kind, IReadOnlyDictionary<string, string> Fields)> Events { get; } = [];

    public void Write(string kind, IReadOnlyDictionary<string, string> fields)
    {
        foreach (var pair in fields)
        {
            foreach (var forbidden in Forbidden)
            {
                if (pair.Key.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("audit field must not carry secret material: " + pair.Key);
                }
            }

            if (pair.Value.Contains(AccountTestProfile.Password, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("audit must not contain the test password");
            }
        }

        Events.Add((kind, fields));
    }
}

internal static class AccountTestProfile
{
    // Hello World profile default from lumio.account-port.v1 passwordProfile.testProfile.
    public const string Password = "123456";
    public const ulong Now = 1_700_000_000;
}

internal sealed class AccountHarness : IDisposable
{
    public AccountHarness()
    {
        StorePath = Path.Combine(Path.GetTempPath(), "lumio-account-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StorePath);
        Clock = new MutableClock(AccountTestProfile.Now);
        Audit = new RecordingAudit();
        var admission = Ed25519Keys.Generate();
        var bot = Ed25519Keys.Generate();
        AdmissionSeed = admission.Seed;
        AdmissionPublicKey = admission.PublicKey;
        BotToolSeed = bot.Seed;
        BotToolPublicKey = bot.PublicKey;
        Runtime = AccountRuntime.Open(new AccountServerOptions
        {
            StorePath = StorePath,
            AdmissionPrivateSeed = (byte[])AdmissionSeed.Clone(),
            BotToolPublicKey = BotToolPublicKey,
            AdmissionKeyId = 1,
            Clock = Clock,
            Audit = Audit,
        });
    }

    public string StorePath { get; }

    public MutableClock Clock { get; }

    public RecordingAudit Audit { get; }

    public byte[] AdmissionSeed { get; }

    public byte[] AdmissionPublicKey { get; }

    public byte[] BotToolSeed { get; }

    public byte[] BotToolPublicKey { get; }

    public AccountRuntime Runtime { get; }

    public string MintBotToolCredential(ulong? expiresAt = null, string toolId = "bot-launcher")
    {
        var nonce = new byte[16];
        RandomNumberGenerator.Fill(nonce);
        return BotToolCredential.Issue(
            BotToolSeed,
            toolId,
            Clock.UnixSeconds,
            expiresAt ?? Clock.UnixSeconds + 3600,
            nonce);
    }

    public static string MintBotToolCredentialWithSeed(
        byte[] seed,
        ulong issuedAt,
        ulong expiresAt,
        string scope,
        string toolId = "bot-launcher")
    {
        var nonce = new byte[16];
        RandomNumberGenerator.Fill(nonce);
        var payload = BotToolCredential.Encode(new BotToolCredentialPayload(toolId, scope, issuedAt, expiresAt, nonce));
        var signature = LumioSignature.Sign(seed, AccountPort.BotToolTrustDomain, AccountPort.BotToolPayloadType, payload);
        var framed = new byte[payload.Length + Ed25519Keys.SignatureLength];
        payload.CopyTo(framed, 0);
        signature.CopyTo(framed.AsSpan(payload.Length));
        return Base64Url.Encode(framed);
    }

    public void Dispose()
    {
        Runtime.Dispose();
        try
        {
            Directory.Delete(StorePath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
