using System.Collections.Generic;

namespace Lumio.Server.MvpHost.Admission;

/// <summary>
/// Replication FullSnapshot of Room bindings for a reconnecting client's
/// ReplicaWorld rebuild. This is not persistence Snapshot/Restore (R-00353).
/// </summary>
public readonly record struct ReplicationFullSnapshot(
    string Kind,
    string RoomId,
    IReadOnlyList<ConnectionBinding> Entities);
