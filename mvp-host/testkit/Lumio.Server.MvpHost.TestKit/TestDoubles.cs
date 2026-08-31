using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.TestKit;

/// <summary>
/// 可推进的虚拟时钟。测试里推进时间必须是**显式动作**——
/// 睡真实时间既慢又不确定，而且会把「等多久才算够」变成一个猜。
/// </summary>
public sealed class FakeMonotonicClock : IMonotonicClock
{
    private long ticks;

    public MonotonicInstant Now => new(Interlocked.Read(ref this.ticks));

    public void Advance(long ticks)
    {
        if (ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "单调时钟不得回退");
        }

        Interlocked.Add(ref this.ticks, ticks);
    }
}

/// <summary>
/// 墙钟替身。返回固定串，使 audit / diagnostic 的 <c>timestamp</c> 在测试里可断言。
/// 取值必须匹配 <c>common.schema.json#/$defs/timestamp</c>，否则产出的事件过不了 schema。
/// </summary>
public sealed class FakeWallClock : IWallClock
{
    private string value;

    public FakeWallClock(string fixedUtcIso8601) => this.value = fixedUtcIso8601;

    public string UtcIso8601Now() => this.value;

    public void Set(string utcIso8601) => this.value = utcIso8601;
}

/// <summary>把 <see cref="IHostTraceSink"/> 的三类调用记进内存列表供断言。</summary>
public sealed class RecordingHostTraceSink : IHostTraceSink
{
    private readonly List<AuditRecord> audits = new();
    private readonly List<(string Effect, ulong? AdmissionAttemptId, ulong? SlotEpoch, ulong? ConnectionEpoch)> acks = new();
    private readonly List<(string? SessionId, string? SessionState, ulong? AuthorityRevision, ulong? SlotEpoch, ulong? GrantEpoch)> states = new();

    public IReadOnlyList<AuditRecord> Audits => this.audits;

    public IReadOnlyList<(string Effect, ulong? AdmissionAttemptId, ulong? SlotEpoch, ulong? ConnectionEpoch)> Acks => this.acks;

    public IReadOnlyList<(string? SessionId, string? SessionState, ulong? AuthorityRevision, ulong? SlotEpoch, ulong? GrantEpoch)> States => this.states;

    public void Audit(in AuditRecord record) => this.audits.Add(record);

    public void Ack(string effect, ulong? admissionAttemptId, ulong? slotEpoch, ulong? connectionEpoch)
        => this.acks.Add((effect, admissionAttemptId, slotEpoch, connectionEpoch));

    public void State(string? sessionId, string? sessionState, ulong? authorityRevision, ulong? slotEpoch, ulong? grantEpoch)
        => this.states.Add((sessionId, sessionState, authorityRevision, slotEpoch, grantEpoch));
}

/// <summary>
/// 镜像 fixture 的装载器。测试进程的 cwd 由 runner 决定、不可依赖，
/// 因此从程序集所在目录逐级向上找哨兵文件。
/// </summary>
public static class ContractMirrorFixtures
{
    private static readonly string MvpHostRoot = Locate();

    public static ReadOnlyMemory<byte> Load(string relativePath)
        => File.ReadAllBytes(Path.Combine(MvpHostRoot, "contract-mirror", relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// 枚举目录而不是写死清单：镜像清单会随上游 additive 增补变长，
    /// 写死条数的测试在上游加一条时会安静地漏掉它。
    /// </summary>
    public static IReadOnlyList<string> ValidReplicationFixtures { get; } = Enumerate("valid");

    public static IReadOnlyList<string> InvalidReplicationFixtures { get; } = Enumerate("invalid");

    private static List<string> Enumerate(string bucket)
        => Directory.EnumerateFiles(Path.Combine(MvpHostRoot, "contract-mirror", "fixtures", bucket), "replication-*.json")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "eng", "verify-all.sh")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"从 {AppContext.BaseDirectory} 向上找不到 mvp-host 根（哨兵 eng/verify-all.sh）。");
    }
}

/// <summary>
/// 内存 <see cref="IByteCarrier"/>。**只替换载体**——Envelope 校验、Auth、Permission、
/// Size、Queue、Tick Barrier 在内存环回下走的仍是同一条路径，
/// 否则「本地能过、上线就炸」正是这种替身会掩盖的失败。
/// </summary>
public sealed class InMemoryByteCarrier : IByteCarrier
{
    private readonly Queue<TransportConnectionId> pendingAccepts = new();
    private readonly Dictionary<ulong, Queue<byte[]>> inbound = new();
    private readonly List<(TransportConnectionId Connection, byte[] Bytes)> sent = new();
    private readonly HashSet<ulong> closed = new();
    private readonly List<(TransportConnectionId Connection, ConnectionCloseReason Reason)> closeCalls = new();
    private readonly List<(string Operation, TransportConnectionId Connection)> operations = new();

    public IReadOnlyList<(TransportConnectionId Connection, byte[] Bytes)> Sent => this.sent;

    public IReadOnlyList<(TransportConnectionId Connection, ConnectionCloseReason Reason)> CloseCalls
        => this.closeCalls;

    public IReadOnlyList<(string Operation, TransportConnectionId Connection)> Operations
        => this.operations;

    public void QueueAccept(TransportConnectionId id, params string[] requestedSubprotocols)
    {
        this.pendingAccepts.Enqueue(id);
        this.Subprotocols[id.Value] = requestedSubprotocols.ToImmutableArray();
    }

    public Dictionary<ulong, ImmutableArray<string>> Subprotocols { get; } = new();

    public void QueueInbound(TransportConnectionId id, ReadOnlyMemory<byte> bytes)
    {
        if (!this.inbound.TryGetValue(id.Value, out var queue))
        {
            queue = new Queue<byte[]>();
            this.inbound[id.Value] = queue;
        }

        queue.Enqueue(bytes.ToArray());
    }

    public ValueTask<CarrierAccept> AcceptAsync(CancellationToken ct)
    {
        if (this.pendingAccepts.Count == 0)
        {
            return ValueTask.FromResult(new CarrierAccept(false, default, ImmutableArray<string>.Empty));
        }

        var id = this.pendingAccepts.Dequeue();
        var subprotocols = this.Subprotocols.TryGetValue(id.Value, out var s) ? s : ImmutableArray<string>.Empty;
        return ValueTask.FromResult(new CarrierAccept(true, id, subprotocols));
    }

    public ValueTask<CarrierReceive> ReceiveAsync(TransportConnectionId c, Memory<byte> buffer, CancellationToken ct)
    {
        if (this.closed.Contains(c.Value))
        {
            return ValueTask.FromResult(new CarrierReceive(false, 0, true, true));
        }

        if (!this.inbound.TryGetValue(c.Value, out var queue) || queue.Count == 0)
        {
            return ValueTask.FromResult(new CarrierReceive(false, 0, false, false));
        }

        var message = queue.Dequeue();
        message.AsSpan().CopyTo(buffer.Span);
        return ValueTask.FromResult(new CarrierReceive(true, message.Length, true, false));
    }

    public bool TrySend(TransportConnectionId c, ReadOnlyMemory<byte> bytes)
    {
        if (this.closed.Contains(c.Value))
        {
            return false;
        }

        this.sent.Add((c, bytes.ToArray()));
        this.operations.Add(("Send", c));
        return true;
    }

    public bool Close(TransportConnectionId c, ConnectionCloseReason reason)
    {
        this.closeCalls.Add((c, reason));
        this.operations.Add(("Close", c));
        return this.closed.Add(c.Value);
    }
}

/// <summary>
/// 可脚本化的故障策略。故障注入**在组装期注入**，生产 Profile 固定 pass-through；
/// 本实现只存在于 TestKit，因此「生产里能不能注入故障」是构建图上可判的事实。
/// </summary>
public sealed class ScriptedTransportFaultPolicy : ITransportFaultPolicy
{
    private TransportFaultAction[] script = Array.Empty<TransportFaultAction>();
    private int cursor;

    public void Script(params TransportFaultAction[] actions)
    {
        this.script = actions ?? Array.Empty<TransportFaultAction>();
        this.cursor = 0;
    }

    /// <summary>脚本用尽后恒 <c>Pass</c>——测试只脚本化它关心的那几步。</summary>
    public TransportFaultAction Decide(in TransportFaultContext ctx)
        => this.cursor < this.script.Length ? this.script[this.cursor++] : TransportFaultAction.Pass;
}
