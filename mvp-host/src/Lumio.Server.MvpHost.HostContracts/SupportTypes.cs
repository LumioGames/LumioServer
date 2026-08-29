using System;
using System.Collections.Immutable;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.HostContracts;

/// <summary>
/// 通用 ack。<c>StableErrorId</c> 的取值必须存在于
/// <c>Lumio.Gen.ContractTypes.Catalog.StableErrorIds</c>，<c>null</c> 表示无错误。
/// 本仓不发明任何新错误码——模块内部的 <c>AuthBusy</c> / <c>AggregateBusy</c>
/// 需要对外表达时一律映射已注册的 <c>QueueFull</c>。
/// </summary>
public readonly record struct AckResult(bool Accepted, string? StableErrorId);

// ── transport

public readonly record struct BindEndpointResult(bool Bound, string BoundUri, string? StableErrorId);

public readonly record struct CarrierAccept(
    bool Accepted,
    TransportConnectionId ConnectionId,
    ImmutableArray<string> RequestedSubprotocols);

public readonly record struct CarrierReceive(bool Received, int ByteCount, bool EndOfMessage, bool Closed);

public readonly record struct TransportEndpointOptions(
    string UriPrefix,
    bool RequireTls,
    int MaxMessageBytes,
    int MaxConnections,
    string ProductId,
    string GameReleaseId);

public readonly record struct TransportFaultContext(int Seed, ulong Sequence, bool IsIngress, string MessageType);

// ── auth

public readonly record struct SessionScope(
    ServerSessionId SessionId,
    string ProductId,
    string GameReleaseId,
    string Role);

public readonly record struct AuthenticateCommand(
    AuthRequestId RequestId,
    TransportConnectionId ConnectionId,
    ConnectionEpoch ConnectionEpoch,
    OpaqueCredentialInput Credential,
    VerificationContext Context);

public readonly record struct AuthenticateOutcome(
    CredentialVerdict Verdict,
    PrincipalId Principal,
    AntiReplayVerdict AntiReplay,
    string? StableErrorId,
    string? AuditReason);

public readonly record struct VerificationContext(
    string ProductId,
    string GameReleaseId,
    string Nonce,
    MonotonicInstant ReceivedAt);

public readonly record struct CredentialVerification(
    CredentialVerdict Verdict,
    PrincipalId Principal,
    string? AuditReason);

// ── session

public readonly record struct SessionBinding(
    TransportConnectionId ConnectionId,
    ConnectionEpoch ConnectionEpoch,
    PermissionGrantRef Grant,
    WorldSlotId Slot,
    SlotEpoch SlotEpoch);

public readonly record struct AdmissionStep(
    AdmissionEffectKind Effect,
    AdmissionAttemptId Attempt,
    ServerConnectionSessionState NextState,
    string? StableErrorId);

// ── world-slot

public readonly record struct SlotBudget(
    int MaxSessions,
    int MaxIngressItemsPerTick,
    long MaxIngressBytesPerTick);

public readonly record struct AllocateResult(
    bool Allocated,
    WorldSlotId SlotId,
    SlotEpoch Epoch,
    string? StableErrorId);

public readonly record struct QuotaView(int MaxSessions, int BoundSessions);

/// <summary>
/// 两个 bool 是 §6.4 判定顺序的直接编码：
/// <c>SessionLocalProven → (false, true)</c>；<c>SlotStateUnproven → (true, false)</c>；
/// <c>ProcessFault → (true, false)</c> 且由 App 转交进程；<c>None → (false, false)</c>；
/// **<c>null</c>（无见证）→ <c>(SlotStateUnproven, true, false)</c>**（ADR-006 的从严默认）。
///
/// <see cref="FaultClass"/> 本身**非空**——<c>Classify</c> 的职责就是把「无见证」
/// 折叠成一个确定的分类。
/// </summary>
public readonly record struct FaultAdjudication(
    HostFaultClass FaultClass,
    bool SlotMustFailStop,
    bool SessionMustIsolate);

// ── 宿主 ↔ Runtime 端口

public readonly record struct HostSessionInit(
    HostSessionId Session,
    HostWorldSlotId Slot,
    ReadOnlyMemory<byte> OpaqueConfig,
    ulong DeterministicSeed);

public readonly record struct HostLifecycleResult(
    bool Accepted,
    HostSimulationState State,
    string? StableErrorId);

public readonly record struct HostTickRequest(
    LogicalTickToken Tick,
    ReadOnlyMemory<WireFrame> Ingress,
    ulong DeterministicSeed);

/// <summary>
/// <see cref="FaultClass"/> **必须可空**：<c>null</c> = 「无见证」，
/// <c>HostFaultClass.None</c> = 「有正向见证且无故障」，两者在类型上必须可区分。
/// 非空枚举的 <c>default</c> 是 <c>0 == None</c>，会让「忘了填见证」静默变成
/// 「证明无故障」，绕过 ADR-006 的从严红线。
/// <c>IFaultAdjudicator.Classify(HostFaultClass?)</c> 的入参因此有真实的 <c>null</c> 生产者。
/// </summary>
public readonly record struct HostTickOutcome(
    HostTickStatus Status,
    LogicalTickToken Tick,
    ReadOnlyMemory<byte> StateHash,
    ulong AuthorityRevision,
    ReadOnlyMemory<WireFrame> Egress,
    HostFaultClass? FaultClass,
    string? StableErrorId);

/// <summary>
/// 不可变授权对象；派生后不可修改，撤销走连接 epoch 递增。
/// 重连**必须重新派生**——不保留任何认证状态，也不实现任何 Session Resume Token 快捷路径。
/// </summary>
public sealed record PermissionGrant(
    PrincipalId Principal,
    string Role,
    ImmutableArray<string> Claims,
    ImmutableArray<string> AllowedMessageTypes,
    GrantEpoch Epoch,
    MonotonicInstant ExpiresAt);

/// <summary>
/// 凭据入参的持有者。**禁 ToString / Equals / 序列化**——凭据、token、nonce
/// 不得进日志、fixture、任务卡或 prompt。
///
/// 承载方式（<c>Sec-WebSocket-Protocol</c> 子协议位序）是公共面缺位期的私有约定，
/// 登记在 <c>absences.json</c> 的 <c>ABS-AUTH-CREDENTIAL-CARRIAGE</c>：
/// 架构源冻结通道认证的凭据承载方式后，本仓即改用公共形态并删除该约定。
/// </summary>
public sealed class OpaqueCredentialInput : IDisposable
{
    private byte[]? bytes;

    public OpaqueCredentialInput(byte[] credentialBytes)
    {
        ArgumentNullException.ThrowIfNull(credentialBytes);
        this.bytes = credentialBytes;
    }

    /// <summary>只供 verifier 读取。释放后再取即抛——避免释放后仍被比对。</summary>
    public ReadOnlySpan<byte> Span
        => this.bytes ?? throw new ObjectDisposedException(nameof(OpaqueCredentialInput));

    public void Dispose()
    {
        if (this.bytes is not null)
        {
            Array.Clear(this.bytes);
            this.bytes = null;
        }
    }

    /// <summary>刻意抛出：凭据的字符串形式一旦存在，就迟早会进日志。</summary>
    public override string ToString()
        => throw new NotSupportedException("凭据不得转成字符串——它会进日志。");

    /// <summary>刻意抛出：相等性比较必须走 verifier 的常量时间路径，不走对象相等。</summary>
    public override bool Equals(object? obj)
        => throw new NotSupportedException("凭据不得用对象相等比较——必须走常量时间 exact-byte 比对。");

    public override int GetHashCode()
        => throw new NotSupportedException("凭据不得参与哈希——哈希值会泄漏进字典诊断与日志。");
}
