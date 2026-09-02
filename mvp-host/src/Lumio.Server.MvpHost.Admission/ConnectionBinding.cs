namespace Lumio.Server.MvpHost.Admission;

public readonly record struct ConnectionBinding(
    string AccountId,
    string RoomId,
    string NetEntityId,
    BoundEntityKind EntityType,
    ulong ConnectionGeneration);

/// <summary>
/// Host-audit / test-control projection of a live binding, including connection
/// identity that is not part of the frozen five-tuple.
/// </summary>
public readonly record struct BindingCensusRow(
    string AccountId,
    string RoomId,
    string NetEntityId,
    BoundEntityKind EntityType,
    ulong ConnectionGeneration,
    string ConnectionId,
    BindingPresence Presence,
    string LoginName);
