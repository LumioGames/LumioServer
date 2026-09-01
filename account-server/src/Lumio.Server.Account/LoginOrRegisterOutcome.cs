namespace Lumio.Server.Account;

public readonly record struct LoginOrRegisterOutcome(
    bool Accepted,
    bool AccountNewlyCreated,
    string? AccountId,
    string? LoginName,
    string? AdmissionCredential,
    ulong AdmissionExpiresAt,
    string? Code,
    string? Detail)
{
    public static LoginOrRegisterOutcome Ok(
        bool accountNewlyCreated,
        string accountId,
        string loginName,
        string admissionCredential,
        ulong admissionExpiresAt)
    {
        return new LoginOrRegisterOutcome(
            true,
            accountNewlyCreated,
            accountId,
            loginName,
            admissionCredential,
            admissionExpiresAt,
            null,
            null);
    }

    public static LoginOrRegisterOutcome Reject(string code, string detail)
    {
        return new LoginOrRegisterOutcome(false, false, null, null, null, 0, code, detail);
    }
}
