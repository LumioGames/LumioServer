using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.TestKit;
using Xunit;

namespace Lumio.Server.MvpHost.Transport.Tests;

/// <summary>
/// 四条有界队列的满载与关闭语义，以及分配前拒绝。
/// </summary>
public sealed class BoundedQueueTest
{
    /// <summary>
    /// **Reliable 满载断开连接，绝不静默丢弃**；Unreliable 满载丢弃并计数、连接存活。
    ///
    /// 两者的区别是整条设计里最容易被实现成「都丢掉算了」的一处：
    /// 静默丢一条 Reliable，对端不会知道，它会一直等一个永远不来的状态。
    /// </summary>
    [Fact]
    public void 可靠消息在队列满时断开连接()
    {
        using var harness = new TransportHarness();
        var id = ConnectionLifecycleTest.AcceptAndValidate(harness);

        FillIngress(harness, id);

        harness.Carrier.QueueInbound(id, TransportHarness.ValidEnvelope(sequence: 999, reliability: "Reliable"));
        harness.Service.PumpReceiveOnce(id);

        var events = ConnectionLifecycleTest.DrainEvents(harness);
        var closed = events.OfType<ConnectionEvent.Closed>().LastOrDefault();

        Assert.NotNull(closed);
        Assert.Equal(ConnectionCloseReason.Fault, closed.Reason);
        Assert.Equal(TransportConnectionState.Closed, harness.Service.StateOf(id));
    }

    [Fact]
    public void ClosedConnectionEntryIsRetiredAfterTerminalEventPublication()
    {
        using var harness = new TransportHarness();
        var id = ConnectionLifecycleTest.AcceptOne(harness);
        Assert.Equal(1, harness.Service.ConnectionCountForTest);

        harness.Service.RaiseClosedForTest(id, ConnectionCloseReason.OwnerRequest);

        Assert.Equal(0, harness.Service.ConnectionCountForTest);
        Assert.Equal(TransportConnectionState.Closed, harness.Service.StateOf(id));
    }

    [Fact]
    public void DisposeClosesCarrierAndCancelsIdleTimerBeforeRetiringEntry()
    {
        using var harness = new TransportHarness();
        var id = ConnectionLifecycleTest.AcceptOne(harness);

        harness.Service.Dispose();

        Assert.Equal(0, harness.Service.ConnectionCountForTest);
        Assert.Contains(
            harness.Carrier.CloseCalls,
            call => call.Connection == id && call.Reason == ConnectionCloseReason.OwnerRequest);
        Assert.NotEmpty(harness.Timers.Canceled);
    }

    [Fact]
    public void DisposePublishesOneTerminalEventBeforeRetiringEachConnection()
    {
        using var harness = new TransportHarness();
        var id = ConnectionLifecycleTest.AcceptOne(harness);
        _ = ConnectionLifecycleTest.DrainEvents(harness);

        harness.Service.Dispose();

        var events = ConnectionLifecycleTest.DrainEvents(harness);
        var terminal = events
            .Where(evt => evt is ConnectionEvent.Closed or ConnectionEvent.Faulted)
            .Where(evt => evt switch
            {
                ConnectionEvent.Closed closed => closed.Id == id,
                ConnectionEvent.Faulted faulted => faulted.Id == id,
                _ => false,
            })
            .ToArray();

        var closed = Assert.Single(terminal);
        var closedEvent = Assert.IsType<ConnectionEvent.Closed>(closed);
        Assert.Equal(ConnectionCloseReason.OwnerRequest, closedEvent.Reason);
        Assert.Equal(0, harness.Service.ConnectionCountForTest);
        Assert.Equal(EnqueueStatus.Closed, harness.Service.TrySend(
            new ConnectionCommand.SetDrain(id, new ConnectionEpoch(0), true)).Status);
    }

    [Fact]
    public void ConcurrentDisposeConvergesToOneTerminalEventAndOneCarrierClose()
    {
        using var harness = new TransportHarness();
        var id = ConnectionLifecycleTest.AcceptOne(harness);
        _ = ConnectionLifecycleTest.DrainEvents(harness);

        Parallel.Invoke(harness.Service.Dispose, harness.Service.Dispose, harness.Service.Dispose);

        var terminals = ConnectionLifecycleTest.DrainEvents(harness)
            .Where(evt => evt is ConnectionEvent.Closed or ConnectionEvent.Faulted)
            .Where(evt => evt switch
            {
                ConnectionEvent.Closed closed => closed.Id == id,
                ConnectionEvent.Faulted faulted => faulted.Id == id,
                _ => false,
            })
            .ToArray();
        Assert.Single(terminals);
        Assert.Single(harness.Carrier.CloseCalls, call => call.Connection == id);
    }

    [Fact]
    public void DisposedTransportRejectsLaterAcceptsAndClosesTheCarrier()
    {
        using var harness = new TransportHarness();
        var first = ConnectionLifecycleTest.AcceptOne(harness);
        harness.Service.Dispose();

        var second = new TransportConnectionId(2);
        harness.Carrier.QueueAccept(second, "lumio.mvp.v0");

        Assert.False(harness.Service.TryAcceptOne());
        Assert.Contains(
            harness.Carrier.CloseCalls,
            call => call.Connection == second && call.Reason == ConnectionCloseReason.PolicyReject);
        Assert.Equal(TransportConnectionState.Closed, harness.Service.StateOf(first));
    }

    [Fact]
    public void DisposeStillCancelsTimersWhenCarrierCloseThrows()
    {
        using var harness = new TransportHarness(
            carrierDecorator: inner => new ThrowingCloseCarrier(inner));
        var id = ConnectionLifecycleTest.AcceptOne(harness);

        var error = Record.Exception(harness.Service.Dispose);

        Assert.Null(error);
        Assert.Equal(0, harness.Service.ConnectionCountForTest);
        Assert.NotEmpty(harness.Timers.Canceled);
        Assert.Equal(TransportConnectionState.Closed, harness.Service.StateOf(id));
    }

    [Fact]
    public void 不可靠消息在队列满时丢弃并计数且连接存活()
    {
        using var harness = new TransportHarness();
        var id = ConnectionLifecycleTest.AcceptAndValidate(harness);

        FillIngress(harness, id);
        var before = harness.Service.UnreliableDropCountOf(id);

        harness.Carrier.QueueInbound(id, TransportHarness.ValidEnvelope(sequence: 999, reliability: "Unreliable"));
        harness.Service.PumpReceiveOnce(id);

        Assert.Equal(before + 1, harness.Service.UnreliableDropCountOf(id));
        Assert.NotEqual(TransportConnectionState.Closed, harness.Service.StateOf(id));
    }

    [Fact]
    public void IngressRejectsByteBudgetBeforeItemBudgetAndReleasesBytesOnTake()
    {
        var entry = CreateEntry(ingressMaxBytes: 5, egressMaxBytes: 32);
        var first = IngressBytes(3);
        var second = IngressBytes(3);

        Assert.Equal(EnqueueStatus.Accepted, entry.TryEnqueueIngress(in first).Status);

        var rejected = entry.TryEnqueueIngress(in second);
        Assert.Equal(EnqueueStatus.Full, rejected.Status);
        Assert.Equal("QueueFull", rejected.StableErrorId);

        Assert.True(entry.TryTakeIngress(out _));
        entry.CommitIngressTake();
        Assert.Equal(EnqueueStatus.Accepted, entry.TryEnqueueIngress(in second).Status);
    }

    [Fact]
    public void DeferredIngressCountsItsBytesExactlyOnceAndRemainsFirst()
    {
        var entry = CreateEntry(ingressMaxBytes: 6, egressMaxBytes: 32);
        var first = IngressBytes(3, sequence: 1);
        var second = IngressBytes(3, sequence: 2);
        var overflow = IngressBytes(1, sequence: 3);

        Assert.Equal(EnqueueStatus.Accepted, entry.TryEnqueueIngress(in first).Status);
        Assert.True(entry.TryTakeIngress(out var taken));
        entry.DeferIngress(in taken);

        Assert.Equal(EnqueueStatus.Accepted, entry.TryEnqueueIngress(in second).Status);
        Assert.Equal(EnqueueStatus.Full, entry.TryEnqueueIngress(in overflow).Status);
        Assert.True(entry.TryTakeIngress(out var deferred));
        Assert.Equal((ulong)1, deferred.Header.Sequence);
        entry.CommitIngressTake();
        Assert.True(entry.TryTakeIngress(out var queued));
        Assert.Equal((ulong)2, queued.Header.Sequence);
        entry.CommitIngressTake();
    }

    [Fact]
    public void InFlightIngressKeepsItsBudgetUntilCommittedOrDeferred()
    {
        var entry = CreateEntry(ingressMaxBytes: 3, egressMaxBytes: 32);
        var fullBudget = IngressBytes(3);
        var extra = IngressBytes(1);
        Assert.Equal(EnqueueStatus.Accepted, entry.TryEnqueueIngress(in fullBudget).Status);
        Assert.True(entry.TryTakeIngress(out var taken));

        Assert.Equal(EnqueueStatus.Full, entry.TryEnqueueIngress(in extra).Status);
        entry.DeferIngress(in taken);
        Assert.Equal(EnqueueStatus.Full, entry.TryEnqueueIngress(in extra).Status);
    }

    [Fact]
    public void EgressRejectsByteBudgetBeforeItemBudgetAndReleasesBytesOnTake()
    {
        var entry = CreateEntry(ingressMaxBytes: 32, egressMaxBytes: 5);
        var first = EgressBytes(3);
        var second = EgressBytes(3);

        Assert.Equal(EnqueueStatus.Accepted, entry.TryEnqueueEgress(in first).Status);

        var rejected = entry.TryEnqueueEgress(in second);
        Assert.Equal(EnqueueStatus.Full, rejected.Status);
        Assert.Equal("QueueFull", rejected.StableErrorId);

        Assert.True(entry.TryTakeEgress(out _));
        entry.CommitEgressTake();
        Assert.Equal(EnqueueStatus.Accepted, entry.TryEnqueueEgress(in second).Status);
    }

    [Fact]
    public void RateLimitRefillsAtSteadyRateAfterBurstIsExhausted()
    {
        var entry = CreateEntry(ingressMaxBytes: 32, egressMaxBytes: 32);
        var now = new MonotonicInstant(0);

        for (var i = 0; i < TransportProvisionalLimits.InboundBurst; i++)
        {
            Assert.True(entry.TryAdmitInbound(now));
        }

        Assert.False(entry.TryAdmitInbound(now));
        now = new MonotonicInstant(TimeSpan.TicksPerSecond);
        for (var i = 0; i < TransportProvisionalLimits.InboundMessagesPerSecond; i++)
        {
            Assert.True(entry.TryAdmitInbound(now));
        }

        Assert.False(entry.TryAdmitInbound(now));
    }

    [Fact]
    public void DeferredEgressCountsItsBytesExactlyOnceAndRemainsFirst()
    {
        var entry = CreateEntry(ingressMaxBytes: 32, egressMaxBytes: 6);
        var first = EgressBytes(3, marker: 1);
        var second = EgressBytes(3, marker: 2);
        var overflow = EgressBytes(1, marker: 3);

        Assert.Equal(EnqueueStatus.Accepted, entry.TryEnqueueEgress(in first).Status);
        Assert.True(entry.TryTakeEgress(out var taken));
        entry.DeferEgress(in taken);

        Assert.Equal(EnqueueStatus.Accepted, entry.TryEnqueueEgress(in second).Status);
        Assert.Equal(EnqueueStatus.Full, entry.TryEnqueueEgress(in overflow).Status);
        Assert.True(entry.TryTakeEgress(out var deferred));
        Assert.Equal(1, deferred.Bytes.Span[0]);
        entry.CommitEgressTake();
        Assert.True(entry.TryTakeEgress(out var queued));
        Assert.Equal(2, queued.Bytes.Span[0]);
        entry.CommitEgressTake();
    }

    [Fact]
    public void InFlightEgressKeepsItsBudgetUntilCommittedOrDeferred()
    {
        var entry = CreateEntry(ingressMaxBytes: 32, egressMaxBytes: 3);
        var fullBudget = EgressBytes(3);
        var extra = EgressBytes(1);
        Assert.Equal(EnqueueStatus.Accepted, entry.TryEnqueueEgress(in fullBudget).Status);
        Assert.True(entry.TryTakeEgress(out var taken));

        Assert.Equal(EnqueueStatus.Full, entry.TryEnqueueEgress(in extra).Status);
        entry.DeferEgress(in taken);
        Assert.Equal(EnqueueStatus.Full, entry.TryEnqueueEgress(in extra).Status);
    }

    [Fact]
    public void ClosingCleanupDrainsQueuedAndDeferredIngressAndEgress()
    {
        var entry = CreateEntry(ingressMaxBytes: 32, egressMaxBytes: 32);
        var ingress = IngressBytes(3, sequence: 1);
        var egress = EgressBytes(3, marker: 1);

        Assert.Equal(EnqueueStatus.Accepted, entry.TryEnqueueIngress(in ingress).Status);
        Assert.True(entry.TryTakeIngress(out var takenIngress));
        entry.DeferIngress(in takenIngress);
        Assert.Equal(EnqueueStatus.Accepted, entry.TryEnqueueIngress(in ingress).Status);

        Assert.Equal(EnqueueStatus.Accepted, entry.TryEnqueueEgress(in egress).Status);
        Assert.True(entry.TryTakeEgress(out var takenEgress));
        entry.DeferEgress(in takenEgress);
        Assert.Equal(EnqueueStatus.Accepted, entry.TryEnqueueEgress(in egress).Status);

        entry.ClearDeferredIngress();
        entry.ClearDeferredEgress();

        Assert.Equal(0, entry.IngressCount);
        Assert.Equal(0, entry.EgressCount);
    }

    [Fact]
    public void AuthenticationMetadataConsumptionIsAtomicAndOneShot()
    {
        for (var iteration = 0; iteration < 2_000; iteration++)
        {
            var entry = CreateEntry(ingressMaxBytes: 32, egressMaxBytes: 32);
            entry.SetAuthenticationMetadata(
                new PrincipalId("principal"),
                "A",
                "A-1.1.0");
            var results = new bool[2];

            Parallel.Invoke(
                () => results[0] = entry.TryTakeAuthenticationMetadata(
                    out _,
                    out _,
                    out _),
                () => results[1] = entry.TryTakeAuthenticationMetadata(
                    out _,
                    out _,
                    out _));

            Assert.Equal(1, results.Count(value => value));
        }
    }

    /// <summary>
    /// 终态事件走**保留槽**，队列填满也必达；非终态事件在同样条件下关闭该连接并写 diagnostic。
    /// 丢一个 <c>Closed</c> 的后果是 session 侧永远留着一条已经不存在的连接。
    /// </summary>
    [Fact]
    public void 终态事件在事件队列满时仍经保留槽投递()
    {
        using var harness = new TransportHarness();
        var id = ConnectionLifecycleTest.AcceptOne(harness);

        harness.Service.FillEventOutboxForTest();

        harness.Service.RaiseClosedForTest(id, ConnectionCloseReason.OwnerRequest);

        var events = ConnectionLifecycleTest.DrainEvents(harness);
        Assert.Contains(events, e => e is ConnectionEvent.Closed);
    }

    [Fact]
    public void FullEventOutboxRetainsFaultAndCloseForEveryLiveConnection()
    {
        using var harness = new TransportHarness();
        var first = new TransportConnectionId(1);
        var second = new TransportConnectionId(2);
        harness.Carrier.QueueAccept(first, "lumio.mvp.v0");
        harness.Carrier.QueueAccept(second, "lumio.mvp.v0");
        Assert.True(harness.Service.TryAcceptOne());
        Assert.True(harness.Service.TryAcceptOne());
        _ = ConnectionLifecycleTest.DrainEvents(harness);
        harness.Service.FillEventOutboxForTest();

        harness.Carrier.QueueInbound(first, TransportHarness.MalformedEnvelope());
        harness.Carrier.QueueInbound(second, TransportHarness.MalformedEnvelope());
        Assert.True(harness.Service.PumpReceiveOnce(first));
        Assert.True(harness.Service.PumpReceiveOnce(second));

        Assert.Equal(0, harness.Service.ConnectionCountForTest);
        var events = ConnectionLifecycleTest.DrainEvents(harness);
        Assert.Equal(2, events.OfType<ConnectionEvent.Faulted>().Count());
        Assert.Equal(2, events.OfType<ConnectionEvent.Closed>().Count());
    }

    [Fact]
    public void TerminalBacklogRejectsChurnUntilReservedEventsAreConsumed()
    {
        using var harness = new TransportHarness(maxConnections: 2);
        var first = new TransportConnectionId(1);
        var second = new TransportConnectionId(2);
        harness.Carrier.QueueAccept(first, "lumio.mvp.v0");
        harness.Carrier.QueueAccept(second, "lumio.mvp.v0");
        Assert.True(harness.Service.TryAcceptOne());
        Assert.True(harness.Service.TryAcceptOne());
        _ = ConnectionLifecycleTest.DrainEvents(harness);
        harness.Service.FillEventOutboxForTest();
        harness.Carrier.QueueInbound(first, TransportHarness.MalformedEnvelope());
        harness.Carrier.QueueInbound(second, TransportHarness.MalformedEnvelope());
        Assert.True(harness.Service.PumpReceiveOnce(first));
        Assert.True(harness.Service.PumpReceiveOnce(second));

        var rejected = new TransportConnectionId(3);
        harness.Carrier.QueueAccept(rejected, "lumio.mvp.v0");
        Assert.False(harness.Service.TryAcceptOne());
        Assert.Contains(
            harness.Carrier.CloseCalls,
            call => call.Connection == rejected && call.Reason == ConnectionCloseReason.PolicyReject);

        _ = ConnectionLifecycleTest.DrainEvents(harness);
        var accepted = new TransportConnectionId(4);
        harness.Carrier.QueueAccept(accepted, "lumio.mvp.v0");
        Assert.True(harness.Service.TryAcceptOne());
        Assert.Equal(1, harness.Service.ConnectionCountForTest);
    }

    [Fact]
    public void 非终态事件在事件队列满时关闭连接并写诊断()
    {
        using var harness = new TransportHarness();
        var id = ConnectionLifecycleTest.AcceptOne(harness);

        harness.Service.FillEventOutboxForTest();
        harness.Service.RaiseBackpressuredForTest(id);

        Assert.Equal(TransportConnectionState.Closed, harness.Service.StateOf(id));
        Assert.Equal(0, harness.Service.ConnectionCountForTest);
        Assert.Contains(
            harness.Carrier.CloseCalls,
            call => call.Connection == id && call.Reason == ConnectionCloseReason.Fault);
        Assert.NotEmpty(harness.Timers.Canceled);
        Assert.True(harness.DiagnosticInbox.TryDequeue(out _), "非终态事件被丢弃时必须留下一条 diagnostic");

        var events = ConnectionLifecycleTest.DrainEvents(harness);
        Assert.Contains(events, e => e is ConnectionEvent.Closed);
    }

    /// <summary>
    /// **分配前拒绝**：声明长度超过 <c>MaxMessageBytes</c> 时立即中止读取并关闭连接，
    /// 且过程中**没有分配过等于声明长度的缓冲**。
    ///
    /// 只断言「最后拒绝了」是不够的——实现完全可以先按声明长度分配再拒绝，
    /// 而那正是对端用一个数字就能打爆内存的路径。
    /// </summary>
    [Fact]
    public void 超限消息在物化之前被拒且从未按声明长度分配()
    {
        var carrier = new AccountingByteCarrier();
        using var harness = new TransportHarness(maxMessageBytes: 4096);

        var oversize = TransportHarness.OversizeDeclaredEnvelope(declaredLength: 64 * 1024 * 1024);
        var id = ConnectionLifecycleTest.AcceptOne(harness);
        _ = carrier;

        harness.Carrier.QueueInbound(id, oversize);
        harness.Service.PumpReceiveOnce(id);

        Assert.Equal(TransportConnectionState.Closed, harness.Service.StateOf(id));

        Assert.All(
            harness.Service.ReceiveBufferSizesForTest,
            size => Assert.True(
                size <= TransportProvisionalLimits.ReceiveBufferBytes,
                $"单次分配 {size} 字节，超过接收缓冲上限——这是按声明长度分配的痕迹"));
    }

    /// <summary>四条队列的七项合同字段齐全，且名字与设计 §6.1 的表逐条对应。</summary>
    [Theory]
    [InlineData("MvpIngressQueue")]
    [InlineData("MvpEgressQueue")]
    [InlineData("MvpConnectionCommandInbox")]
    [InlineData("MvpTransportEventOutbox")]
    public void 四条队列都已登记且合同字段齐全(string queueName)
    {
        var path = Path.Combine(
            RepoPaths.MvpHostRoot, "src", "Lumio.Server.MvpHost.Transport", "queues.json");

        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        var entry = (doc["queues"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .FirstOrDefault(q => q["name"]?.GetValue<string>() == queueName);

        Assert.NotNull(entry);

        foreach (var field in new[] { "owner", "producer", "consumer", "ordering", "budget", "onFull", "onClose" })
        {
            Assert.True(
                entry[field] is not null && !string.IsNullOrWhiteSpace(entry[field]!.ToString()),
                $"{queueName} 缺合同字段 {field}");
        }
    }

    /// <summary>
    /// <c>MvpTransportEventOutbox</c> 的**所有者是 session**，不是 transport——
    /// 消费者拥有队列，因此「终态永不丢弃」是消费者的保证，不是生产者的自觉。
    /// </summary>
    [Fact]
    public void 事件出箱的所有者登记为session()
    {
        var path = Path.Combine(
            RepoPaths.MvpHostRoot, "src", "Lumio.Server.MvpHost.Transport", "queues.json");

        var entry = (JsonNode.Parse(File.ReadAllText(path))!["queues"] as JsonArray)!
            .OfType<JsonObject>()
            .First(q => q["name"]?.GetValue<string>() == "MvpTransportEventOutbox");

        Assert.Contains("Session", entry["owner"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 填满 ingress，**用 Unreliable 消息填**。
    ///
    /// 两处非显然的细节，都是实测撞出来的：
    /// ① 每条之间推进一秒虚拟时间——入站限流的突发上限（128）小于 ingress 容量（256），
    ///    不推进时钟会先撞限流被断连，永远填不满。限流本就是每秒窗口，推进时间就是让窗口正常翻页。
    /// ② 必须用 Unreliable 填——用 Reliable 填的话，**填满的那一条自己就会触发断连**
    ///    （这正是被测的设计行为），于是「填满」和「测满载行为」互相踩踏。
    ///    Unreliable 满载只丢弃并计数，连接存活，队列因此能停在满的状态上。
    ///
    /// 判满的依据取**丢弃计数出现增长**而不是 count 达到容量：后者依赖 Channel 内部
    /// 计数的时序，前者是队列自己给出的「我满了」的答复。
    /// </summary>
    [Fact]
    public void DrainDoesNotDropTheFirstItemThatExceedsTheByteBudget()
    {
        using var harness = new TransportHarness();
        var id = ConnectionLifecycleTest.AcceptAndValidate(harness);
        harness.Carrier.QueueInbound(id, TransportHarness.ValidEnvelope(sequence: 2));
        harness.Service.PumpReceiveOnce(id);
        var expected = TransportHarness.ValidEnvelope(sequence: 2);

        var tooSmall = new ValidatedEnvelopeBytes[1];
        var taken = harness.Service.Drain(id, 1, expected.Length - 1L, tooSmall.AsSpan());

        Assert.Equal(0, taken);
        var enough = new ValidatedEnvelopeBytes[1];
        taken = harness.Service.Drain(id, 1, expected.Length, enough.AsSpan());

        Assert.Equal(1, taken);
        Assert.Equal((ulong)2, enough[0].Header.Sequence);
    }

    [Fact]
    public void DrainRetainsFifoWhenALaterItemWouldExceedTheRemainingBudget()
    {
        using var harness = new TransportHarness();
        var id = ConnectionLifecycleTest.AcceptAndValidate(harness);
        harness.Carrier.QueueInbound(id, TransportHarness.ValidEnvelope(sequence: 2));
        harness.Carrier.QueueInbound(id, TransportHarness.ValidEnvelope(sequence: 3));
        harness.Service.PumpReceiveOnce(id);
        harness.Service.PumpReceiveOnce(id);
        var itemBytes = TransportHarness.ValidEnvelope(sequence: 2).Length;

        var destination = new ValidatedEnvelopeBytes[2];
        var taken = harness.Service.Drain(id, 2, itemBytes + itemBytes - 1L, destination.AsSpan());

        Assert.Equal(1, taken);
        Assert.Equal((ulong)2, destination[0].Header.Sequence);

        var remainder = new ValidatedEnvelopeBytes[1];
        taken = harness.Service.Drain(id, 1, itemBytes, remainder.AsSpan());

        Assert.Equal(1, taken);
        Assert.Equal((ulong)3, remainder[0].Header.Sequence);
    }

    private static void FillIngress(TransportHarness harness, TransportConnectionId id)
    {
        for (var i = 0; i < 1024; i++)
        {
            harness.Clock.Advance(TimeSpan.TicksPerSecond);
            harness.Carrier.QueueInbound(
                id, TransportHarness.ValidEnvelope(sequence: (ulong)(i + 1), reliability: "Unreliable"));
            harness.Service.PumpReceiveOnce(id);

            // ③ 每轮排空事件出箱，模拟 session 在持续消费事件。
            //    不排空的话，事件出箱（256）会先于 ingress（256）填满，
            //    触发「非终态事件满载 → 关闭该连接」——那是被测的另一条正确行为，
            //    但在这里会把连接提前关掉，让本测试永远等不到 ingress 满。
            ConnectionLifecycleTest.DrainEvents(harness);

            if (harness.Service.UnreliableDropCountOf(id) > 0)
            {
                return;
            }
        }

        Assert.Fail("1024 条之内没能填满 ingress 队列——预算配置或计数有问题");
    }

    private static ConnectionEntry CreateEntry(long ingressMaxBytes, long egressMaxBytes)
        => new(
            new TransportConnectionId(1),
            new QueueBudget(4, ingressMaxBytes),
            new QueueBudget(4, egressMaxBytes));

    private static ValidatedEnvelopeBytes IngressBytes(int length, ulong sequence = 0)
        => new(
            new byte[length],
            default(Lumio.Server.MvpHost.Wire.EnvelopeHeaderView) with { Sequence = sequence });

    private static OutboundEnvelopeBytes EgressBytes(int length, byte marker = 0)
    {
        var bytes = new byte[length];
        if (length > 0)
        {
            bytes[0] = marker;
        }

        return new OutboundEnvelopeBytes(bytes);
    }

    private sealed class ThrowingCloseCarrier(InMemoryByteCarrier inner) : IByteCarrier
    {
        public ValueTask<CarrierAccept> AcceptAsync(CancellationToken ct) => inner.AcceptAsync(ct);

        public ValueTask<CarrierReceive> ReceiveAsync(
            TransportConnectionId c,
            Memory<byte> buffer,
            CancellationToken ct)
            => inner.ReceiveAsync(c, buffer, ct);

        public bool TrySend(TransportConnectionId c, ReadOnlyMemory<byte> bytes)
            => inner.TrySend(c, bytes);

        public bool Close(TransportConnectionId c, ConnectionCloseReason reason)
            => throw new InvalidOperationException("close probe");
    }
}

internal static class RepoPaths
{
    internal static string MvpHostRoot { get; } = Locate();

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

        throw new InvalidOperationException("向上找不到 mvp-host 根");
    }
}
