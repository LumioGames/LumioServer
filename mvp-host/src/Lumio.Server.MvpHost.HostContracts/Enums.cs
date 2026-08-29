namespace Lumio.Server.MvpHost.HostContracts;

public enum ConnectionCloseReason
{
    OwnerRequest,
    Disconnect,
    Fault,
    PolicyReject,
    MaintenanceKick,
}

/// <summary>与 LumioClient 同名同序——双端故障脚本共用同一口径。</summary>
public enum TransportFaultAction
{
    Pass,
    Drop,
    Duplicate,
    Delay,
    Disconnect,
}

/// <summary>
/// 名称固定。**禁止别名为 <c>ClientReplicaSession</c>、禁止与其做状态映射**（ADR-001）。
/// <c>Faulted</c> 建模但 MVP 期不可达（参考存根恒不产生 <c>SessionLocalProven</c> 见证），
/// 已在 <c>absences.json</c> 的 <c>ABS-SESSION-FAULTED-UNREACHABLE</c> 登记，**不得从状态机删除**。
/// </summary>
public enum ServerConnectionSessionState
{
    Admitted,
    Syncing,
    Active,
    ReconnectWindow,
    Expired,
    Closed,
    Kicked,
    Faulted,
}

public enum AdmissionGateState
{
    Open,
    Closed,
}

/// <summary>13 态，逐字取自 <c>fixtures/valid/state-machine-world-slot-host.json</c> 的 <c>states</c>。</summary>
/// <remarks>
/// <c>NativeReady</c> **不可跳过**：它是通往 <c>ManagedReady</c> 的唯一中间态。MVP 走
/// PureHeadless / NoNative 无 Loader 路径，以「无 Native 可加载」的显式空实现穿过
/// （<c>absences.json</c> 的 <c>ABS-WORLDSLOT-NATIVE</c>），不得删除该状态。
///
/// <c>Faulted</c> 是 fail-stop 终态（ADR-027）：**不得设计任何从它回到活动态的迁移**。
/// </remarks>
public enum WorldSlotHostState
{
    Allocated,
    Bootstrapping,
    NativeReady,
    ManagedReady,
    LoadingSession,
    Running,
    Quiescing,
    Snapshotting,
    Reloading,
    Migrating,
    Stopping,
    Destroyed,
    Faulted,
}

public enum HostSimulationState
{
    Created,
    Initialized,
    Ready,
    Running,
    Paused,
    Draining,
    Snapshotted,
    Disposed,
    Faulted,
}

/// <summary>
/// 故障域分类。
///
/// <c>ids/index.json</c> 的 <c>FaultClass</c> 命名空间**恰 3 值**（实测：
/// <c>SessionLocalProven</c> / <c>SlotStateUnproven</c> / <c>ProcessFault</c>）。
/// <c>None</c> 是**本仓私有的第 4 值**，只表示「本 tick 有正向见证且无故障」，
/// **绝不跨 wire、绝不进任何 <c>reasonCode</c>**。
///
/// 它排在首位（值为 0）是刻意的：配合 <c>HostTickOutcome.FaultClass</c> 的**可空**声明，
/// 「忘了填见证」表现为 <c>null</c>（→ 从严判 <c>SlotStateUnproven</c>），
/// 而不是静默变成「证明无故障」。非空枚举的 <c>default</c> 恰好是 <c>None</c>，
/// 那正是 ADR-006「A caught failure without a FaultClass attestation defaults to
/// SlotStateUnproven」要挡住的失效方式。
/// </summary>
public enum HostFaultClass
{
    None,
    SessionLocalProven,
    SlotStateUnproven,
    ProcessFault,
}

public enum HostTickStatus
{
    Completed,
    Rejected,
    Faulted,
}

public enum CredentialVerdict
{
    Accepted,
    Rejected,
}

public enum AntiReplayVerdict
{
    Ok,
    Replayed,
    OutOfWindow,
}

/// <summary>八步 saga 的 effect，逐条对应 §6.3 的 1..8 步，外加补偿与拒绝两个终止效果。</summary>
public enum AdmissionEffectKind
{
    None,
    ReadGate,
    Authenticate,
    MatchExactRelease,
    ReserveSlot,
    CommitSlot,
    CreateSession,
    BindConnection,
    StartReplication,
    Compensate,
    Reject,
}
