namespace Lumio.Server.MvpHost.Admission;

public abstract class AdmissionCredentialOutcome
{
    private AdmissionCredentialOutcome()
    {
    }

    public sealed class Accepted : AdmissionCredentialOutcome
    {
        public Accepted(string accountId, string loginName, bool botToolContext, ulong expiresAt, byte keyId)
        {
            AccountId = accountId;
            LoginName = loginName;
            BotToolContext = botToolContext;
            ExpiresAt = expiresAt;
            KeyId = keyId;
        }

        public string AccountId { get; }

        public string LoginName { get; }

        public bool BotToolContext { get; }

        public ulong ExpiresAt { get; }

        public byte KeyId { get; }
    }

    public sealed class Rejected : AdmissionCredentialOutcome
    {
        public Rejected(string code)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
