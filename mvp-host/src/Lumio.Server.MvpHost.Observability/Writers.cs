using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.Observability;

/// <summary>
/// Audit 写入面。调用方签名里**没有** <c>eventId</c> 与 <c>timestamp</c>——
/// 两者由实现内部填充，因此调用方无从伪造时间戳，也无从造出重复的 eventId。
/// </summary>
public interface IAuditWriter
{
    /// <summary>
    /// Release 作用域的拒绝事件。**不带 sessionId**（ADR-011）：
    /// 认证失败发生在 session 创建之前，此时任何 sessionId 都是编造的。
    ///
    /// <para>
    /// <paramref name="reasonCode"/> **可空**，且 <c>null</c> 与「随便填一个」有本质区别：
    /// 已注册 ErrorCode 集合里不存在「凭据无效」语义码（<c>absences.json</c> 的
    /// <c>ABS-AUTH-CREDENTIAL-ERRORCODE</c>），而 <c>logging-event.schema.json</c> 的
    /// <c>required</c> 不含 <c>fields</c>——因此「不写 errorCode」产出的仍是合法事件，
    /// 而填一个语义不对的已注册码会把缺席伪装成有依据，并让下游把它当真读走。
    /// 本参数原为非空，交付 auth 存根时发现该形状无法表达最要紧的那条审计事件，故放宽。
    /// **有已注册码的路径仍必须带上它**——这条由消费方的定向断言守住，放宽不等于可省略。
    /// </para>
    /// </summary>
    EnqueueResult WriteReleaseScopedReject(
        string releasePoolId,
        string productId,
        string gameReleaseId,
        string traceId,
        string producerId,
        ulong eventSeq,
        string? reasonCode);

    EnqueueResult WriteSessionScoped(
        ServerSessionId sessionId,
        string productId,
        string gameReleaseId,
        string traceId,
        string producerId,
        ulong eventSeq,
        string message);
}

public interface IDiagnosticWriter
{
    EnqueueResult Write(string category, string severity, string message);
}

/// <summary>
/// 服务端只写观测面（A1-α 的判定面之一）。**只写、无任何查询方法**，
/// 因此不构成可被误用的状态查询面——谁都不能把它当作「读服务端当前状态」的后门。
///
/// 生产 Profile 注入 <see cref="NullHostTraceSink"/>。
/// </summary>
public interface IHostTraceSink
{
    void Audit(in AuditRecord record);

    void Ack(string effect, ulong? admissionAttemptId, ulong? slotEpoch, ulong? connectionEpoch);

    void State(string? sessionId, string? sessionState, ulong? authorityRevision, ulong? slotEpoch, ulong? grantEpoch);
}

/// <summary>生产 Profile 的 trace sink：三个方法全部空实现。</summary>
public sealed class NullHostTraceSink : IHostTraceSink
{
    public void Audit(in AuditRecord record)
    {
    }

    public void Ack(string effect, ulong? admissionAttemptId, ulong? slotEpoch, ulong? connectionEpoch)
    {
    }

    public void State(string? sessionId, string? sessionState, ulong? authorityRevision, ulong? slotEpoch, ulong? grantEpoch)
    {
    }
}
