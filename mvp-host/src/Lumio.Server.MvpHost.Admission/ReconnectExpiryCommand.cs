namespace Lumio.Server.MvpHost.Admission;

public readonly record struct ReconnectExpiryCommand(
    string AccountId,
    string NetEntityId,
    ulong Token);
