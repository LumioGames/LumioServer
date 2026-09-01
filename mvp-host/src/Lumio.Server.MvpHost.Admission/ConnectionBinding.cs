namespace Lumio.Server.MvpHost.Admission;

public readonly record struct ConnectionBinding(
    string AccountId,
    string RoomId,
    string NetEntityId,
    BoundEntityKind EntityType,
    ulong ConnectionGeneration);
