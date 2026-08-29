using System;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.HostContracts;

/// <summary>
/// 送给 transport 的类型化命令。**闭集**——密封继承层次让「谁能命令 transport 做什么」
/// 在类型上穷举，没有通用的 <c>Send(object)</c> 后门。
/// </summary>
public abstract record ConnectionCommand
{
    private ConnectionCommand()
    {
    }

    public sealed record Bind(
        TransportConnectionId Id,
        ConnectionEpoch Epoch,
        PermissionGrantRef Grant,
        ServerSessionId Session) : ConnectionCommand;

    public sealed record Unbind(TransportConnectionId Id, ConnectionEpoch Epoch) : ConnectionCommand;

    public sealed record Close(
        TransportConnectionId Id,
        ConnectionEpoch Epoch,
        ConnectionCloseReason Reason) : ConnectionCommand;

    public sealed record SetDrain(TransportConnectionId Id, ConnectionEpoch Epoch, bool Draining) : ConnectionCommand;

    public sealed record EnqueueControlEnvelope(
        TransportConnectionId Id,
        ConnectionEpoch Epoch,
        OutboundEnvelopeBytes Envelope) : ConnectionCommand;
}

/// <summary>
/// transport 发出的类型化事件。<c>Closed</c> 与 <c>Faulted</c> 是**终态**，
/// 走保留槽、永不丢弃（见 <c>MvpTransportEventOutbox</c> 的七项合同）。
/// </summary>
public abstract record ConnectionEvent
{
    private ConnectionEvent()
    {
    }

    public sealed record Accepted(TransportConnectionId Id, ConnectionEpoch Epoch) : ConnectionEvent;

    /// <summary>首帧过结构校验后送达 session，作为 admission saga 的 <c>ConnectionCandidate</c> 来源。</summary>
    public sealed record HandshakeEnvelope(
        TransportConnectionId Id,
        ConnectionEpoch Epoch,
        ValidatedEnvelopeBytes Envelope) : ConnectionEvent;

    public sealed record IngressReady(TransportConnectionId Id, ConnectionEpoch Epoch, int PendingCount) : ConnectionEvent;

    public sealed record Backpressured(TransportConnectionId Id, ConnectionEpoch Epoch, bool Reliable) : ConnectionEvent;

    public sealed record Closed(
        TransportConnectionId Id,
        ConnectionEpoch Epoch,
        ConnectionCloseReason Reason) : ConnectionEvent;

    public sealed record Faulted(
        TransportConnectionId Id,
        ConnectionEpoch Epoch,
        string StableErrorId) : ConnectionEvent;
}

public interface ITransportService
{
    BindEndpointResult BindEndpoint(in TransportEndpointOptions options);
}

public interface ITransportControlPort
{
    EnqueueResult TrySend(in ConnectionCommand command);
}

public interface ITransportEventPort
{
    bool TryReceive(out ConnectionEvent evt);
}

/// <summary>
/// 有界 ingress 排空。每 tick 的上限由调用方传入（world-slot 的
/// <c>SlotBudget.MaxIngressItemsPerTick</c> / <c>MaxIngressBytesPerTick</c>），
/// 不由本接口自定——排空节奏归 Owner Thread。
/// </summary>
public interface IIngressReader
{
    int Drain(TransportConnectionId c, int maxItems, long maxBytes, Span<ValidatedEnvelopeBytes> destination);
}

public interface IEgressWriter
{
    EnqueueResult TryEnqueue(TransportConnectionId c, ConnectionEpoch e, in OutboundEnvelopeBytes envelope);
}

/// <summary>
/// 载体抽象。**WSS 与内存环回只替换本接口**——其余一切（Envelope 校验、Auth、
/// Permission、Size、Queue、Tick Barrier）在两种载体下走同一条路径。
/// </summary>
public interface IByteCarrier
{
    ValueTask<CarrierAccept> AcceptAsync(CancellationToken ct);

    ValueTask<CarrierReceive> ReceiveAsync(TransportConnectionId c, Memory<byte> buffer, CancellationToken ct);

    bool TrySend(TransportConnectionId c, ReadOnlyMemory<byte> bytes);

    bool Close(TransportConnectionId c, ConnectionCloseReason reason);
}

/// <summary>
/// 故障注入策略。在组装期注入，生产 Profile 固定 pass-through；
/// 注入实现只存在于 <c>TestKit</c>（硬编码 pass-through 是 LumioClient 侧的已知缺陷，本仓不复制）。
/// </summary>
public interface ITransportFaultPolicy
{
    TransportFaultAction Decide(in TransportFaultContext ctx);
}
