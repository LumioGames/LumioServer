namespace Lumio.Server.Account;

public interface IAccountClock
{
    ulong UnixSeconds { get; }
}
