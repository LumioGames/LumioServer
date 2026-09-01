using System;

namespace Lumio.Server.Account;

public sealed class AccountServerOptions
{
    public required string StorePath { get; init; }

    public required byte[] AdmissionPrivateSeed { get; init; }

    public required byte[] BotToolPublicKey { get; init; }

    public byte AdmissionKeyId { get; init; }

    public required IAccountClock Clock { get; init; }

    public IAccountAuditSink Audit { get; init; } = NullAccountAuditSink.Instance;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrEmpty(StorePath);
        ArgumentNullException.ThrowIfNull(AdmissionPrivateSeed);
        ArgumentNullException.ThrowIfNull(BotToolPublicKey);
        ArgumentNullException.ThrowIfNull(Clock);
        ArgumentNullException.ThrowIfNull(Audit);
        if (AdmissionPrivateSeed.Length != Ed25519Keys.SeedLength)
        {
            throw new ArgumentException("admission private seed must be 32 bytes.", nameof(AdmissionPrivateSeed));
        }

        if (BotToolPublicKey.Length != Ed25519Keys.PublicKeyLength)
        {
            throw new ArgumentException("bot-tool public key must be 32 bytes.", nameof(BotToolPublicKey));
        }
    }
}
