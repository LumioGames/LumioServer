using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.Admission;

public sealed class RoomAdmissionRegistry
{
    private readonly IAdmissionCredentialVerifier verifier;
    private readonly IAdmissionClock clock;
    private readonly IMonotonicClock monotonic;
    private readonly ITimerService timers;
    private readonly IBoundedInbox<ReconnectExpiryCommand> expiryInbox;
    private readonly int reconnectWindowSeconds;
    private readonly object gate = new();
    private readonly Dictionary<string, LiveBinding> byAccount = new(StringComparer.Ordinal);
    private readonly Dictionary<(string RoomId, string ConnectionId), LiveBinding> byConnection = new();
    private readonly Dictionary<(string RoomId, string NetEntityId), LiveBinding> byNetEntity = new();
    private readonly Dictionary<string, string> netEntityHomeRoom = new(StringComparer.Ordinal);
    private readonly HashSet<string> tombstones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TakeoverNotice> terminationNotices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> roomCounts = new(StringComparer.Ordinal);
    private readonly ulong instanceKey = CreateInstanceKey();
    private ulong nextNetEntity;
    private ulong nextExpiryToken;

    public RoomAdmissionRegistry(
        IAdmissionCredentialVerifier verifier,
        IAdmissionClock clock,
        IMonotonicClock monotonic,
        ITimerService timers,
        IBoundedInbox<ReconnectExpiryCommand> expiryInbox,
        int reconnectWindowSeconds)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(monotonic);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(expiryInbox);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reconnectWindowSeconds);

        this.verifier = verifier;
        this.clock = clock;
        this.monotonic = monotonic;
        this.timers = timers;
        this.expiryInbox = expiryInbox;
        this.reconnectWindowSeconds = reconnectWindowSeconds;
    }

    public RoomAdmitOutcome Admit(string roomId, string connectionId, string admissionCredential)
    {
        ArgumentNullException.ThrowIfNull(roomId);
        ArgumentNullException.ThrowIfNull(connectionId);
        ArgumentNullException.ThrowIfNull(admissionCredential);

        if (roomId.Length == 0 || connectionId.Length == 0)
        {
            return new RoomAdmitOutcome.Rejected(EntityBindingPort.InvalidRequest);
        }

        if (admissionCredential.Length == 0)
        {
            return new RoomAdmitOutcome.Rejected(EntityBindingPort.AdmissionCredentialMalformed);
        }

        var verified = verifier.Verify(admissionCredential);
        if (verified is AdmissionCredentialOutcome.Rejected rejected)
        {
            return new RoomAdmitOutcome.Rejected(rejected.Code);
        }

        var accepted = (AdmissionCredentialOutcome.Accepted)verified;
        if (EntityKindRules.IsBotNamespace(accepted.LoginName) && !accepted.BotToolContext)
        {
            return new RoomAdmitOutcome.Rejected(EntityBindingPort.BotNamespaceAdmissionForbidden);
        }

        var kind = EntityKindRules.Classify(accepted.LoginName, accepted.BotToolContext);
        lock (gate)
        {
            DrainExpiryLocked();
            return AdmitLocked(roomId, connectionId, accepted.AccountId, kind);
        }
    }

    public bool Disconnect(string roomId, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(roomId);
        ArgumentNullException.ThrowIfNull(connectionId);
        lock (gate)
        {
            DrainExpiryLocked();
            if (!byConnection.TryGetValue((roomId, connectionId), out var live)
                || live.Presence != BindingPresence.Active)
            {
                return false;
            }

            byConnection.Remove((roomId, connectionId));
            live.Presence = BindingPresence.Disconnected;
            var token = ++nextExpiryToken;
            live.ExpiryToken = token;
            var due = new MonotonicInstant(
                monotonic.Now.Ticks + TimeSpan.FromSeconds(reconnectWindowSeconds).Ticks);
            var command = new ReconnectExpiryCommand(live.AccountId, live.NetEntityId, token);
            live.ExpiryTimer = timers.Schedule(due, expiryInbox, command);
            return true;
        }
    }

    public InputAdmissionOutcome TryAcceptInput(string roomId, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(roomId);
        ArgumentNullException.ThrowIfNull(connectionId);
        lock (gate)
        {
            DrainExpiryLocked();
            if (byConnection.TryGetValue((roomId, connectionId), out var live)
                && live.Presence == BindingPresence.Active)
            {
                return new InputAdmissionOutcome.Accepted(live.ToBinding());
            }
        }

        return new InputAdmissionOutcome.Rejected(EntityBindingPort.BindingNotFound);
    }

    public bool TryGetPresence(string roomId, string netEntityId, out BindingPresence presence)
    {
        ArgumentNullException.ThrowIfNull(roomId);
        ArgumentNullException.ThrowIfNull(netEntityId);
        lock (gate)
        {
            DrainExpiryLocked();
            if (tombstones.Contains(netEntityId)
                && netEntityHomeRoom.TryGetValue(netEntityId, out var tombHome)
                && string.Equals(tombHome, roomId, StringComparison.Ordinal))
            {
                presence = BindingPresence.Tombstoned;
                return true;
            }

            if (byNetEntity.TryGetValue((roomId, netEntityId), out var live))
            {
                presence = live.Presence;
                return true;
            }
        }

        presence = default;
        return false;
    }

    public ReplicationFullSnapshot CaptureReplicationFullSnapshot(string roomId)
    {
        ArgumentNullException.ThrowIfNull(roomId);
        return new ReplicationFullSnapshot(
            EntityBindingPort.ReplicationFullSnapshotKind,
            roomId,
            ListBindings(roomId));
    }

    public bool TryGetBindingByConnection(string roomId, string connectionId, out ConnectionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(roomId);
        ArgumentNullException.ThrowIfNull(connectionId);
        lock (gate)
        {
            DrainExpiryLocked();
            if (byConnection.TryGetValue((roomId, connectionId), out var live)
                && live.Presence == BindingPresence.Active)
            {
                binding = live.ToBinding();
                return true;
            }
        }

        binding = default;
        return false;
    }

    public bool TryGetBindingByAccount(string accountId, out ConnectionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        lock (gate)
        {
            DrainExpiryLocked();
            if (byAccount.TryGetValue(accountId, out var live))
            {
                binding = live.ToBinding();
                return true;
            }
        }

        binding = default;
        return false;
    }

    public bool TryGetTerminationNotice(string connectionId, out TakeoverNotice notice)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        lock (gate)
        {
            if (terminationNotices.TryGetValue(connectionId, out notice))
            {
                return true;
            }
        }

        notice = default;
        return false;
    }

    public BindingResolveOutcome ResolveByNetEntityId(
        string roomId,
        string netEntityId,
        ulong? connectionGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(roomId);
        ArgumentNullException.ThrowIfNull(netEntityId);
        lock (gate)
        {
            DrainExpiryLocked();
            if (!netEntityHomeRoom.TryGetValue(netEntityId, out var homeRoom))
            {
                return new BindingResolveOutcome.Rejected(EntityBindingPort.NonExistent);
            }

            if (!string.Equals(homeRoom, roomId, StringComparison.Ordinal))
            {
                return new BindingResolveOutcome.Rejected(EntityBindingPort.CrossRoomReference);
            }

            if (tombstones.Contains(netEntityId))
            {
                return new BindingResolveOutcome.Rejected(EntityBindingPort.Tombstoned);
            }

            if (!byNetEntity.TryGetValue((roomId, netEntityId), out var live))
            {
                return new BindingResolveOutcome.Rejected(EntityBindingPort.NonExistent);
            }

            if (connectionGeneration is { } generation && generation != live.ConnectionGeneration)
            {
                return new BindingResolveOutcome.Rejected(EntityBindingPort.StaleGeneration);
            }

            return new BindingResolveOutcome.Found(live.ToBinding());
        }
    }

    public IReadOnlyList<ConnectionBinding> ListBindings(string roomId)
    {
        ArgumentNullException.ThrowIfNull(roomId);
        lock (gate)
        {
            DrainExpiryLocked();
            var result = new List<ConnectionBinding>();
            foreach (var live in byNetEntity.Values)
            {
                if (string.Equals(live.RoomId, roomId, StringComparison.Ordinal))
                {
                    result.Add(live.ToBinding());
                }
            }

            return result;
        }
    }

    public int CountEntities(string roomId, BoundEntityKind kind)
    {
        ArgumentNullException.ThrowIfNull(roomId);
        lock (gate)
        {
            DrainExpiryLocked();
            var count = 0;
            foreach (var live in byNetEntity.Values)
            {
                if (string.Equals(live.RoomId, roomId, StringComparison.Ordinal) && live.EntityType == kind)
                {
                    count++;
                }
            }

            return count;
        }
    }

    private RoomAdmitOutcome AdmitLocked(
        string roomId,
        string connectionId,
        string accountId,
        BoundEntityKind kind)
    {
        if (byConnection.TryGetValue((roomId, connectionId), out var occupant)
            && !string.Equals(occupant.AccountId, accountId, StringComparison.Ordinal))
        {
            return new RoomAdmitOutcome.Rejected(EntityBindingPort.InvalidRequest);
        }

        if (byAccount.TryGetValue(accountId, out var existing))
        {
            if (string.Equals(existing.RoomId, roomId, StringComparison.Ordinal)
                && string.Equals(existing.ConnectionId, connectionId, StringComparison.Ordinal)
                && existing.Presence == BindingPresence.Active)
            {
                return new RoomAdmitOutcome.Accepted(existing.ToBinding(), null, null);
            }

            if (!string.Equals(existing.RoomId, roomId, StringComparison.Ordinal))
            {
                return new RoomAdmitOutcome.Rejected(EntityBindingPort.InvalidRequest);
            }

            return RebindLocked(
                roomId,
                connectionId,
                existing,
                takeover: existing.Presence == BindingPresence.Active);
        }

        if (RoomCount(roomId) >= EntityBindingPort.MaxBindingsPerRoom)
        {
            return new RoomAdmitOutcome.Rejected(EntityBindingPort.InvalidRequest);
        }

        var created = new LiveBinding
        {
            AccountId = accountId,
            RoomId = roomId,
            NetEntityId = AllocateNetEntityId(),
            EntityType = kind,
            ConnectionGeneration = 1,
            ConnectionId = connectionId,
            Presence = BindingPresence.Active,
        };
        Attach(created);
        return new RoomAdmitOutcome.Accepted(created.ToBinding(), null, null);
    }

    private RoomAdmitOutcome.Accepted RebindLocked(
        string roomId,
        string connectionId,
        LiveBinding existing,
        bool takeover)
    {
        var superseded = existing.ConnectionId;
        TakeoverNotice? notice = null;
        if (takeover)
        {
            var issued = new TakeoverNotice(
                EntityBindingPort.TakeoverReasonCode,
                ReconnectEligible: true,
                clock.UnixSeconds,
                Detail: null);
            terminationNotices[superseded] = issued;
            notice = issued;
        }

        CancelExpiryLocked(existing);
        byConnection.Remove((existing.RoomId, existing.ConnectionId));

        existing.ConnectionId = connectionId;
        existing.ConnectionGeneration++;
        existing.Presence = BindingPresence.Active;
        byConnection[(roomId, connectionId)] = existing;
        return new RoomAdmitOutcome.Accepted(existing.ToBinding(), notice, superseded);
    }

    private void Attach(LiveBinding live)
    {
        byAccount[live.AccountId] = live;
        byConnection[(live.RoomId, live.ConnectionId)] = live;
        byNetEntity[(live.RoomId, live.NetEntityId)] = live;
        netEntityHomeRoom[live.NetEntityId] = live.RoomId;
        roomCounts[live.RoomId] = RoomCount(live.RoomId) + 1;
    }

    private void DrainExpiryLocked()
    {
        while (expiryInbox.TryDequeue(out var command))
        {
            ExpireIfMatchingLocked(command);
        }
    }

    private void ExpireIfMatchingLocked(in ReconnectExpiryCommand command)
    {
        if (!netEntityHomeRoom.TryGetValue(command.NetEntityId, out var homeRoom)
            || !byNetEntity.TryGetValue((homeRoom, command.NetEntityId), out var live)
            || live.ExpiryToken != command.Token
            || live.Presence != BindingPresence.Disconnected
            || !string.Equals(live.AccountId, command.AccountId, StringComparison.Ordinal))
        {
            return;
        }

        TombstoneLocked(live);
    }

    private void TombstoneLocked(LiveBinding live)
    {
        CancelExpiryLocked(live);
        byAccount.Remove(live.AccountId);
        byConnection.Remove((live.RoomId, live.ConnectionId));
        byNetEntity.Remove((live.RoomId, live.NetEntityId));
        roomCounts[live.RoomId] = Math.Max(0, RoomCount(live.RoomId) - 1);
        tombstones.Add(live.NetEntityId);
    }

    private void CancelExpiryLocked(LiveBinding live)
    {
        if (live.ExpiryTimer is { } timer)
        {
            _ = timers.Cancel(timer);
        }

        live.ExpiryTimer = null;
        live.ExpiryToken = 0;
    }

    private int RoomCount(string roomId)
        => roomCounts.TryGetValue(roomId, out var count) ? count : 0;

    private string AllocateNetEntityId()
    {
        var value = ++nextNetEntity;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"nent_{instanceKey:x16}{value:x16}");
    }

    private static ulong CreateInstanceKey()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private sealed class LiveBinding
    {
        public required string AccountId { get; init; }

        public required string RoomId { get; init; }

        public required string NetEntityId { get; init; }

        public required BoundEntityKind EntityType { get; init; }

        public ulong ConnectionGeneration { get; set; }

        public required string ConnectionId { get; set; }

        public BindingPresence Presence { get; set; }

        public ulong ExpiryToken { get; set; }

        public TimerId? ExpiryTimer { get; set; }

        public ConnectionBinding ToBinding()
            => new(AccountId, RoomId, NetEntityId, EntityType, ConnectionGeneration);
    }
}
