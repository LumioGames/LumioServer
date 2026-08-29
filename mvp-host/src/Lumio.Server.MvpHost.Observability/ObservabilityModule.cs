using System;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.Observability;

/// <summary>
/// 组装根显式构造用的静态工厂（不引入任何 DI 容器）。
///
/// <c>IWallClock</c> **只在这里被消费**：两个 writer 内部用它填 <c>Timestamp</c>，
/// 调用方签名因此不变。「IWallClock 只被 Observability 引用」由 Architecture.Tests
/// 的依赖断言守住——墙钟一旦散播到别处，超时与窗口判定迟早会误用它。
/// </summary>
public static class ObservabilityModule
{
    /// <summary>
    /// <para>
    /// <b>本签名比设计 §6.6 多一个 <paramref name="identity"/> 参数，这是交付时发现的必要扩展。</b>
    /// §6.6 的原签名是 <c>Create(auditInbox, diagnosticInbox, wallClock, trace)</c>，
    /// 但 <c>IDiagnosticWriter.Write(category, severity, message)</c> 的签名里没有任何身份信息，
    /// 而 <c>common.schema.json#/$defs/correlation</c> 的 <c>productId</c> / <c>gameReleaseId</c>
    /// 是**恒必填**（与 scope 无关）。两者相减，Diagnostic 记录的这两个字段就没有合法来源——
    /// 实现只剩两条路：由组装根注入，或由实现者临场编一个常量。
    /// </para>
    /// <para>
    /// 后者会让「本仓不发明任何公共字段取值」这条纪律在一个不起眼的地方破掉，
    /// 且编出来的值会随 diagnostic 流到下游被当作真实身份读走。因此取前者，
    /// 并把这处签名差异作为设计缺口上报。
    /// </para>
    /// </summary>
    public static ObservabilityServices Create(
        IBoundedInbox<AuditRecord> auditInbox,
        IBoundedInbox<DiagnosticRecord> diagnosticInbox,
        IWallClock wallClock,
        IHostTraceSink trace,
        in HostIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(auditInbox);
        ArgumentNullException.ThrowIfNull(diagnosticInbox);
        ArgumentNullException.ThrowIfNull(wallClock);
        ArgumentNullException.ThrowIfNull(trace);

        if (string.IsNullOrEmpty(identity.ProductId) || string.IsNullOrEmpty(identity.GameReleaseId))
        {
            throw new ArgumentException("HostIdentity 的 productId 与 gameReleaseId 必须非空", nameof(identity));
        }

        var queue = new MvpAuditQueue(auditInbox);
        var audit = new AuditWriter(queue, wallClock, trace);
        var diagnostics = new DiagnosticWriter(diagnosticInbox, wallClock, identity);

        return new ObservabilityServices(audit, diagnostics, trace, queue);
    }
}

/// <summary>
/// 宿主自身的 Release 身份。由组装根从配置读入后注入——
/// 本工程不为这两个字段提供任何默认值，缺它们时宁可构造失败也不编一个。
/// </summary>
public readonly record struct HostIdentity(string ProductId, string GameReleaseId, string ProducerId);

public sealed class ObservabilityServices
{
    private readonly MvpAuditQueue auditQueue;

    internal ObservabilityServices(
        IAuditWriter audit,
        IDiagnosticWriter diagnostics,
        IHostTraceSink trace,
        MvpAuditQueue auditQueue)
    {
        this.Audit = audit;
        this.Diagnostics = diagnostics;
        this.Trace = trace;
        this.auditQueue = auditQueue;
    }

    public IAuditWriter Audit { get; }

    public IDiagnosticWriter Diagnostics { get; }

    public IHostTraceSink Trace { get; }

    /// <summary>
    /// 达阈即请求 world-slot 关闸。这是「Audit 队列背压时认证结果不得静默放行」
    /// 这条安全红线的出口——编排层必须读它。
    /// </summary>
    public bool IsAuditBackpressured => this.auditQueue.IsBackpressured;
}

/// <summary>
/// Audit 队列的背压包装。队列满时**不得让调用方静默通过**：
/// 写入失败会如实回 <c>EnqueueStatus.Full</c>，并把 <see cref="IsBackpressured"/> 置真。
///
/// 阈值刻意取「队列已满」而不是某个百分比：百分比需要一个本仓自定的常数，
/// 而「满」是队列自己就能回答的事实。
/// </summary>
// CA1711：类型名不应以 Queue 结尾。此处定点抑制而非改名——MvpAuditQueue 是卡面与
// 设计 §6.2 队列表逐字定死的登记名，queues.json 与下游卡的验收都按这个名字引用它；
// 改名会让「队列登记行 ↔ 类型」的对应关系断掉。抑制只覆盖这一个类型。
#pragma warning disable CA1711
public sealed class MvpAuditQueue
{
    private readonly IBoundedInbox<AuditRecord> inbox;
    private bool backpressured;

    internal MvpAuditQueue(IBoundedInbox<AuditRecord> inbox) => this.inbox = inbox;

    public bool IsBackpressured => this.backpressured;

    /// <summary>
    /// 背压时产出的类型化事件：请求 world-slot 关闸。
    /// 用类型化事件而不是回调，是为了让「谁在什么条件下要求关闸」在类型上可穷举。
    /// </summary>
    public AuditBackpressureSignal? PendingSignal { get; private set; }

    internal EnqueueResult Enqueue(in AuditRecord record)
    {
        var result = this.inbox.TryEnqueue(in record);

        if (result.Status == EnqueueStatus.Full)
        {
            this.backpressured = true;
            this.PendingSignal = new AuditBackpressureSignal(this.inbox.Count, this.inbox.Budget.MaxItems);
        }
        else if (result.Status == EnqueueStatus.Accepted)
        {
            this.backpressured = false;
            this.PendingSignal = null;
        }

        return result;
    }
}

#pragma warning restore CA1711

/// <summary>请求 world-slot 关闭 Admission Gate 的类型化信号。</summary>
public readonly record struct AuditBackpressureSignal(int PendingItems, int Capacity);

internal sealed class AuditWriter : IAuditWriter
{
    private readonly MvpAuditQueue queue;
    private readonly IWallClock wallClock;
    private readonly IHostTraceSink trace;

    internal AuditWriter(MvpAuditQueue queue, IWallClock wallClock, IHostTraceSink trace)
    {
        this.queue = queue;
        this.wallClock = wallClock;
        this.trace = trace;
    }

    public EnqueueResult WriteReleaseScopedReject(
        string releasePoolId,
        string productId,
        string gameReleaseId,
        string traceId,
        string producerId,
        ulong eventSeq,
        string? reasonCode)
    {
        var correlation = new CorrelationView(
            Scope: "Release",
            ProductId: productId,
            GameReleaseId: gameReleaseId,
            ReleasePoolId: releasePoolId,
            SessionId: null,
            WorldId: null,
            TickId: null,
            TxnId: null,
            TraceId: traceId,
            ProducerId: producerId,
            EventSeq: eventSeq);

        return this.Write(new AuditRecord(
            EventId: EventIdOf(producerId, eventSeq),
            Timestamp: this.wallClock.UtcIso8601Now(),
            Correlation: correlation,
            Category: "Audit",
            Severity: "Warn",
            Durability: "Durable",
            Redaction: "Applied",
            Message: "Handshake rejected before session creation",
            ReasonCode: reasonCode));
    }

    public EnqueueResult WriteSessionScoped(
        ServerSessionId sessionId,
        string productId,
        string gameReleaseId,
        string traceId,
        string producerId,
        ulong eventSeq,
        string message)
    {
        var correlation = new CorrelationView(
            Scope: "Session",
            ProductId: productId,
            GameReleaseId: gameReleaseId,
            ReleasePoolId: null,
            SessionId: sessionId.Value,
            WorldId: null,
            TickId: null,
            TxnId: null,
            TraceId: traceId,
            ProducerId: producerId,
            EventSeq: eventSeq);

        return this.Write(new AuditRecord(
            EventId: EventIdOf(producerId, eventSeq),
            Timestamp: this.wallClock.UtcIso8601Now(),
            Correlation: correlation,
            Category: "Audit",
            Severity: "Info",
            Durability: "Durable",
            Redaction: "Applied",
            Message: message,
            ReasonCode: null));
    }

    /// <summary>
    /// ADR-011 的两张表在入队**之前**强制：产出一条违规的 audit 比丢掉它更糟——
    /// 前者会被下游当成真实关联读走。
    /// </summary>
    private EnqueueResult Write(in AuditRecord record)
    {
        var violation = CorrelationScopeRules.Validate(record.Correlation);
        if (violation is not null)
        {
            throw new InvalidOperationException($"ADR-011 关联作用域违规：{violation}");
        }

        var result = this.queue.Enqueue(in record);

        // 成功写入后自动镜像给 trace，因此 Auth 侧无需显式调用。
        if (result.Status == EnqueueStatus.Accepted)
        {
            this.trace.Audit(in record);
        }

        return result;
    }

    /// <summary>匹配 <c>common.schema.json#/$defs/id</c> 的 <c>^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$</c>。</summary>
    internal static string EventIdOf(string producerId, ulong eventSeq) => $"event-{producerId}-{eventSeq}";
}

internal sealed class DiagnosticWriter : IDiagnosticWriter
{
    private readonly IBoundedInbox<DiagnosticRecord> inbox;
    private readonly IWallClock wallClock;
    private readonly HostIdentity identity;
    private ulong eventSeq;

    internal DiagnosticWriter(IBoundedInbox<DiagnosticRecord> inbox, IWallClock wallClock, in HostIdentity identity)
    {
        this.inbox = inbox;
        this.wallClock = wallClock;
        this.identity = identity;
    }

    public EnqueueResult Write(string category, string severity, string message)
    {
        var seq = this.eventSeq++;

        var record = new DiagnosticRecord(
            EventId: AuditWriter.EventIdOf(this.identity.ProducerId, seq),
            Timestamp: this.wallClock.UtcIso8601Now(),
            Correlation: new CorrelationView(
                Scope: "Process",
                ProductId: this.identity.ProductId,
                GameReleaseId: this.identity.GameReleaseId,
                ReleasePoolId: null,
                SessionId: null,
                WorldId: null,
                TickId: null,
                TxnId: null,
                TraceId: $"trace-diagnostic-{seq}",
                ProducerId: this.identity.ProducerId,
                EventSeq: seq),
            Category: category,
            Severity: severity,
            Message: message);

        var violation = CorrelationScopeRules.Validate(record.Correlation);
        if (violation is not null)
        {
            throw new InvalidOperationException($"ADR-011 关联作用域违规：{violation}");
        }

        return this.inbox.TryEnqueue(in record);
    }
}
