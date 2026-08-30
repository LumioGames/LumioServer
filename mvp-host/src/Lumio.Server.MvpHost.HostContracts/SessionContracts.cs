using System;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.HostContracts;

/// <summary>送给 session 编排的类型化命令。</summary>
public abstract record SessionCommand
{
    private SessionCommand()
    {
    }

    /// <summary>transport 首包已过结构校验，作为候选送达。</summary>
    public sealed record ConnectionCandidate(
        TransportConnectionId ConnectionId,
        ConnectionEpoch ConnectionEpoch,
        ValidatedEnvelopeBytes Handshake) : SessionCommand
    {
        /// <summary>
        /// Optional transport-authenticated principal. Legacy in-process
        /// candidates omit it and retain the credential-verifier path.
        /// </summary>
        public TransportAuthenticationEvidence? AuthenticationEvidence { get; init; }
    }

    /// <summary>saga 某一步的依赖方回执。每步都必须有显式 ack，没有隐式成功。</summary>
    public sealed record DependencyResult(
        AdmissionAttemptId Attempt,
        AdmissionEffectKind Effect,
        bool Accepted,
        string? StableErrorId) : SessionCommand;

    public sealed record BeginDrain(MonotonicInstant GraceDeadline) : SessionCommand;

    public sealed record Kick(ServerSessionId SessionId, string RegisteredReasonCode) : SessionCommand;

    /// <summary>重连窗口到期等超时事件。经 <c>ITimerService</c> 投递，**不自建轮询线程**。</summary>
    public sealed record TimerFired(TimerId Timer, ServerSessionId SessionId) : SessionCommand;

    public sealed record SlotFaulted(WorldSlotId Slot, SlotEpoch Epoch, HostFaultClass FaultClass) : SessionCommand;
}

/// <summary>session 发出的类型化事件。关键终态走保留槽，无法交付则隔离该 session 并发 diagnostic。</summary>
public abstract record SessionEvent
{
    private SessionEvent()
    {
    }

    public sealed record Admitted(ServerSessionId SessionId, SessionEpoch Epoch, SessionBinding Binding) : SessionEvent;

    public sealed record Rejected(
        AdmissionAttemptId Attempt,
        TransportConnectionId ConnectionId,
        string StableErrorId) : SessionEvent;

    public sealed record Disconnected(ServerSessionId SessionId, SessionEpoch Epoch) : SessionEvent;

    public sealed record Reconnected(ServerSessionId SessionId, SessionEpoch Epoch, SessionBinding Binding) : SessionEvent;

    public sealed record Drained(ServerSessionId SessionId, SessionEpoch Epoch) : SessionEvent;

    public sealed record Kicked(ServerSessionId SessionId, SessionEpoch Epoch, string RegisteredReasonCode) : SessionEvent;

    public sealed record Faulted(ServerSessionId SessionId, SessionEpoch Epoch, string StableErrorId) : SessionEvent;
}

/// <summary>
/// 纯状态机：只生成下一条类型化 effect，**不做 IO**。
/// 这条约束让 admission saga 的八步可以离线重放与穷举测试。
/// </summary>
public interface IAdmissionReducer
{
    AdmissionStep Advance(in ServerConnectionSessionState state, in SessionCommand input);
}

/// <summary>
/// 测试控制面：**仅回环绑定**、需显式 <c>--enable-test-control</c> 开关、
/// 生产 Profile 的配置 schema 中**不可表达**，且每次调用写 Audit
/// （依据 <c>.spec/rules/system.md</c>「dev-only 开关 / 调试后门不得在生产开启」）。
/// </summary>
public interface ISessionAdminPort
{
    AckResult BeginDrain(MonotonicInstant graceDeadline);

    AckResult Kick(ServerSessionId sessionId, string registeredReasonCode);

    /// <summary>
    /// 把不透明字节投进 <see cref="IWorldMutationSink.TryEnqueueOpaqueMutation"/>，
    /// **不构造任何 Envelope**——因此 Session 不需要引用 WorldSlot 或 Simulation.Reference。
    /// </summary>
    AckResult InjectWorldMutation(ServerSessionId onBehalfOf, ReadOnlyMemory<byte> opaqueCommand);
}
