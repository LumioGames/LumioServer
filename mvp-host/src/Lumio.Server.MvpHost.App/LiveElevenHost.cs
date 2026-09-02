using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.MvpHost.Admission;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.App;

internal readonly record struct GameplayTickCommand(string? RoomId, TaskCompletionSource<JsonObject> Completion);

/// <summary>
/// Slices ChatRoomWorld + RoomAdmissionRegistry onto the frozen test-control API.
/// </summary>
internal sealed class LiveElevenHost : IDisposable, IBoundedInbox<GameplayTickCommand>
{
    private static readonly QueueBudget TickBudget = new(8, 4 * 1024);

    private readonly object gate = new();
    private readonly Dictionary<string, string> sessionByNent = new(StringComparer.Ordinal);
    private readonly RoomAdmissionRegistry registry;
    private readonly IMonotonicClock clock;
    private readonly ITimerService timers;
    private readonly IHostTraceSink trace;
    private readonly ChatRoomWorldAdapter gameplay;
    private readonly IBoundedInbox<GameplayTickCommand> inbox;
    private bool disposed;

    private LiveElevenHost(
        RoomAdmissionRegistry registry,
        IMonotonicClock clock,
        ITimerService timers,
        IHostTraceSink trace,
        ChatRoomWorldAdapter gameplay)
    {
        this.registry = registry;
        this.clock = clock;
        this.timers = timers;
        this.trace = trace;
        this.gameplay = gameplay;
        inbox = PlatformModule.CreateInbox<GameplayTickCommand>(in TickBudget);
    }

    internal static LiveElevenHost Create(
        RoomAdmissionRegistry registry,
        IMonotonicClock clock,
        ITimerService timers,
        INamedThreadSupervisor threads,
        IHostTraceSink trace)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(threads);
        ArgumentNullException.ThrowIfNull(trace);
        return new LiveElevenHost(
            registry,
            clock,
            timers,
            trace,
            new ChatRoomWorldAdapter(threads));
    }

    public QueueBudget Budget => inbox.Budget;

    public int Count => inbox.Count;

    internal JsonObject ListBindings()
    {
        var rows = new JsonArray();
        foreach (var live in registry.ListAllBindings())
        {
            rows.Add(new JsonObject
            {
                ["netEntityId"] = live.NetEntityId,
                ["accountId"] = live.AccountId,
                ["roomId"] = live.RoomId,
                ["entityKind"] = live.EntityType.ToContractValue(),
                ["connectionId"] = live.ConnectionId,
                ["sessionId"] = SessionIdOf(live),
                ["generation"] = live.ConnectionGeneration,
            });
        }

        return new JsonObject { ["bindings"] = rows };
    }

    internal void OnAdmitted(ConnectionBinding binding, string connectionId, string sessionId, string loginName)
    {
        ArgumentNullException.ThrowIfNull(binding.NetEntityId);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = binding.ConnectionGeneration > 1
                ? "sess-" + loginName + "-re"
                : "sess-" + loginName;
        }

        lock (gate)
        {
            sessionByNent[binding.NetEntityId] = sessionId;
        }

        if (ChatRoomWorldAdapter.TryParseGameplayId(binding.NetEntityId, out var gameplayId))
        {
            _ = gameplay.TryCreateEntity(binding.RoomId, gameplayId);
        }

        if (trace is IBindingTraceProjection projection)
        {
            projection.ProjectBinding(
                sessionId,
                binding.NetEntityId,
                binding.AccountId,
                binding.EntityType.ToContractValue());
        }
        else
        {
            trace.State(sessionId, "Active", 1, 1, 1);
        }

        _ = connectionId;
    }

    internal JsonObject RoomAdmit(string roomId, string connectionId, string admissionCredential)
    {
        var outcome = registry.Admit(roomId, connectionId, admissionCredential);
        if (outcome is not RoomAdmitOutcome.Accepted accepted)
        {
            var code = (outcome as RoomAdmitOutcome.Rejected)?.Code ?? EntityBindingPort.InvalidRequest;
            return new JsonObject { ["accepted"] = false, ["code"] = code };
        }

        var loginName = LoginNameOf(accepted.Binding.NetEntityId);
        OnAdmitted(accepted.Binding, connectionId, string.Empty, loginName);
        return new JsonObject
        {
            ["accepted"] = true,
            ["netEntityId"] = accepted.Binding.NetEntityId,
            ["generation"] = accepted.Binding.ConnectionGeneration,
        };
    }

    internal JsonObject Query(string requesterNetEntityId, string targetNetEntityId, string attributeId, ulong? generation)
    {
        if (IsForbiddenAttribute(attributeId))
        {
            return QueryJson("unauthorized", null);
        }

        var target = registry.ResolveAnywhere(targetNetEntityId);
        if (target is BindingResolveOutcome.Rejected rejectedTarget)
        {
            return QueryJson(MapResolve(rejectedTarget.Code), null);
        }

        if (generation is { } supplied)
        {
            var stale = registry.ResolveAnywhere(targetNetEntityId, supplied);
            if (stale is BindingResolveOutcome.Rejected staleRejected
                && string.Equals(staleRejected.Code, EntityBindingPort.StaleGeneration, StringComparison.Ordinal))
            {
                return QueryJson("stale_generation", null);
            }
        }

        var foundTarget = (BindingResolveOutcome.Found)target;
        var requester = registry.ResolveAnywhere(requesterNetEntityId);
        if (requester is not BindingResolveOutcome.Found foundRequester)
        {
            return QueryJson("unauthorized", null);
        }

        if (!string.Equals(foundRequester.Binding.RoomId, foundTarget.Binding.RoomId, StringComparison.Ordinal))
        {
            return QueryJson("unauthorized", null);
        }

        var crossEntity = !string.Equals(requesterNetEntityId, targetNetEntityId, StringComparison.Ordinal);
        return attributeId switch
        {
            "EntityIdentity.entityType" => QueryJson("ok", foundTarget.Binding.EntityType.ToContractValue()),
            "EntityIdentity.accountId" => crossEntity
                ? QueryJson("invisible", null)
                : QueryJson("ok", foundTarget.Binding.AccountId),
            "EntityIdentity.restrictedFlag" => crossEntity
                ? QueryJson("unauthorized", null)
                : QueryJson("ok", "0"),
            "ChatComponent.lastMessageText" => crossEntity
                ? QueryJson("invisible", null)
                : ReadLastMessage(foundTarget.Binding, text: true),
            "ChatComponent.lastMessageTick" => crossEntity
                ? QueryJson("invisible", null)
                : ReadLastMessage(foundTarget.Binding, text: false),
            "EntityPresence.disconnected" => QueryJson("ok", "false"),
            _ => QueryJson("unauthorized", null),
        };
    }

    internal JsonObject Chat(string connectionId, string mappingId, string payload, string payloadSha256)
    {
        if (!ChatInputCommand.TryDecode(mappingId, payload, payloadSha256, out var text, out var error))
        {
            return new JsonObject { ["ok"] = false, ["kind"] = "Rejected", ["error"] = error };
        }

        BindingCensusRow? live = null;
        foreach (var row in registry.ListAllBindings())
        {
            if (string.Equals(row.ConnectionId, connectionId, StringComparison.Ordinal)
                && row.Presence == BindingPresence.Active)
            {
                live = row;
                break;
            }
        }

        if (live is null)
        {
            return new JsonObject { ["ok"] = false, ["kind"] = "Rejected", ["error"] = EntityBindingPort.BindingNotFound };
        }

        var input = registry.TryAcceptInput(live.Value.RoomId, connectionId);
        if (input is not InputAdmissionOutcome.Accepted)
        {
            return new JsonObject { ["ok"] = false, ["kind"] = "Rejected", ["error"] = "disconnected" };
        }

        if (!ChatRoomWorldAdapter.TryParseGameplayId(live.Value.NetEntityId, out var sender))
        {
            return new JsonObject { ["ok"] = false, ["kind"] = "Rejected", ["error"] = "invalid_request" };
        }

        if (!gameplay.TryAdmitChat(live.Value.RoomId, sender, text, out var kind, out var chatError))
        {
            return new JsonObject { ["ok"] = false, ["kind"] = kind, ["error"] = chatError };
        }

        return new JsonObject { ["ok"] = true, ["kind"] = kind };
    }

    internal async Task<JsonObject> TickAsync(string? roomId, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new GameplayTickCommand(roomId, completion);
        _ = timers.Schedule(clock.Now, this, command);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new JsonObject { ["ok"] = false, ["error"] = "tick_timeout" };
        }
    }

    internal JsonObject Expire(string netEntityId)
    {
        BindingCensusRow? live = null;
        foreach (var row in registry.ListAllBindings())
        {
            if (string.Equals(row.NetEntityId, netEntityId, StringComparison.Ordinal))
            {
                live = row;
                break;
            }
        }

        var ok = registry.TryTombstone(netEntityId);
        if (ok && live is { } bound
            && ChatRoomWorldAdapter.TryParseGameplayId(netEntityId, out var gameplayId))
        {
            _ = gameplay.DestroyEntity(bound.RoomId, gameplayId);
        }

        lock (gate)
        {
            sessionByNent.Remove(netEntityId);
        }

        return new JsonObject { ["ok"] = ok };
    }

    internal JsonObject Snapshot(string roomId)
    {
        var entities = new JsonArray();
        foreach (var live in registry.ListAllBindings())
        {
            if (!string.Equals(live.RoomId, roomId, StringComparison.Ordinal))
            {
                continue;
            }

            var text = string.Empty;
            var tick = 0UL;
            if (ChatRoomWorldAdapter.TryParseGameplayId(live.NetEntityId, out var gameplayId))
            {
                _ = gameplay.TryGetLastMessage(roomId, gameplayId, out text, out tick);
            }

            entities.Add(new JsonObject
            {
                ["netEntityId"] = live.NetEntityId,
                ["accountId"] = live.AccountId,
                ["entityKind"] = live.EntityType.ToContractValue(),
                ["lastMessageText"] = text,
                ["lastMessageTick"] = tick,
                ["historyCount"] = 0,
            });
        }

        return new JsonObject
        {
            ["roomId"] = roomId,
            ["historyCount"] = 0,
            ["entities"] = entities,
        };
    }

    internal JsonObject Restore(JsonObject body)
    {
        if (!TryReadString(body, "roomId", out var roomId))
        {
            return new JsonObject { ["ok"] = false, ["error"] = "invalid_request" };
        }

        if (body["historyCount"] is JsonValue historyNode
            && historyNode.TryGetValue<int>(out var history)
            && history != 0)
        {
            return new JsonObject { ["ok"] = false, ["error"] = "history_not_persisted" };
        }

        if (body["entities"] is not JsonArray entities)
        {
            return new JsonObject { ["ok"] = false, ["error"] = "invalid_request" };
        }

        foreach (var row in entities)
        {
            if (row is not JsonObject entity)
            {
                continue;
            }

            if (entity["historyCount"] is JsonValue entityHistory
                && entityHistory.TryGetValue<int>(out var count)
                && count != 0)
            {
                return new JsonObject { ["ok"] = false, ["error"] = "history_not_persisted" };
            }
        }

        foreach (var row in entities)
        {
            if (row is not JsonObject entity
                || !TryReadString(entity, "netEntityId", out var nent)
                || !ChatRoomWorldAdapter.TryParseGameplayId(nent, out var gameplayId))
            {
                continue;
            }

            var text = entity["lastMessageText"] is JsonValue textNode && textNode.TryGetValue<string>(out var parsed)
                ? parsed ?? string.Empty
                : string.Empty;
            var tick = entity["lastMessageTick"] is JsonValue tickNode && tickNode.TryGetValue<ulong>(out var tickValue)
                ? tickValue
                : 0UL;
            _ = gameplay.TryRestoreLastMessage(roomId, gameplayId, text, tick);
        }

        return new JsonObject { ["ok"] = true, ["historyCount"] = 0 };
    }

    public EnqueueResult TryEnqueue(in GameplayTickCommand item)
    {
        if (disposed)
        {
            item.Completion.TrySetResult(new JsonObject { ["ok"] = false, ["error"] = "ContextClosing" });
            return new EnqueueResult(EnqueueStatus.Closed, "ContextClosing");
        }

        var admitted = inbox.TryEnqueue(in item);
        if (admitted.Status != EnqueueStatus.Accepted)
        {
            item.Completion.TrySetResult(new JsonObject { ["ok"] = false, ["error"] = admitted.StableErrorId });
            return admitted;
        }

        if (!inbox.TryDequeue(out var command))
        {
            return admitted;
        }

        command.Completion.TrySetResult(RunTickNow(command.RoomId));
        return admitted;
    }

    public bool TryDequeue(out GameplayTickCommand item) => inbox.TryDequeue(out item);

    public void Close() => Dispose();

    public void Dispose()
    {
        disposed = true;
        gameplay.Dispose();
        inbox.Close();
    }

    private JsonObject RunTickNow(string? roomId)
    {
        ulong applied = 0;
        var rooms = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(roomId))
        {
            rooms.Add(roomId);
        }
        else
        {
            foreach (var live in registry.ListAllBindings())
            {
                rooms.Add(live.RoomId);
            }

            foreach (var id in gameplay.RoomIds)
            {
                rooms.Add(id);
            }
        }

        foreach (var room in rooms)
        {
            var tick = gameplay.RunTick(room);
            if (tick.AppliedTick > applied)
            {
                applied = tick.AppliedTick;
            }
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["appliedTick"] = applied,
        };
    }

    private JsonObject ReadLastMessage(ConnectionBinding binding, bool text)
    {
        if (!ChatRoomWorldAdapter.TryParseGameplayId(binding.NetEntityId, out var gameplayId)
            || !gameplay.TryGetLastMessage(binding.RoomId, gameplayId, out var lastText, out var lastTick))
        {
            return QueryJson("non_existent", null);
        }

        return text
            ? QueryJson("ok", lastText)
            : QueryJson("ok", lastTick.ToString(CultureInfo.InvariantCulture));
    }

    private string SessionIdOf(BindingCensusRow live)
    {
        lock (gate)
        {
            if (sessionByNent.TryGetValue(live.NetEntityId, out var session))
            {
                return session;
            }
        }

        return live.ConnectionGeneration > 1
            ? "sess-" + live.LoginName + "-re"
            : "sess-" + live.LoginName;
    }

    private string LoginNameOf(string netEntityId)
    {
        foreach (var row in registry.ListAllBindings())
        {
            if (string.Equals(row.NetEntityId, netEntityId, StringComparison.Ordinal))
            {
                return row.LoginName;
            }
        }

        return "unknown";
    }

    private static JsonObject QueryJson(string outcome, string? value)
    {
        var body = new JsonObject { ["outcome"] = outcome };
        if (value is not null)
        {
            body["value"] = value;
        }

        return body;
    }

    private static string MapResolve(string code)
    {
        if (string.Equals(code, EntityBindingPort.Tombstoned, StringComparison.Ordinal))
        {
            return "tombstoned";
        }

        if (string.Equals(code, EntityBindingPort.StaleGeneration, StringComparison.Ordinal))
        {
            return "stale_generation";
        }

        return "non_existent";
    }

    private static bool IsForbiddenAttribute(string attributeId)
        => attributeId.Contains('(', StringComparison.Ordinal)
            || attributeId.StartsWith("Storage.", StringComparison.Ordinal)
            || attributeId.Contains('/', StringComparison.Ordinal)
            || attributeId.Contains('\\', StringComparison.Ordinal);

    private static bool TryReadString(JsonObject body, string name, out string value)
    {
        value = string.Empty;
        return body[name] is JsonValue node && node.TryGetValue<string>(out var parsed)
            && !string.IsNullOrWhiteSpace(parsed)
            && (value = parsed) is not null;
    }
}
