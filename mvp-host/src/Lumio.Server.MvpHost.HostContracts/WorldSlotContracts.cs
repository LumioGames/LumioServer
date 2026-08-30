using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.HostContracts;

/// <summary>送给 world-slot 聚合根的类型化命令。</summary>
public abstract record WorldSlotCommand
{
    private WorldSlotCommand()
    {
    }

    public sealed record ReserveAdmission(AdmissionAttemptId Attempt, ServerSessionId Session) : WorldSlotCommand;

    public sealed record CommitAdmission(
        SlotReservationId Reservation,
        ServerSessionId Session,
        SlotEpoch Epoch) : WorldSlotCommand;

    public sealed record AbortAdmission(SlotReservationId Reservation, SlotEpoch Epoch) : WorldSlotCommand;

    public sealed record Quiesce(string Reason, SlotEpoch Epoch) : WorldSlotCommand;

    // CA1716：Stop 是 VB 的保留字。此处定点抑制而非改名——该名字由设计 §6.4 逐字定死，
    // 是跨模块契约面的一部分，改名会让下游卡的「签名逐字相同」验收不可满足；
    // 且本仓不存在 VB 消费方。抑制范围只覆盖这一个类型，不放宽全工程。
    // （与 Platform 的 IThreadBody.Step 同款处置。）
#pragma warning disable CA1716
    public sealed record Stop(SlotEpoch Epoch) : WorldSlotCommand;
#pragma warning restore CA1716

    /// <summary>pacing 发出的 tick 许可。队列容量 1、**不堆积 catch-up**——超时记 overrun。</summary>
    public sealed record TickPermit(LogicalTickToken Tick, SlotEpoch Epoch) : WorldSlotCommand;

    public sealed record DependencyAck(AdmissionAttemptId Attempt, bool Accepted, string? StableErrorId) : WorldSlotCommand;
}

/// <summary>world-slot 发出的类型化事件。</summary>
public abstract record WorldSlotEvent
{
    private WorldSlotEvent()
    {
    }

    public sealed record AdmissionReserved(
        AdmissionAttemptId Attempt,
        SlotReservationId Reservation,
        SlotEpoch Epoch) : WorldSlotEvent;

    public sealed record AdmissionRejected(AdmissionAttemptId Attempt, string StableErrorId) : WorldSlotEvent;

    public sealed record SessionAssociated(ServerSessionId Session, WorldSlotId Slot, SlotEpoch Epoch) : WorldSlotEvent;

    public sealed record TickCompleted(LogicalTickToken Tick, ulong AuthorityRevision, SlotEpoch Epoch) : WorldSlotEvent;

    public sealed record Quiesced(SnapshotCutRef Cut, SlotEpoch Epoch) : WorldSlotEvent;

    /// <summary>Gate 的开关只能由本模块发起并广播——聚合根是唯一所有者，session 只读。</summary>
    public sealed record GateStateChanged(AdmissionGateState State, SlotEpoch Epoch) : WorldSlotEvent;

    public sealed record FaultAdjudicated(FaultAdjudication Adjudication, SlotEpoch Epoch) : WorldSlotEvent;

    public sealed record ReadyToStop(SlotEpoch Epoch) : WorldSlotEvent;
}

/// <summary>
/// world-slot 聚合根。五项收权：① Admission Gate 唯一所有者；② 生命周期 epoch
/// （旧 epoch 命令/ack 一律 <c>StaleEpoch</c> 拒绝）；③ Quiesce/Drain/Snapshot/Stop 原子序列；
/// ④ pacing 启停（不接受其他模块的暂停/恢复指令）；⑤ FaultClass 裁决。
/// </summary>
public interface IWorldSlotHost
{
    AllocateResult Allocate(in SlotBudget budget);

    AckResult BindSession(SlotReservationId reservation, ServerSessionId session, SlotEpoch epoch);

    /// <summary>
    /// 原子序列，顺序固定：关闭 Gate → 排空/记录在途 → 固定 SnapshotCut → 暂停 pacing → 停止。
    /// **任一步失败进入 <c>Faulted</c>，不留半完成状态。**
    /// </summary>
    AckResult Quiesce(string reason, SlotEpoch epoch);

    SnapshotCutRef FixSnapshotCut(SlotEpoch epoch);

    AckResult Destroy(SlotEpoch epoch);

    /// <summary>★ 唯一所有者；session 只读。</summary>
    AdmissionGateState Gate { get; }

    QuotaView Capacity { get; }

    AckResult ReportFault(string registeredErrorCode, HostFaultClass faultClass, SlotEpoch epoch);
}

/// <summary>
/// Narrow admission capability used by Session.  Reservation and compensation
/// are intentionally separate from the broader WorldSlot lifecycle aggregate.
/// </summary>
public interface IWorldSlotAdmissionPort
{
    AdmissionReservationResult ReserveAdmission(AdmissionAttemptId attempt, ServerSessionId session);

    AckResult BindSession(SlotReservationId reservation, ServerSessionId session, SlotEpoch epoch);

    AckResult AbortAdmission(SlotReservationId reservation, SlotEpoch epoch);
}

/// <summary>
/// Narrow pacing capability used by the App owner loop.  A permit carries only
/// the logical tick and slot epoch; the aggregate owns the bounded queue and all
/// epoch/state validation.
/// </summary>
public interface IWorldSlotPacingPort
{
    EnqueueResult EnqueueTickPermit(LogicalTickToken tick, SlotEpoch epoch);
}

/// <summary>
/// 故障域裁决。<c>Classify(null)</c>（无见证）**必须**返回 <c>SlotStateUnproven</c>——
/// ADR-006 的从严默认。**Host 永不从「异常是否被捕获」推断故障域。**
/// </summary>
public interface IFaultAdjudicator
{
    FaultAdjudication Classify(HostFaultClass? witness);
}
