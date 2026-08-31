using System;
using System.Text;
using System.Text.Json.Nodes;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.TestKit;
using Lumio.Server.MvpHost.Wire;

namespace Lumio.Server.MvpHost.Transport.Tests;

/// <summary>
/// 测试装置。**全部走内存 carrier，不依赖任何网络**——
/// 本卡交付的是与字节载体无关的那一半，WSS 由下游卡实现。
/// </summary>
internal sealed class TransportHarness : IDisposable
{
    internal TransportHarness(
        ITransportFaultPolicy? faultPolicy = null,
        int maxMessageBytes = 65536,
        int maxConnections = 8,
        Func<InMemoryByteCarrier, IByteCarrier>? carrierDecorator = null)
    {
        this.Carrier = new InMemoryByteCarrier();
        this.Clock = new FakeMonotonicClock();
        this.Timers = new RecordingTimerService(this.Clock);
        this.FaultPolicy = faultPolicy ?? new PassThroughFaultPolicy();

        var auditInbox = PlatformModule.CreateInbox<AuditRecord>(new QueueBudget(64, 65536));
        var diagnosticInbox = PlatformModule.CreateInbox<DiagnosticRecord>(new QueueBudget(64, 65536));
        this.DiagnosticInbox = diagnosticInbox;
        this.Observability = ObservabilityModule.Create(
            auditInbox,
            diagnosticInbox,
            new FakeWallClock("2026-08-27T00:10:00Z"),
            new RecordingHostTraceSink(),
            new HostIdentity("A", "A-1.1.0", "server-transport"));

        this.Options = new TransportEndpointOptions(
            UriPrefix: "ws://127.0.0.1:0/",
            RequireTls: false,
            MaxMessageBytes: maxMessageBytes,
            MaxConnections: maxConnections,
            ProductId: "A",
            GameReleaseId: "A-1.1.0");

        this.Service = TransportService.Create(
            carrierDecorator?.Invoke(this.Carrier) ?? this.Carrier,
            this.FaultPolicy,
            this.Clock,
            this.Timers,
            this.Observability,
            this.Options);
    }

    internal InMemoryByteCarrier Carrier { get; }

    internal FakeMonotonicClock Clock { get; }

    internal RecordingTimerService Timers { get; }

    internal ITransportFaultPolicy FaultPolicy { get; }

    internal ObservabilityServices Observability { get; }

    internal IBoundedInbox<DiagnosticRecord> DiagnosticInbox { get; }

    internal TransportEndpointOptions Options { get; }

    internal TransportService Service { get; }

    /// <summary>
    /// 造一条能过结构层校验的合法信封。走 <c>Wire</c> 的 writer，不手写 JSON——
    /// 手写的话本测试会变成「测我写的 JSON 对不对」而不是「测 transport 对不对」。
    /// </summary>
    internal static ReadOnlyMemory<byte> ValidEnvelope(
        ulong sequence = 1,
        string reliability = "Reliable",
        int maxMessageBytes = 65536)
    {
        var ctx = new EnvelopeWriteContext(
            SessionId: "session-001",
            ProductId: "A",
            GameReleaseId: "A-1.1.0",
            Sequence: sequence,
            TraceId: $"trace-{sequence}",
            Reliability: reliability,
            MaxMessageBytes: maxMessageBytes,
            MaxFragmentBytes: 4096,
            AntiReplayWindow: 1024,
            AuthBinding: "SessionAdmission",
            ErrorClass: "Rejectable");

        return MvpEnvelopeWriter.WriteServerHandshake(ctx);
    }

    /// <summary>把一条合法信封的 <c>length</c> 改成超过 maxMessageBytes 的值。</summary>
    internal static ReadOnlyMemory<byte> OversizeDeclaredEnvelope(int declaredLength)
    {
        var node = JsonNode.Parse(ValidEnvelope().ToArray())!;
        node["length"] = declaredLength;
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    internal static ReadOnlyMemory<byte> MalformedEnvelope() => Encoding.UTF8.GetBytes("{ not json");

    public void Dispose()
    {
        this.Service.Dispose();
        this.Timers.Dispose();
    }
}

/// <summary>
/// 记录被排定的定时器，供「空闲超时经 ITimerService 而不是自建轮询」的断言使用。
/// 真的排进 <c>MvpTimerService</c> 也行，但那样测试就要等真实时间。
/// </summary>
internal sealed class RecordingTimerService : ITimerService
{
    private readonly ITimerService inner;

    internal RecordingTimerService(IMonotonicClock clock) => this.inner = PlatformModule.CreateTimerService(clock);

    internal System.Collections.Generic.List<(MonotonicInstant DueAt, object Command)> Scheduled { get; } = new();

    internal System.Collections.Generic.List<TimerId> Canceled { get; } = new();

    public TimerId Schedule<TCommand>(MonotonicInstant dueAt, IBoundedInbox<TCommand> target, in TCommand command)
    {
        this.Scheduled.Add((dueAt, command!));
        return this.inner.Schedule(dueAt, target, in command);
    }

    public bool Cancel(TimerId id)
    {
        this.Canceled.Add(id);
        return this.inner.Cancel(id);
    }

    public void Dispose() => this.inner.Dispose();
}

/// <summary>
/// 记账型 carrier：记录**每次分配的缓冲大小**，用于证明超限消息在被完整物化之前就已拒绝。
/// 「分配前拒绝」如果只断言「最后拒绝了」，实现完全可以先分配一个声明长度的缓冲再拒绝——
/// 那正是对端能用一个数字打爆内存的路径。
/// </summary>
internal sealed class AccountingByteCarrier : IByteCarrier
{
    private readonly InMemoryByteCarrier inner = new();

    internal System.Collections.Generic.List<int> BufferSizes { get; } = new();

    internal InMemoryByteCarrier Inner => this.inner;

    public System.Threading.Tasks.ValueTask<CarrierAccept> AcceptAsync(System.Threading.CancellationToken ct)
        => this.inner.AcceptAsync(ct);

    public System.Threading.Tasks.ValueTask<CarrierReceive> ReceiveAsync(
        TransportConnectionId c, Memory<byte> buffer, System.Threading.CancellationToken ct)
    {
        this.BufferSizes.Add(buffer.Length);
        return this.inner.ReceiveAsync(c, buffer, ct);
    }

    public bool TrySend(TransportConnectionId c, ReadOnlyMemory<byte> bytes) => this.inner.TrySend(c, bytes);

    public bool Close(TransportConnectionId c, ConnectionCloseReason reason) => this.inner.Close(c, reason);
}
