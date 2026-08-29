namespace Lumio.Server.MvpHost.Observability;

/// <summary>
/// 关联作用域视图。字段集与取值规则的真值是架构源
/// <c>common.schema.json#/$defs/correlation</c> 与 ADR-011 的 REQUIRED / FORBIDDEN 两张表。
/// </summary>
public readonly record struct CorrelationView(
    string Scope,
    string ProductId,
    string GameReleaseId,
    string? ReleasePoolId,
    string? SessionId,
    string? WorldId,
    ulong? TickId,
    string? TxnId,
    string TraceId,
    string ProducerId,
    ulong EventSeq);

/// <summary>
/// Audit 记录。<see cref="EventId"/> 与 <see cref="Timestamp"/> 是
/// <c>logging-event.schema.json</c> 的 required 成员（实测 required 恰 7 项、
/// <c>additionalProperties: false</c>），缺任一项即产不出合法事件。
///
/// 两者都由 writer **内部**填充，不进调用方签名：
/// <see cref="EventId"/> 按 <c>event-{producerId}-{eventSeq}</c> 生成（匹配 common 的 <c>id</c>），
/// <see cref="Timestamp"/> 的**唯一**来源是 Platform 的 <c>IWallClock</c>（全仓唯一墙钟出口）。
/// </summary>
public readonly record struct AuditRecord(
    string EventId,
    string Timestamp,
    CorrelationView Correlation,
    string Category,
    string Severity,
    string Durability,
    string Redaction,
    string Message,
    string? ReasonCode);

/// <summary>Diagnostic 记录。与 Audit 分属两条独立的有界写入面，互不挤占。</summary>
public readonly record struct DiagnosticRecord(
    string EventId,
    string Timestamp,
    CorrelationView Correlation,
    string Category,
    string Severity,
    string Message);
