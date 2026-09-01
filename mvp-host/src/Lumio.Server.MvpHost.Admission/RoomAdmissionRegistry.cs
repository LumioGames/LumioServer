using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lumio.Server.MvpHost.Admission;

public sealed class RoomAdmissionRegistry
{
    private readonly IAdmissionCredentialVerifier verifier;
    private readonly IAdmissionClock clock;
    private readonly object gate = new();
    private readonly Dictionary<string, LiveBinding> byAccount = new(StringComparer.Ordinal);
    private readonly Dictionary<(string RoomId, string ConnectionId), LiveBinding> byConnection = new();
    private readonly Dictionary<(string RoomId, string NetEntityId), LiveBinding> byNetEntity = new();
    private readonly Dictionary<string, string> netEntityHomeRoom = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TakeoverNotice> terminationNotices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> roomCounts = new(StringComparer.Ordinal);
    private ulong nextNetEntity;

    public RoomAdmissionRegistry(IAdmissionCredentialVerifier verifier, IAdmissionClock clock)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(clock);
        this.verifier = verifier;
        this.clock = clock;
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
            return AdmitLocked(roomId, connectionId, accepted.AccountId, kind);
        }
    }

    public bool TryGetBindingByConnection(string roomId, string connectionId, out ConnectionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(roomId);
        ArgumentNullException.ThrowIfNull(connectionId);
        lock (gate)
        {
            if (byConnection.TryGetValue((roomId, connectionId), out var live))
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
            if (!netEntityHomeRoom.TryGetValue(netEntityId, out var homeRoom))
            {
                return new BindingResolveOutcome.Rejected(EntityBindingPort.NonExistent);
            }

            if (!string.Equals(homeRoom, roomId, StringComparison.Ordinal))
            {
                return new BindingResolveOutcome.Rejected(EntityBindingPort.CrossRoomReference);
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
                && string.Equals(existing.ConnectionId, connectionId, StringComparison.Ordinal))
            {
                return new RoomAdmitOutcome.Accepted(existing.ToBinding(), null, null);
            }

            if (!string.Equals(existing.RoomId, roomId, StringComparison.Ordinal))
            {
                return new RoomAdmitOutcome.Rejected(EntityBindingPort.InvalidRequest);
            }

            return TakeoverLocked(roomId, connectionId, existing);
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
        };
        Attach(created);
        return new RoomAdmitOutcome.Accepted(created.ToBinding(), null, null);
    }

    private RoomAdmitOutcome.Accepted TakeoverLocked(string roomId, string connectionId, LiveBinding existing)
    {
        var superseded = existing.ConnectionId;
        var notice = new TakeoverNotice(
            EntityBindingPort.TakeoverReasonCode,
            ReconnectEligible: true,
            clock.UnixSeconds,
            Detail: null);
        terminationNotices[superseded] = notice;
        byConnection.Remove((existing.RoomId, existing.ConnectionId));

        existing.ConnectionId = connectionId;
        existing.ConnectionGeneration++;
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

    private int RoomCount(string roomId)
        => roomCounts.TryGetValue(roomId, out var count) ? count : 0;

    private string AllocateNetEntityId()
    {
        var value = ++nextNetEntity;
        return "nent_" + value.ToString("x32", CultureInfo.InvariantCulture);
    }

    private sealed class LiveBinding
    {
        public required string AccountId { get; init; }

        public required string RoomId { get; init; }

        public required string NetEntityId { get; init; }

        public required BoundEntityKind EntityType { get; init; }

        public ulong ConnectionGeneration { get; set; }

        public required string ConnectionId { get; set; }

        public ConnectionBinding ToBinding()
            => new(AccountId, RoomId, NetEntityId, EntityType, ConnectionGeneration);
    }
}
