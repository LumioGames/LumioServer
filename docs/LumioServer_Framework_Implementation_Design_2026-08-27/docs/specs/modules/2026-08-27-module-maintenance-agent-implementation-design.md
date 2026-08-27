# LumioServer `maintenance-agent` 模块实现级设架

> 架构基线：`LGE-V1.2-2026-08-27`  
> 实现优先级：**P1**  
> crate：`lumio-maintenance-agent`  
> 本文只定义仓内实现结构；公共 Schema / ABI / ID / 状态机仍由 `LumioGameEngineArchitecture` 维护。

## A. 一句话职责与非职责

**职责：** 消费 control-plane-adapter 已验证的 `MaintenanceCommand`，编排本进程 close admission、session drain/kick、slot quiesce、persistence 与 Audit 两个独立 ack，最终产出 `ReadyToExit`。

**明确不负责：**
- 不拥有 WorldSlotHost/Session/Release 状态，只拥有单个维护命令的进度。
- 不启动目标实例、不等待 `TargetActivated`、不修改集群 desired state。
- 不伪造/扩展 MaintenanceCommand wire、scope、mode/action/broadcastCode。
- 不把 persistence ack 当 Audit ack，也不从 sink flush 推断 durable audit。

## B. crate、目录与文件清单

建议 package 名：`lumio-maintenance-agent`。文件按所有权/职责切分，不创建 `common.rs`、`globals.rs`、`event_bus.rs` 或大锅文件。

| 文件 | 唯一职责 |
| --- | --- |
| `modules/maintenance-agent/Cargo.toml` | 依赖 owner 模块 command/event types与 generated MaintenanceCommand。 |
| `modules/maintenance-agent/src/lib.rs` | 导出维护行为端口。 |
| `modules/maintenance-agent/src/command.rs` | generated command validation wrapper；camelCase 不复制。 |
| `modules/maintenance-agent/src/semantics.rs` | Graceful/Forced effect rules。 |
| `modules/maintenance-agent/src/deadline.rs` | grace seconds→monotonic deadline、generation。 |
| `modules/maintenance-agent/src/state.rs` | 维护运行状态与合法转移。 |
| `modules/maintenance-agent/src/progress.rs` | progress sequence/idempotent query。 |
| `modules/maintenance-agent/src/acks.rs` | world/session/release/persistence/audit 独立 ack slots。 |
| `modules/maintenance-agent/src/orchestrator.rs` | 纯 reducer：state+event→effects。 |
| `modules/maintenance-agent/src/commands.rs` | 外部输入和 dependency result。 |
| `modules/maintenance-agent/src/events.rs` | Accepted/Progress/Rejected/ReadyToExit/Failed。 |
| `modules/maintenance-agent/src/evidence.rs` | 维护证据，不含 secrets。 |
| `modules/maintenance-agent/src/error.rs` | conflict/stale/deadline/dependency/ack errors。 |
| `modules/maintenance-agent/tests/graceful_flow_test.rs` | 完整双 ack 流程。 |
| `modules/maintenance-agent/tests/forced_flow_test.rs` | 到期/kick/stop。 |
| `modules/maintenance-agent/tests/idempotency_test.rs` | duplicate command/progress。 |
| `modules/maintenance-agent/tests/dual_ack_test.rs` | 任一 ack 缺失均不得 ReadyToExit。 |

## C. 公开/内部类型、Trait、命令、事件与端口

### C.1 类型、命令与事件
- `MaintenanceRunId`、`MaintenanceRunState`、`MaintenanceProgress`。
- `VerifiedMaintenanceCommand` 只包装 generated `MaintenanceCommand`。
- `MaintenanceCommandInput` 仅包含已验证的 generated `MaintenanceCommand` wrapper；不存在本地伪 wire 分支。
- `MaintenanceEvent::{Accepted, Progressed, Rejected, Failed, ReadyToExit}`。
- `DurableAckState { persistence: Option<...>, audit: Option<...> }` 两字段独立。

### C.2 稳定仓内端口
| 接口 | 落点 | 契约 |
| --- | --- | --- |
| `MaintenanceCommandPort::try_send(VerifiedMaintenanceCommand)` | `commands.rs` | control-plane-adapter 唯一生产者。 |
| `MaintenanceDependencyEventPort::try_send(MaintenanceDependencyEvent)` | `commands.rs` | 所有 ack 带 run/command/expected epoch。 |
| `MaintenanceEventPort::try_recv()` | `events.rs` | process/control-plane 消费。 |
| `MaintenanceReducer::reduce(state, event)` | `orchestrator.rs` | 纯 reducer 只产生已有图上的 typed effects。 |

### C.3 Rust 签名草案

以下签名是**仓内实现接口**，不是新增公共 ABI；实现时只能引用上游 generated 类型，不能复制公共字段。

```rust

impl MaintenanceCommandPort {
    pub fn try_send(
        &self,
        command: VerifiedMaintenanceCommand,
    ) -> Result<(), MaintenancePortError>;
}

impl MaintenanceReducer {
    pub fn reduce(
        state: MaintenanceRunState,
        input: MaintenanceInput,
    ) -> Result<(MaintenanceRunState, MaintenanceEffects), MaintenanceError>;
}

impl DurableAckState {
    pub fn record_persistence(&mut self, ack: PersistenceCommitAck) -> AckOutcome;
    pub fn record_audit(&mut self, ack: AuditDurableAck) -> AckOutcome;
    pub fn is_complete(&self) -> bool;
}
```

## D. 状态、资源与生命周期所有权

- `MaintenanceRun`、`maintenanceId`/幂等性、局部 state/progress、deadline timer generation。
- 每个下游 typed command 的 ack correlation 和重试次数。
- persistence completion 与 Audit durable completion 两个独立 latch。
- 最终 `MaintenanceEvidence` 与 `ReadyToExit` event。

### D.1 模块红线
- 终态是 `ReadyToExit`，没有 `TargetActivated`。
- persistence commit ack 与 Audit durable ack 互不蕴含。

## E. 线程、队列、背压与关闭

### E.1 线程/执行上下文
- 单写 orchestrator runner，经 host-runtime bounded executor。
- deadline 使用收到命令时的 monotonic instant + generated `graceDeadlineSeconds`；到期由 TimerFired。
- 不阻塞等待；每个 effect 都以 event/ack 回到 maintenance inbox。
- 无自有线程/sleep；Forced/Graceful 都是显式状态转移。

### E.2 队列合同
| 队列 | 元素 | 所有者 | 生产者 | 消费者 | 顺序 | 容量配置 | 满载动作 | 关闭语义 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `VerifiedMaintenanceCommandQueue` | `VerifiedMaintenanceCommand` | control-plane-adapter | control-plane verifier | maintenance-agent | FIFO/idempotency by maintenanceId | `control_plane.verified.capacity` | 重复返回已有 progress；满载以稳定错误拒绝 | Stopping 期拒新命令 |
| `MaintenanceInbox` | 下游 ack/event/timer | maintenance-agent | world-slot/session/release/persistence/observability/timer | maintenance runner | FIFO by run id | `maintenance.inbox.capacity` | 关键 ack 保留槽；饱和升级进程不可证明 | 终态 drain |
| `MaintenanceEventOutbox` | `MaintenanceEvent` | process/control-plane | maintenance runner | consumers | progress sequence | `maintenance.event.capacity` | progress 可合并，ReadyToExit 不可丢 | 终态 ack 后关闭 |

这些行必须写入仓库 Queue Contract Registry；数值来自配置快照，SRV-D 默认值不得提升为公共常量。

## F. 失败分类、恢复与显式 Ack

| 分类 | 实现要求 |
| --- | --- |
| 可拒绝 | scope 不适用、本进程 release 不匹配、并发冲突命令、stale command/fencing；无状态改变。 |
| 可重试 | 下游 port full/暂时 backpressure；同 effect id 有界重试。 |
| Forced escalation | grace deadline 到期：停止新 admission/tick，kick 剩余 session，继续等待两个 durable ack或明确失败。 |
| 进程失败 | aggregate state 无法证明、任一 required durable ack 永久失败、关键 ack 队列失真；生成 bundle并非零退出。 |

## G. 编译依赖、成熟方案与 Adapter 隔离

### G.1 编译依赖
**允许：**
- world-slot
- session
- release-agent
- transport
- persistence-host
- control-plane-adapter
- observability
- host-runtime
- host-profiles
- generated MaintenanceCommand

**禁止：**
- process 反向实现
- cluster orchestrator SDK types
- TargetActivated state
- protocol-dispatch

### G.2 成熟方案选择
| 方案 | 用途 | 成熟性、许可证与隔离 |
| --- | --- | --- |
| host-runtime Timer | grace deadline | 单调、可测试、到期只投命令。 |
| `serde`/generated validator | MaintenanceCommand | 只消费上游 Schema/fixtures。 |
| 无通用 workflow engine | orchestration | 显式 reducer更符合 bounded typed ports/epoch；避免引入回调与持久化第二真相。 |

### G.3 明确拒绝的自研项
- 不自研控制面、集群滚动更新器、timer、通用 saga/workflow engine。
- 维护 reducer 属必要产品编排；通用框架会破坏静态依赖图、显式 ack 与 slot epoch。

## H. 测试面与 Fixture

- Golden：maintenance graceful/forced 正反 fixtures与字段 `graceDeadlineSeconds`。
- 状态：duplicate、conflicting command、deadline、partial failure。
- 双 Ack：persistence-first/audit-first/任一迟到/重复；只有两者均成立可 ReadyToExit。
- Epoch：world-slot rebuild 后旧 quiesce ack/command=`StaleEpoch`。
- E2E：ReadyToExit 后 process 退出；永不产生 TargetActivated。

## I. 决策门与配置默认

- D-003 决定默认 mode；行为支持 generated mode，默认只在配置。
- SRV-D-010 grace 默认值只作为 `graceDeadlineSeconds` 配置缺省（若 Schema允许），不得替换命令字段。
- D-010 只阻塞外部 channel/签名 framing；已验证 typed command 的本地编排可实现。

## J. 本模块任务卡

| 任务 | Wave | 单一目标 | 依赖 |
| --- | --- | --- | --- |
| [`implement-maintenance-command-state-deadline-and-idempotency`](../../../.spec/tasks/implement-maintenance-command-state-deadline-and-idempotency.md) | Wave 9 | 包装generated command、monotonic grace deadline、run state和duplicate/conflict行为。 | `implement-control-plane-injected-channel-and-status-reporting`, `implement-host-runtime-clock-and-timer-delivery`, `implement-session-reconnect-window-and-epoch-races`, `implement-world-slot-quiesce-migration-and-fault-adjudication` |
| [`implement-maintenance-orchestration-and-dual-durable-ack`](../../../.spec/tasks/implement-maintenance-orchestration-and-dual-durable-ack.md) | Wave 11 | 实现纯reducer和effect dispatcher：close gate→drain→quiesce→persist/audit→kick/escalate→ReadyToExit。 | `implement-maintenance-command-state-deadline-and-idempotency`, `implement-session-drain-kick-and-fault-isolation`, `implement-persistence-durability-fault-matrix`, `implement-observability-failure-bundle-and-emergency-path`, `implement-release-local-member-state-health-and-reporting` |

## K. 完成定义

本模块的任务卡全部通过；模块单测/fixture/故障测试通过；Queue Registry、Cargo DAG、许可证与源码红线检查通过；没有供应商类型泄漏、无界队列、任意回调或未见证 FaultClass 推断。
