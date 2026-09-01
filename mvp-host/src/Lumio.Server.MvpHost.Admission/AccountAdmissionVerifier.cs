using System;
using Lumio.Server.Account;

namespace Lumio.Server.MvpHost.Admission;

public sealed class AccountAdmissionVerifier : IAdmissionCredentialVerifier
{
    private readonly byte keyId;
    private readonly byte[] publicKey;
    private readonly ClockAdapter clock;

    public AccountAdmissionVerifier(byte keyId, ReadOnlyMemory<byte> publicKey, IAdmissionClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (publicKey.Length != Ed25519Keys.PublicKeyLength)
        {
            throw new ArgumentException("admission public key must be 32 bytes.", nameof(publicKey));
        }

        this.keyId = keyId;
        this.publicKey = publicKey.ToArray();
        this.clock = new ClockAdapter(clock);
    }

    public AdmissionCredentialOutcome Verify(string admissionCredential)
    {
        var outcome = AdmissionCredential.Verify(admissionCredential, keyId, publicKey, clock);
        return outcome switch
        {
            AdmissionVerifyOutcome.Accepted accepted => new AdmissionCredentialOutcome.Accepted(
                accepted.Payload.AccountId,
                accepted.Payload.LoginName,
                accepted.Payload.BotToolContext,
                accepted.Payload.ExpiresAt,
                accepted.Payload.KeyId),
            AdmissionVerifyOutcome.Rejected rejected => new AdmissionCredentialOutcome.Rejected(rejected.Code),
            _ => new AdmissionCredentialOutcome.Rejected(EntityBindingPort.AdmissionCredentialMalformed),
        };
    }

    private sealed class ClockAdapter : IAccountClock
    {
        private readonly IAdmissionClock inner;

        public ClockAdapter(IAdmissionClock inner)
        {
            this.inner = inner;
        }

        public ulong UnixSeconds => inner.UnixSeconds;
    }
}
