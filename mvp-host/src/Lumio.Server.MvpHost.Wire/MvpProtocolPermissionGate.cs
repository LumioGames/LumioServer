using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Lumio.Server.MvpHost.Wire
{
    /// <summary>ADR-022 gate 的一次判定输入。</summary>
    public readonly record struct MvpPermissionGateRequest(
        string SessionId,
        string ProductId,
        string GameReleaseId,
        string MessageId,
        string Role,
        ImmutableArray<string> Claims,
        ulong ConnectionGeneration,
        string AdmittedSessionId,
        string AdmittedProductId,
        string AdmittedGameReleaseId,
        string AdmittedRole,
        ImmutableArray<string> AdmittedClaims,
        ulong AdmittedConnectionGeneration);

    /// <summary>gate 判定结果。<c>RejectReason</c> 为 null 即 Accept。</summary>
    public readonly record struct MvpPermissionGateVerdict(bool Accepted, string? RejectReason);

    /// <summary>
    /// ADR-022 Protocol/Permission gate 的**薄适配层**。
    ///
    /// **判定本体不在这里** —— 它在架构源生成的
    /// <c>Lumio.Gen.ProtocolPermissionValidator.ProtocolGate.Evaluate</c>（ADR-048 已发布，
    /// 带 <c>RejectPrecedence</c> 拒绝优先级表与 <c>RegisteredMessageIds</c>）。
    /// 本类型只做入参搬运，**一条判定逻辑都不复制**：手写第二份判定＝两个实现能各自通过
    /// 自己的门却在拒绝优先级上分歧，正是 ADR-028 否决 free-form 时点名的那种失败。
    ///
    /// 本仓早先的手写 gate 路径已随 ADR-048 作废（<c>absences.json</c> 的
    /// <c>ABS-PERMISSION-VALIDATOR</c> 前提已满足）。
    /// </summary>
    public static class MvpProtocolPermissionGate
    {
        /// <summary>
        /// 直接转发生成物的字段名表，**不复制成本仓的字面量**——
        /// 复制一份就等于给了它一个会独立漂移的机会。
        /// </summary>
        public static IReadOnlyList<string> ActiveFieldNames
            => Lumio.Gen.ProtocolPermissionValidator.ActivePermissionFields.Names;

        /// <summary>
        /// 六项判定**全部委托**给生成物。本方法只做入参搬运与结果翻译，
        /// 一条 <c>if</c> 都不复制——包括拒绝优先级：多条同时失败时先报哪一条是公共规则
        /// （生成物的 <c>RejectPrecedence</c>），本仓自定就会与其他实现分歧。
        /// </summary>
        public static MvpPermissionGateVerdict Evaluate(in MvpPermissionGateRequest request)
        {
            var input = new Lumio.Gen.ProtocolPermissionValidator.GateInput(
                sessionId: request.SessionId,
                productId: request.ProductId,
                gameReleaseId: request.GameReleaseId,
                messageId: request.MessageId,
                role: request.Role,
                claims: request.Claims.IsDefault ? Array.Empty<string>() : request.Claims.ToArray(),
                connectionGeneration: request.ConnectionGeneration,
                admittedSessionId: request.AdmittedSessionId,
                admittedProductId: request.AdmittedProductId,
                admittedGameReleaseId: request.AdmittedGameReleaseId,
                admittedRole: request.AdmittedRole,
                admittedClaims: request.AdmittedClaims.IsDefault ? Array.Empty<string>() : request.AdmittedClaims.ToArray(),
                admittedConnectionGeneration: request.AdmittedConnectionGeneration);

            var verdict = Lumio.Gen.ProtocolPermissionValidator.ProtocolGate.Evaluate(input, out var rejectReason);

            return verdict == Lumio.Gen.ProtocolPermissionValidator.Verdict.Accept
                ? new MvpPermissionGateVerdict(true, null)
                : new MvpPermissionGateVerdict(false, rejectReason);
        }
    }
}
