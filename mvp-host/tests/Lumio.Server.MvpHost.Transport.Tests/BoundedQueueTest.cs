using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Platform;
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
    public void 非终态事件在事件队列满时关闭连接并写诊断()
    {
        using var harness = new TransportHarness();
        var id = ConnectionLifecycleTest.AcceptOne(harness);

        harness.Service.FillEventOutboxForTest();
        harness.Service.RaiseBackpressuredForTest(id);

        Assert.Equal(TransportConnectionState.Closed, harness.Service.StateOf(id));
        Assert.True(harness.DiagnosticInbox.TryDequeue(out _), "非终态事件被丢弃时必须留下一条 diagnostic");
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
