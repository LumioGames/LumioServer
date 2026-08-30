using System;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.Session;

/// <summary>
/// Host-private record for one server connection/session relationship.
/// It is deliberately distinct from any client replica state machine.
/// </summary>
public sealed class ServerConnectionSession
{
    private ServerConnectionSessionState state;
    private SessionBinding? binding;
    private ReplicationContextHandle? replicationContext;
    private WorldSlotId slot;
    private SlotEpoch slotEpoch;
    private TimerId? pendingTimer;
    private string? lastSnapshotId;
    private ulong lastSnapshotRevision;
    private bool baselineAcknowledged;

    public ServerConnectionSession(
        ServerSessionId sessionId,
        SessionEpoch sessionEpoch,
        string productId,
        string gameReleaseId)
    {
        if (string.IsNullOrWhiteSpace(sessionId.Value))
        {
            throw new ArgumentException("SessionId is required", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(productId))
        {
            throw new ArgumentException("ProductId is required", nameof(productId));
        }

        if (string.IsNullOrWhiteSpace(gameReleaseId))
        {
            throw new ArgumentException("GameReleaseId is required", nameof(gameReleaseId));
        }

        SessionId = sessionId;
        SessionEpoch = sessionEpoch;
        ProductId = productId;
        GameReleaseId = gameReleaseId;
        state = ServerConnectionSessionState.Admitted;
    }

    public ServerSessionId SessionId { get; }

    public SessionEpoch SessionEpoch { get; private set; }

    public ServerConnectionSessionState State => state;

    public SessionBinding? Binding => binding;

    public ReplicationContextHandle? ReplicationContext => replicationContext;

    public string ProductId { get; }

    public string GameReleaseId { get; }

    /// <summary>Last world-slot association retained while a connection is absent.</summary>
    internal WorldSlotId Slot => slot;

    internal SlotEpoch SlotEpoch => slotEpoch;

    internal TimerId? PendingTimer
    {
        get => pendingTimer;
        set => pendingTimer = value;
    }

    /// <summary>
    /// Identity of the most recently delivered full snapshot.  The identity is
    /// retained by the session so every Delta names the exact baseline that was
    /// sent, including after a resync or a reconnect.
    /// </summary>
    internal string? LastSnapshotId => lastSnapshotId;

    internal ulong LastSnapshotRevision => lastSnapshotRevision;

    internal bool BaselineAcknowledged => baselineAcknowledged;

    internal bool TryTransition(ServerConnectionSessionState next)
    {
        if (state == next)
        {
            return true;
        }

        if (!IsAllowed(state, next))
        {
            return false;
        }

        state = next;
        return true;
    }

    internal void Associate(WorldSlotId worldSlot, SlotEpoch epoch)
    {
        slot = worldSlot;
        slotEpoch = epoch;
    }

    internal void Bind(in SessionBinding value, ReplicationContextHandle context)
    {
        binding = value;
        replicationContext = context;
        slot = value.Slot;
        slotEpoch = value.SlotEpoch;
    }

    internal void ClearConnectionBinding()
    {
        binding = null;
        baselineAcknowledged = false;
    }

    internal void SetReplicationContext(ReplicationContextHandle context)
    {
        replicationContext = context;
    }

    internal void ClearReplicationContext()
    {
        replicationContext = null;
    }

    internal void AdvanceSessionEpoch()
    {
        SessionEpoch = new SessionEpoch(SessionEpoch.Value + 1);
        lastSnapshotId = null;
        lastSnapshotRevision = 0;
        baselineAcknowledged = false;
    }

    internal void RecordSnapshot(string snapshotId, ulong revision)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            throw new ArgumentException("Snapshot identity is required", nameof(snapshotId));
        }

        lastSnapshotId = snapshotId;
        lastSnapshotRevision = revision;
        baselineAcknowledged = false;
    }

    internal bool TryAcknowledgeBaseline(string snapshotId, ulong confirmedRevision)
    {
        if (!string.Equals(lastSnapshotId, snapshotId, StringComparison.Ordinal)
            || confirmedRevision != lastSnapshotRevision)
        {
            return false;
        }

        lastSnapshotRevision = confirmedRevision;
        baselineAcknowledged = true;
        return true;
    }

    internal bool TryAcknowledgeDelta(ulong toRevision)
    {
        if (!baselineAcknowledged || toRevision < lastSnapshotRevision)
        {
            return false;
        }

        lastSnapshotRevision = toRevision;
        return true;
    }

    internal static bool IsAllowed(ServerConnectionSessionState from, ServerConnectionSessionState to)
    {
        if (to == ServerConnectionSessionState.Closed
            && from is not ServerConnectionSessionState.Closed
            and not ServerConnectionSessionState.Expired
            and not ServerConnectionSessionState.Kicked)
        {
            return true;
        }

        if (to == ServerConnectionSessionState.Kicked
            && from is not ServerConnectionSessionState.Closed
            and not ServerConnectionSessionState.Expired
            and not ServerConnectionSessionState.Kicked)
        {
            return true;
        }

        return (from, to) switch
        {
            (ServerConnectionSessionState.Admitted, ServerConnectionSessionState.Syncing) => true,
            (ServerConnectionSessionState.Syncing, ServerConnectionSessionState.Active) => true,
            // A transport close may race the client's BaselineAck; retain the
            // session metadata and use the same reconnect window in that case.
            (ServerConnectionSessionState.Syncing, ServerConnectionSessionState.ReconnectWindow) => true,
            (ServerConnectionSessionState.Active, ServerConnectionSessionState.ReconnectWindow) => true,
            (ServerConnectionSessionState.ReconnectWindow, ServerConnectionSessionState.Syncing) => true,
            (ServerConnectionSessionState.ReconnectWindow, ServerConnectionSessionState.Expired) => true,
            _ => false,
        };
    }
}
