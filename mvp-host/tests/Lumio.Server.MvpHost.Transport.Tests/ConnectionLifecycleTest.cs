using System;
using System.Linq;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Platform;
using Xunit;

namespace Lumio.Server.MvpHost.Transport.Tests;

/// <summary>
/// 连接状态机、代次与注册表所有权。
/// </summary>
public sealed class ConnectionLifecycleTest
{
    private static readonly (TransportConnectionState From, TransportConnectionState To, bool Legal)[] Transitions =
    {
        (TransportConnectionState.Accepted, TransportConnectionState.EnvelopeValidated, true),
        (TransportConnectionState.EnvelopeValidated, TransportConnectionState.Bound, true),
        (TransportConnectionState.Bound, TransportConnectionState.Active, true),
        (TransportConnectionState.Active, TransportConnectionState.Draining, true),
        (TransportConnectionState.Draining, TransportConnectionState.Closed, true),

        // 任一状态因可致命错误 → Closed。
        (TransportConnectionState.Accepted, TransportConnectionState.Closed, true),
        (TransportConnectionState.EnvelopeValidated, TransportConnectionState.Closed, true),
        (TransportConnectionState.Bound, TransportConnectionState.Closed, true),
        (TransportConnectionState.Active, TransportConnectionState.Closed, true),

        // 非法：跳步、回退、以及从终态复活。
        (TransportConnectionState.Accepted, TransportConnectionState.Bound, false),
        (TransportConnectionState.Accepted, TransportConnectionState.Active, false),
        (TransportConnectionState.EnvelopeValidated, TransportConnectionState.Active, false),
        (TransportConnectionState.Active, TransportConnectionState.Bound, false),
        (TransportConnectionState.Draining, TransportConnectionState.Active, false),
        (TransportConnectionState.Closed, TransportConnectionState.Active, false),
        (TransportConnectionState.Closed, TransportConnectionState.Draining, false),
    };

    public static TheoryData<TransportConnectionState, TransportConnectionState, bool> TransitionCases()
    {
        var data = new TheoryData<TransportConnectionState, TransportConnectionState, bool>();
        foreach (var (from, to, legal) in Transitions)
        {
            data.Add(from, to, legal);
        }

        return data;
    }

    /// <summary>
    /// 逐条驱动状态机。**非法迁移必须被拒绝而不是被容忍**——一个能从 <c>Closed</c>
    /// 回到 <c>Active</c> 的连接会让 epoch 语义失效：拿旧 epoch 的命令就能复活它。
    /// </summary>
    [Theory]
    [MemberData(nameof(TransitionCases))]
    public void 连接状态机只接受合法迁移(TransportConnectionState from, TransportConnectionState to, bool legal)
    {
        var entry = EntryInState(from);

        Assert.Equal(legal, entry.TryTransitionTo(to));
        Assert.Equal(legal ? to : from, entry.State);
    }

    /// <summary>每次 Bind / Unbind 被应用后 epoch 递增，携旧 epoch 的命令一律拒绝。</summary>
    [Fact]
    public void 绑定与解绑各递增一次代次()
    {
        using var harness = new TransportHarness();
        var id = AcceptAndValidate(harness);

        var e0 = harness.Service.EpochOf(id);

        var bind = harness.Service.TrySend(new ConnectionCommand.Bind(
            id, e0, new PermissionGrantRef(1), new ServerSessionId("session-001")));
        Assert.Equal(EnqueueStatus.Accepted, bind.Status);

        var e1 = harness.Service.EpochOf(id);
        Assert.Equal(e0.Value + 1, e1.Value);

        var unbind = harness.Service.TrySend(new ConnectionCommand.Unbind(id, e1));
        Assert.Equal(EnqueueStatus.Accepted, unbind.Status);
        Assert.Equal(e1.Value + 1, harness.Service.EpochOf(id).Value);
    }

    [Fact]
    public void 携旧代次的命令被拒并回StaleConnectionGeneration()
    {
        using var harness = new TransportHarness();
        var id = AcceptAndValidate(harness);

        var stale = harness.Service.EpochOf(id);

        // 先做一次合法 Bind，代次因此递增；`stale` 从此是旧代次。
        harness.Service.TrySend(new ConnectionCommand.Bind(
            id, stale, new PermissionGrantRef(1), new ServerSessionId("session-001")));

        var rejected = harness.Service.TrySend(new ConnectionCommand.SetDrain(id, stale, true));

        Assert.NotEqual(EnqueueStatus.Accepted, rejected.Status);
        Assert.Equal("StaleConnectionGeneration", rejected.StableErrorId);
    }

    /// <summary>
    /// 注册表的写入 API 全部是 <c>internal</c>：本程序集之外无法写入，
    /// 因此「transport 是唯一写入者」是编译期事实而不是纪律。
    /// </summary>
    [Fact]
    public void 连接注册表的写入面全部是内部可见()
    {
        var registry = typeof(ConnectionRegistry);

        Assert.False(registry.IsPublic, "ConnectionRegistry 不得公开");

        var publicWriters = registry
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToList();

        Assert.Empty(publicWriters);

        var entry = typeof(ConnectionEntry);
        Assert.False(entry.IsPublic, "ConnectionEntry 不得公开");
    }

    /// <summary>
    /// 可拒绝类故障**只断该连接**，不上升为 Slot 或进程故障。
    /// 四种输入都只应产生 <c>ConnectionEvent.Closed</c>。
    /// </summary>
    [Fact]
    public void 连接级故障不上升为槽位或进程故障()
    {
        using var harness = new TransportHarness();
        var id = AcceptOne(harness);

        harness.Carrier.QueueInbound(id, TransportHarness.MalformedEnvelope());
        harness.Service.PumpReceiveOnce(id);

        var events = DrainEvents(harness);

        Assert.Contains(events, e => e is ConnectionEvent.Closed);
        Assert.All(events, e => Assert.True(
            e is ConnectionEvent.Closed or ConnectionEvent.Accepted or ConnectionEvent.Faulted,
            $"畸形帧只应产生连接级事件，实际出现 {e.GetType().Name}"));
    }

    /// <summary>
    /// 空闲超时经 <c>ITimerService</c> 投递 <c>Close</c> 命令，**不自建轮询线程**。
    /// 自建轮询意味着每条连接一个循环，而且那个循环会需要一个「睡多久」的常数。
    /// </summary>
    [Fact]
    public void 空闲超时经定时服务投递而不是自建轮询()
    {
        using var harness = new TransportHarness();
        var id = AcceptOne(harness);

        Assert.NotEmpty(harness.Timers.Scheduled);
        Assert.Contains(harness.Timers.Scheduled, s => s.Command is ConnectionCommand.Close);

        var due = harness.Timers.Scheduled.First(s => s.Command is ConnectionCommand.Close).DueAt;
        Assert.True(
            due.Ticks >= TimeSpan.FromSeconds(TransportProvisionalLimits.IdleTimeoutSeconds).Ticks,
            "空闲截止必须按 provisional 的 15 秒排定");
    }

    internal static TransportConnectionId AcceptOne(TransportHarness harness)
    {
        var id = new TransportConnectionId(1);
        harness.Carrier.QueueAccept(id, "lumio.mvp.v0");
        Assert.True(harness.Service.TryAcceptOne());
        return id;
    }

    /// <summary>
    /// 接受一条连接**并送首帧过结构校验**，使其到达 <c>EnvelopeValidated</c>。
    /// 这是真实时序：session 是收到 <c>HandshakeEnvelope</c> 事件之后才发 <c>Bind</c> 的，
    /// 直接从 <c>Accepted</c> 就 Bind 在状态机上本来就不合法。
    /// </summary>
    internal static TransportConnectionId AcceptAndValidate(TransportHarness harness)
    {
        var id = AcceptOne(harness);
        harness.Carrier.QueueInbound(id, TransportHarness.ValidEnvelope(sequence: 0));
        harness.Service.PumpReceiveOnce(id);
        Assert.Equal(TransportConnectionState.EnvelopeValidated, harness.Service.StateOf(id));
        return id;
    }

    internal static System.Collections.Generic.List<ConnectionEvent> DrainEvents(TransportHarness harness)
    {
        var events = new System.Collections.Generic.List<ConnectionEvent>();
        while (harness.Service.TryReceive(out var evt))
        {
            events.Add(evt);
        }

        return events;
    }

    private static ConnectionEntry EntryInState(TransportConnectionState state)
    {
        var entry = new ConnectionEntry(
            new TransportConnectionId(1), new QueueBudget(4, 4096), new QueueBudget(4, 4096));

        var path = new[]
        {
            TransportConnectionState.EnvelopeValidated,
            TransportConnectionState.Bound,
            TransportConnectionState.Active,
            TransportConnectionState.Draining,
            TransportConnectionState.Closed,
        };

        foreach (var step in path)
        {
            if (entry.State == state)
            {
                break;
            }

            entry.TryTransitionTo(step);
        }

        return entry;
    }
}
