using System;

namespace Lumio.Server.MvpHost.Wire
{
    /// <summary>信封 <c>integrity</c> 的只读视图。取值域由镜像 schema 判定，本类型不校验。</summary>
    public readonly record struct IntegrityView(string Algorithm, string Value);

    /// <summary>信封 <c>transportPolicy</c> 的只读视图（顶层 12 项 required 之一，含 5 个子字段）。</summary>
    public readonly record struct TransportPolicyView(
        int MaxMessageBytes,
        int MaxFragmentBytes,
        int AntiReplayWindow,
        string AuthBinding,
        string ErrorClass);

    /// <summary>
    /// 解析结果的三态。<c>StructuralReject</c> 是镜像 schema 判死的，
    /// <c>SemanticReject</c> 是 schema 表达不了、须由语义层判死的。
    /// 两者分开是为了让 <c>InvalidFixturesRejectedByNamedLayerTest</c> 能逐条断言拦截层次——
    /// 合成一个 Reject，实现退化成「只做 schema 校验」时测试照样绿。
    /// </summary>
    public enum EnvelopeParseStatus
    {
        Ok,
        StructuralReject,
        SemanticReject,
    }

    /// <summary>
    /// 解析结果。<c>StableErrorId</c> 必须取自
    /// <c>Lumio.Gen.ContractTypes.Catalog.StableErrorIds</c>，本工程不发明任何新错误码。
    /// </summary>
    public readonly record struct EnvelopeParseResult(EnvelopeParseStatus Status, string? StableErrorId, string? Detail);

    /// <summary>
    /// 信封头的只读视图。<c>WireByteLength</c> 是**实际收到的字节数**，与信封里的
    /// <c>length</c> 字段是两回事：ADR-045 §3 明确 <c>length</c> **不表示任何字节数**，
    /// 只是一个不得超过 <c>transportPolicy.maxMessageBytes</c> 的声明上界。
    /// 两者永不交叉核对。
    /// </summary>
    public readonly record struct EnvelopeHeaderView(
        int ProtocolVersion,
        ulong Sequence,
        string SessionId,
        string ProductId,
        string GameReleaseId,
        string MessageType,
        string Reliability,
        string TraceId,
        int WireByteLength);

    /// <summary>
    /// 出站信封的公共上下文。<c>transportPolicy</c> 的 5 个子字段取值必须有来源，
    /// 不得由实现者临场发明，因此一律经本结构传入。
    /// </summary>
    public readonly record struct EnvelopeWriteContext(
        string SessionId,
        string ProductId,
        string GameReleaseId,
        ulong Sequence,
        string TraceId,
        string Reliability,
        int MaxMessageBytes,
        int MaxFragmentBytes,
        int AntiReplayWindow,
        string AuthBinding,
        string ErrorClass);
}
