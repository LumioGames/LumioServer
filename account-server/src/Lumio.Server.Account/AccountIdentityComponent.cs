namespace Lumio.Server.Account;

public sealed class AccountIdentityComponent
{
    public AccountIdentityComponent(ulong entityId, string accountId, string loginName, ulong createdAtUnixSeconds)
    {
        EntityId = entityId;
        AccountId = accountId;
        LoginName = loginName;
        CreatedAtUnixSeconds = createdAtUnixSeconds;
    }

    public ulong EntityId { get; }

    public string AccountId { get; }

    public string LoginName { get; }

    public ulong CreatedAtUnixSeconds { get; }
}
