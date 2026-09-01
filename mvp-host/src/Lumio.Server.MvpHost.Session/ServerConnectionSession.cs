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
    private PrincipalId? principal;
    private WorldSlotId slot;
    private SlotEpoch slotEpoch;
    private TimerId? pendingTimer;
    private TimerId? pendingTimerToken;
    private string? lastSnapshotId;
    private ulong lastSnapshotRevision;
    private ulong? lastSentDeltaRevision;
    private ulong? pendingDeltaConfirmationSequence;
    private ulong? pendingDeltaFromRevision;
    private ulong? pendingDeltaToRevision;
    private string? pendingDeltaBaseSnapshotId;
    private ulong? lastAcknowledgedDeltaConfirmationSequence;
    private ulong? lastAcknowledgedDeltaToRevision;
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

    /// <summary>
    /// Immutable authenticated identity retained across the reconnect window.
    /// A reconnect must prove this same principal before a new grant is issued.
    /// </summary>
    internal PrincipalId? Principal => principal;

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

    internal TimerId? PendingTimerToken
    {
        get => pendingTimerToken;
        set => pendingTimerToken = value;
    }

    /// <summary>
    /// Identity of the most recently delivered full snapshot.  The identity is
    /// retained by the session so every Delta names the exact baseline that was
    /// sent, including after a resync or a reconnect.
    /// </summary>
    internal string? LastSnapshotId => lastSnapshotId;

    internal ulong LastSnapshotRevision => lastSnapshotRevision;

    internal ulong? LastSentDeltaRevision => lastSentDeltaRevision;

    internal ulong? PendingDeltaConfirmationSequence => pendingDeltaConfirmationSequence;

    internal ulong? PendingDeltaToRevision => pendingDeltaToRevision;

    internal ulong? PendingDeltaFromRevision => pendingDeltaFromRevision;

    internal string? PendingDeltaBaseSnapshotId => pendingDeltaBaseSnapshotId;

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

    internal void SetPrincipal(PrincipalId value)
    {
        if (string.IsNullOrWhiteSpace(value.Value))
        {
            throw new ArgumentException("PrincipalId is required", nameof(value));
        }

        if (principal is { } existing && existing != value)
        {
            throw new InvalidOperationException("Session principal cannot be rebound");
        }

        principal = value;
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
        ClearPendingDelta();
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
        baselineAcknowledged = false;
        ClearPendingDelta();
        ClearLastAcknowledgedDelta();
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
        ClearPendingDelta();
        ClearLastAcknowledgedDelta();
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

    internal void RecordDelta(
        ulong confirmationSequence,
        ulong fromRevision,
        ulong toRevision,
        string baseSnapshotId)
    {
        if (!baselineAcknowledged
            || pendingDeltaConfirmationSequence is not null
            || string.IsNullOrWhiteSpace(lastSnapshotId)
            || !string.Equals(lastSnapshotId, baseSnapshotId, StringComparison.Ordinal)
            || fromRevision != lastSnapshotRevision
            || toRevision <= fromRevision)
        {
            throw new InvalidOperationException("A delta cannot be recorded for the current replication cursor");
        }

        pendingDeltaConfirmationSequence = confirmationSequence;
        pendingDeltaFromRevision = fromRevision;
        pendingDeltaToRevision = toRevision;
        pendingDeltaBaseSnapshotId = baseSnapshotId;
        lastSentDeltaRevision = toRevision;
    }

    internal bool TryAcknowledgeDelta(ulong confirmationSequence, ulong toRevision)
    {
        if (!baselineAcknowledged)
        {
            return false;
        }

        if (pendingDeltaConfirmationSequence is null)
        {
            return lastAcknowledgedDeltaConfirmationSequence == confirmationSequence
                && lastAcknowledgedDeltaToRevision == toRevision;
        }

        if (pendingDeltaConfirmationSequence != confirmationSequence
            || pendingDeltaToRevision != toRevision
            || pendingDeltaFromRevision is null
            || toRevision <= pendingDeltaFromRevision)
        {
            return false;
        }

        lastSnapshotRevision = toRevision;
        lastAcknowledgedDeltaConfirmationSequence = confirmationSequence;
        lastAcknowledgedDeltaToRevision = toRevision;
        ClearPendingDelta();
        return true;
    }

    private void ClearPendingDelta()
    {
        pendingDeltaConfirmationSequence = null;
        pendingDeltaFromRevision = null;
        pendingDeltaToRevision = null;
        pendingDeltaBaseSnapshotId = null;
    }

    private void ClearLastAcknowledgedDelta()
    {
        lastAcknowledgedDeltaConfirmationSequence = null;
        lastAcknowledgedDeltaToRevision = null;
    }

    /// <summary>
    /// Legal MVP transitions. <see cref="ServerConnectionSessionState.Faulted"/> is
    /// modeled but unreachable here (absences.json <c>ABS-SESSION-FAULTED-UNREACHABLE</c>).
    /// </summary>
    internal static bool IsAllowed(ServerConnectionSessionState from, ServerConnectionSessionState to)
    {
        if (to == ServerConnectionSessionState.Faulted)
        {
            return false;
        }

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
