using System;
using System.Collections.Generic;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.Wire;

namespace Lumio.Server.MvpHost.Transport;

/// <summary>
/// 载体无关的 transport 核心。**连接注册表的唯一写入者**——
/// 别的模块只能经 <see cref="ITransportControlPort.TrySend"/> 发类型化命令影响它，
/// 并等显式 ack。
///
/// 本类型不认识 auth、不认识 session 编排、不认识 slot 生命周期：
/// <c>PermissionGrantRef</c> 只被搬运，从不被读取内部数值。
/// </summary>
public sealed class TransportService
    : ITransportService, ITransportControlPort, ITransportEventPort, IIngressReader, IEgressWriter, IDisposable
{
    /// <summary>per-connection ingress：256 条 / 256 KiB（SRV-D-001，provisional）。</summary>
    private static readonly QueueBudget IngressBudget = new(256, 256 * 1024);

    /// <summary>per-connection egress：512 条 / 1 MiB（SRV-D-002，provisional）。</summary>
    private static readonly QueueBudget EgressBudget = new(512, 1024 * 1024);

    /// <summary>session → 连接命令循环：64 条（SRV-D-015，provisional）。</summary>
    private static readonly QueueBudget CommandBudget = new(64, 64 * 1024);

    /// <summary>
    /// transport → session：256 条。**外加两个保留槽**给终态事件——
    /// <c>Closed</c> / <c>Faulted</c> 永不丢弃。丢一个 <c>Closed</c> 的后果是
    /// session 侧永远留着一条已经不存在的连接。
    /// </summary>
    private static readonly QueueBudget EventBudget = new(256, 256 * 1024);

    private readonly IByteCarrier carrier;
    private readonly ITransportFaultPolicy faultPolicy;
    private readonly IMonotonicClock clock;
    private readonly ITimerService timers;
    private readonly ObservabilityServices observability;
    private readonly TransportEndpointOptions options;
    private readonly ConnectionRegistry registry = new();

    private readonly IBoundedInbox<ConnectionCommand> commandInbox;
    private readonly IBoundedInbox<ConnectionEvent> eventOutbox;

    /// <summary>终态事件的保留槽。队列满时它们仍然必达。</summary>
    private readonly Queue<ConnectionEvent> terminalReserve = new();

    private readonly List<int> receiveBufferSizes = new();

    private TransportService(
        IByteCarrier carrier,
        ITransportFaultPolicy faultPolicy,
        IMonotonicClock clock,
        ITimerService timers,
        ObservabilityServices observability,
        in TransportEndpointOptions options)
    {
        this.carrier = carrier;
        this.faultPolicy = faultPolicy;
        this.clock = clock;
        this.timers = timers;
        this.observability = observability;
        this.options = options;

        this.commandInbox = PlatformModule.CreateInbox<ConnectionCommand>(in CommandBudget);
        this.eventOutbox = PlatformModule.CreateInbox<ConnectionEvent>(in EventBudget);
    }

    /// <summary>
    /// 组装根显式构造入口。<paramref name="faultPolicy"/> **没有默认值**：
    /// 给了默认值，「生产 Profile 用的是哪个策略」就变成要读调用点才知道的事，
    /// 而漏传时它会静默变成 pass-through。生产 Profile 由 App 传
    /// <see cref="PassThroughFaultPolicy"/>。
    /// </summary>
    public static TransportService Create(
        IByteCarrier carrier,
        ITransportFaultPolicy faultPolicy,
        IMonotonicClock clock,
        ITimerService timers,
        ObservabilityServices observability,
        in TransportEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        ArgumentNullException.ThrowIfNull(faultPolicy);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(observability);

        return new TransportService(carrier, faultPolicy, clock, timers, observability, in options);
    }

    public BindEndpointResult BindEndpoint(in TransportEndpointOptions options)
        => new(Bound: true, BoundUri: options.UriPrefix, StableErrorId: null);

    /// <summary>
    /// 应用一条连接命令。**代次不匹配一律拒绝**——迟到的命令携带的是一个已经作废的
    /// 世界观，应用它会让注册表回到那个世界观里。
    /// </summary>
    public EnqueueResult TrySend(in ConnectionCommand command)
    {
        var (id, epoch) = Address(command);

        if (!this.registry.TryGet(id, out var entry))
        {
            return new EnqueueResult(EnqueueStatus.Closed, "StaleConnectionGeneration");
        }

        if (entry.Epoch != epoch)
        {
            return new EnqueueResult(EnqueueStatus.Closed, "StaleConnectionGeneration");
        }

        // Closed 之后只收 Close 并 ack，其余一律拒绝。
        if (entry.State == TransportConnectionState.Closed && command is not ConnectionCommand.Close)
        {
            return new EnqueueResult(EnqueueStatus.Closed, "StaleConnectionGeneration");
        }

        var admitted = this.commandInbox.TryEnqueue(in command);
        if (admitted.Status == EnqueueStatus.Full)
        {
            return new EnqueueResult(EnqueueStatus.Full, "QueueFull");
        }

        this.commandInbox.TryDequeue(out _);
        return this.Apply(entry, command);
    }

    public bool TryReceive(out ConnectionEvent evt)
    {
        if (this.terminalReserve.Count > 0)
        {
            evt = this.terminalReserve.Dequeue();
            return true;
        }

        return this.eventOutbox.TryDequeue(out evt!);
    }

    /// <summary>
    /// 有界排空。上限由调用方（world-slot 的 <c>SlotBudget</c>）传入，
    /// 不由本类自定——排空节奏归 Owner Thread。
    /// </summary>
    public int Drain(TransportConnectionId c, int maxItems, long maxBytes, Span<ValidatedEnvelopeBytes> destination)
    {
        if (!this.registry.TryGet(c, out var entry))
        {
            return 0;
        }

        var taken = 0;
        long bytes = 0;

        while (taken < maxItems && taken < destination.Length)
        {
            if (!entry.Ingress.TryDequeue(out var item))
            {
                break;
            }

            bytes += item.Bytes.Length;
            if (bytes > maxBytes)
            {
                break;
            }

            destination[taken++] = item;
        }

        return taken;
    }

    public EnqueueResult TryEnqueue(TransportConnectionId c, ConnectionEpoch e, in OutboundEnvelopeBytes envelope)
    {
        if (!this.registry.TryGet(c, out var entry) || entry.Epoch != e)
        {
            return new EnqueueResult(EnqueueStatus.Closed, "StaleConnectionGeneration");
        }

        var result = entry.Egress.TryEnqueue(in envelope);
        return result.Status == EnqueueStatus.Full
            ? new EnqueueResult(EnqueueStatus.Full, "QueueFull")
            : result;
    }

    public bool TryAcceptOne()
    {
        var accept = this.carrier.AcceptAsync(System.Threading.CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

        if (!accept.Accepted)
        {
            return false;
        }

        var entry = this.registry.Add(accept.ConnectionId, IngressBudget, EgressBudget);
        entry.NoteActivity(this.clock.Now);
        this.ArmIdleTimer(entry);

        this.Publish(new ConnectionEvent.Accepted(entry.Id, entry.Epoch));
        return true;
    }

    /// <summary>
    /// 驱动一次接收。**分配前拒绝**：无论对端声明多长，一次只向 carrier 要
    /// <see cref="TransportProvisionalLimits.ReceiveBufferBytes"/> 字节，
    /// 累计越限就立刻中止并关连接——绝不先按声明长度分配再看它合不合法。
    /// </summary>
    public bool PumpReceiveOnce(TransportConnectionId connection)
    {
        if (!this.registry.TryGet(connection, out var entry) || entry.State == TransportConnectionState.Closed)
        {
            return false;
        }

        var buffer = new byte[TransportProvisionalLimits.ReceiveBufferBytes];
        this.receiveBufferSizes.Add(buffer.Length);

        var received = this.carrier.ReceiveAsync(connection, buffer, System.Threading.CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

        if (received.Closed)
        {
            this.CloseConnection(entry, ConnectionCloseReason.Disconnect, stableErrorId: null);
            return true;
        }

        if (!received.Received)
        {
            return false;
        }

        entry.NoteActivity(this.clock.Now);

        var total = entry.AddInboundBytes(received.ByteCount);
        if (total > this.options.MaxMessageBytes)
        {
            // 累计字节越限：可拒绝，只断该连接。此时还没有物化整条消息。
            this.CloseConnection(entry, ConnectionCloseReason.Fault, "BudgetExceeded");
            return true;
        }

        if (!received.EndOfMessage)
        {
            return true;
        }

        entry.ResetInboundMessageBytes();

        if (!entry.TryAdmitInbound(this.clock.Now))
        {
            this.CloseConnection(entry, ConnectionCloseReason.PolicyReject, "QueueFull");
            return true;
        }

        var message = buffer.AsMemory(0, received.ByteCount);
        return this.AdmitMessage(entry, message);
    }

    /// <summary>驱动一次发送。故障装饰器在**出队后、交 carrier 前**挂第二次。</summary>
    public int PumpSendOnce(TransportConnectionId connection)
    {
        if (!this.registry.TryGet(connection, out var entry) || entry.State == TransportConnectionState.Closed)
        {
            return 0;
        }

        var sent = 0;

        while (sent < TransportProvisionalLimits.EgressBatchPerTick && entry.Egress.TryDequeue(out var outbound))
        {
            var decision = this.faultPolicy.Decide(new TransportFaultContext(
                Seed: 0, Sequence: (ulong)sent, IsIngress: false, MessageType: "Outbound"));

            switch (decision)
            {
                case TransportFaultAction.Drop:
                    continue;
                case TransportFaultAction.Disconnect:
                    this.CloseConnection(entry, ConnectionCloseReason.Fault, "QueueFull");
                    return sent;
                case TransportFaultAction.Duplicate:
                    this.carrier.TrySend(connection, outbound.Bytes);
                    break;
                default:
                    break;
            }

            if (!this.carrier.TrySend(connection, outbound.Bytes))
            {
                this.CloseConnection(entry, ConnectionCloseReason.Disconnect, stableErrorId: null);
                return sent;
            }

            sent++;
        }

        return sent;
    }

    public TransportConnectionState StateOf(TransportConnectionId connection)
        => this.registry.TryGet(connection, out var entry) ? entry.State : TransportConnectionState.Closed;

    public ConnectionEpoch EpochOf(TransportConnectionId connection)
        => this.registry.TryGet(connection, out var entry) ? entry.Epoch : default;

    public int UnreliableDropCountOf(TransportConnectionId connection)
        => this.registry.TryGet(connection, out var entry) ? entry.UnreliableDropCount : 0;

    // ── 测试可见的观察点。internal 而非 public：它们让测试能看见队列与事件出箱的内部状态，
    //    但不构成对外契约——别的模块只收类型化事件。

    internal bool IngressIsFullForTest(TransportConnectionId connection)
        => this.registry.TryGet(connection, out var entry) && entry.Ingress.Count >= entry.Ingress.Budget.MaxItems;

    internal void FillEventOutboxForTest()
    {
        for (var i = 0; i < EventBudget.MaxItems + 1; i++)
        {
            var filler = new ConnectionEvent.IngressReady(new TransportConnectionId(0), default, i);
            if (this.eventOutbox.TryEnqueue(filler).Status != EnqueueStatus.Accepted)
            {
                return;
            }
        }
    }

    internal void RaiseClosedForTest(TransportConnectionId connection, ConnectionCloseReason reason)
    {
        if (this.registry.TryGet(connection, out var entry))
        {
            this.CloseConnection(entry, reason, stableErrorId: null);
        }
    }

    internal void RaiseBackpressuredForTest(TransportConnectionId connection)
    {
        if (this.registry.TryGet(connection, out var entry))
        {
            this.Publish(new ConnectionEvent.Backpressured(entry.Id, entry.Epoch, Reliable: true));
        }
    }

    /// <summary>每次向 carrier 请求接收时分配的缓冲大小，用于证明从未按声明长度分配。</summary>
    internal IReadOnlyList<int> ReceiveBufferSizesForTest => this.receiveBufferSizes;

    public void Dispose()
    {
        foreach (var id in new List<ulong>(this.registry.ConnectionIds))
        {
            if (this.registry.TryGet(new TransportConnectionId(id), out var entry))
            {
                entry.Ingress.Close();
                entry.Egress.Close();
            }
        }

        this.commandInbox.Close();
        this.eventOutbox.Close();
    }

    /// <summary>
    /// 校验闸 + 故障装饰器第一个挂点（**解码后、ingress 入队前**）。
    /// 位置刻意与 LumioClient 的装饰器对称，使双端故障脚本共用同一口径。
    /// </summary>
    private bool AdmitMessage(ConnectionEntry entry, ReadOnlyMemory<byte> message)
    {
        // 声明长度先判，**在结构层解析与握手分支之前**：`length` 是对端自己声明的上界，
        // 它超过本端点的 MaxMessageBytes 时，这条消息无论内容如何都不该被继续处理。
        // 放在握手分支之后的话，第一帧就能绕过这道判定。
        if (DeclaredLengthOf(message) is { } declared && declared > this.options.MaxMessageBytes)
        {
            this.CloseConnection(entry, ConnectionCloseReason.Fault, "BudgetExceeded");
            return true;
        }

        var parsed = MvpEnvelopeReader.TryReadHeader(message.Span, out var header);
        if (parsed.Status != EnvelopeParseStatus.Ok)
        {
            // 畸形 / 完整性失败：可拒绝，只断该连接，不上升为 Slot 或进程故障。
            this.CloseConnection(entry, ConnectionCloseReason.Fault, parsed.StableErrorId ?? "ManifestMalformed");
            return true;
        }

        if (header.SessionId.Length == 0)
        {
            this.CloseConnection(entry, ConnectionCloseReason.Fault, "SessionMismatch");
            return true;
        }

        if (entry.State == TransportConnectionState.Accepted)
        {
            entry.TryTransitionTo(TransportConnectionState.EnvelopeValidated);
            this.Publish(new ConnectionEvent.HandshakeEnvelope(
                entry.Id, entry.Epoch, new ValidatedEnvelopeBytes(message, header)));
            return true;
        }

        var decision = this.faultPolicy.Decide(new TransportFaultContext(
            Seed: 0, Sequence: header.Sequence, IsIngress: true, MessageType: header.MessageType));

        switch (decision)
        {
            case TransportFaultAction.Drop:
                return true;
            case TransportFaultAction.Disconnect:
                this.CloseConnection(entry, ConnectionCloseReason.Fault, "QueueFull");
                return true;
            default:
                break;
        }

        var validated = new ValidatedEnvelopeBytes(message, header);
        var enqueued = entry.Ingress.TryEnqueue(in validated);

        if (enqueued.Status == EnqueueStatus.Full)
        {
            // Reliable 满载**断开连接**，绝不静默丢：静默丢一条 Reliable，
            // 对端不会知道，它会一直等一个永远不来的状态。
            if (string.Equals(header.Reliability, "Reliable", StringComparison.Ordinal))
            {
                this.CloseConnection(entry, ConnectionCloseReason.Fault, "QueueFull");
                return true;
            }

            entry.CountUnreliableDrop();
            return true;
        }

        if (entry.State is TransportConnectionState.Bound)
        {
            entry.TryTransitionTo(TransportConnectionState.Active);
        }

        this.Publish(new ConnectionEvent.IngressReady(entry.Id, entry.Epoch, entry.Ingress.Count));
        return true;
    }

    private EnqueueResult Apply(ConnectionEntry entry, ConnectionCommand command)
    {
        switch (command)
        {
            case ConnectionCommand.Bind bind:
                if (!entry.TryTransitionTo(TransportConnectionState.Bound)
                    && entry.State != TransportConnectionState.Bound)
                {
                    return new EnqueueResult(EnqueueStatus.Closed, "StaleEpoch");
                }

                entry.ApplyBind(bind.Session, bind.Grant);
                entry.BumpEpoch();
                return new EnqueueResult(EnqueueStatus.Accepted, null);

            case ConnectionCommand.Unbind:
                entry.ApplyUnbind();
                entry.BumpEpoch();
                return new EnqueueResult(EnqueueStatus.Accepted, null);

            case ConnectionCommand.SetDrain setDrain:
                if (setDrain.Draining && !entry.TryTransitionTo(TransportConnectionState.Draining))
                {
                    return new EnqueueResult(EnqueueStatus.Closed, "StaleEpoch");
                }

                return new EnqueueResult(EnqueueStatus.Accepted, null);

            case ConnectionCommand.Close close:
                this.CloseConnection(entry, close.Reason, stableErrorId: null);
                return new EnqueueResult(EnqueueStatus.Accepted, null);

            case ConnectionCommand.EnqueueControlEnvelope control:
                return this.TryEnqueue(entry.Id, entry.Epoch, control.Envelope);

            default:
                return new EnqueueResult(EnqueueStatus.Closed, "MessagePermissionDenied");
        }
    }

    private void CloseConnection(ConnectionEntry entry, ConnectionCloseReason reason, string? stableErrorId)
    {
        if (entry.State == TransportConnectionState.Closed)
        {
            return;
        }

        entry.TryTransitionTo(TransportConnectionState.Closed);
        entry.Ingress.Close();
        entry.Egress.Close();
        this.carrier.Close(entry.Id, reason);

        if (entry.IdleTimer is { } timer)
        {
            this.timers.Cancel(timer);
            entry.SetIdleTimer(null);
        }

        this.Publish(stableErrorId is null
            ? new ConnectionEvent.Closed(entry.Id, entry.Epoch, reason)
            : new ConnectionEvent.Faulted(entry.Id, entry.Epoch, stableErrorId));

        // Faulted 之后仍要让 session 看到连接终止；两者都是终态，都走保留槽。
        if (stableErrorId is not null)
        {
            this.Publish(new ConnectionEvent.Closed(entry.Id, entry.Epoch, reason));
        }
    }

    /// <summary>
    /// 终态事件走保留槽、永不丢弃；非终态满载则关闭该连接并写一条 diagnostic——
    /// 丢一个非终态事件而不留痕，等于让 session 侧的状态悄悄落后于事实。
    /// </summary>
    private void Publish(ConnectionEvent evt)
    {
        var terminal = evt is ConnectionEvent.Closed or ConnectionEvent.Faulted;

        var result = this.eventOutbox.TryEnqueue(in evt);
        if (result.Status == EnqueueStatus.Accepted)
        {
            return;
        }

        if (terminal)
        {
            this.terminalReserve.Enqueue(evt);
            return;
        }

        this.observability.Diagnostics.Write(
            "Diagnostic",
            "Warn",
            $"transport event outbox full; dropping non-terminal {evt.GetType().Name} and closing connection");

        if (this.registry.TryGet(ConnectionIdOf(evt), out var entry))
        {
            entry.TryTransitionTo(TransportConnectionState.Closed);
            entry.Ingress.Close();
            entry.Egress.Close();
            this.terminalReserve.Enqueue(new ConnectionEvent.Closed(
                entry.Id, entry.Epoch, ConnectionCloseReason.Fault));
        }
    }

    private void ArmIdleTimer(ConnectionEntry entry)
    {
        var dueAt = new MonotonicInstant(
            this.clock.Now.Ticks + TimeSpan.FromSeconds(TransportProvisionalLimits.IdleTimeoutSeconds).Ticks);

        var close = new ConnectionCommand.Close(entry.Id, entry.Epoch, ConnectionCloseReason.Disconnect);
        entry.SetIdleTimer(this.timers.Schedule<ConnectionCommand>(dueAt, this.commandInbox, close));
    }

    /// <summary>
    /// 只读出顶层 <c>length</c>，不解析整条信封。用 <c>Utf8JsonReader</c> 前向扫描：
    /// 它不构造任何节点树，因此「读一个字段」不等于「物化整条消息」。
    /// </summary>
    private static long? DeclaredLengthOf(ReadOnlyMemory<byte> message)
    {
        try
        {
            var reader = new System.Text.Json.Utf8JsonReader(message.Span);

            while (reader.Read())
            {
                if (reader.TokenType == System.Text.Json.JsonTokenType.PropertyName
                    && reader.ValueTextEquals("length"u8)
                    && reader.Read()
                    && reader.TokenType == System.Text.Json.JsonTokenType.Number
                    && reader.TryGetInt64(out var value))
                {
                    return value;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // 畸形输入交给下面的结构层判死，这里不代它做判断。
            return null;
        }

        return null;
    }

    private static (TransportConnectionId Id, ConnectionEpoch Epoch) Address(ConnectionCommand command) => command switch
    {
        ConnectionCommand.Bind b => (b.Id, b.Epoch),
        ConnectionCommand.Unbind u => (u.Id, u.Epoch),
        ConnectionCommand.Close c => (c.Id, c.Epoch),
        ConnectionCommand.SetDrain s => (s.Id, s.Epoch),
        ConnectionCommand.EnqueueControlEnvelope e => (e.Id, e.Epoch),
        _ => (default, default),
    };

    private static TransportConnectionId ConnectionIdOf(ConnectionEvent evt) => evt switch
    {
        ConnectionEvent.Accepted a => a.Id,
        ConnectionEvent.HandshakeEnvelope h => h.Id,
        ConnectionEvent.IngressReady i => i.Id,
        ConnectionEvent.Backpressured b => b.Id,
        ConnectionEvent.Closed c => c.Id,
        ConnectionEvent.Faulted f => f.Id,
        _ => default,
    };
}
