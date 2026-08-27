# LumioServer `world-slot` 模块实现级设架

> 架构基线：`LGE-V1.2-2026-08-27`  
> 实现优先级：**P0**  
> crate：`lumio-world-slot`  
> 本文只定义仓内实现结构；公共 Schema / ABI / ID / 状态机仍由 `LumioGameEngineArchitecture` 维护。

## A. 一句话职责与非职责

**职责：** `WorldSlotHost` 的唯一 Host 聚合根：拥有 slot lifecycle、Admission Gate、slot epoch、quota、Simulation Owner Thread 和对 pacing/coreclr/persistence/transport 的编排，确保权威变化仅在 Runtime Tick Barrier 应用。

**明确不负责：**
- 不拥有 ECS/Voxel/Game 权威数据结构或 Logical Tick 语义；只持 opaque handles。
- 不拥有 connection/session registry、Release Pool desired state、CoreCLR 装载实现或存储格式。
- 不自行裁决无 Runtime 见证的 FaultClass；缺 witness 固定为 `SlotStateUnproven`。
- V1 不实现多 active slot 或跨进程目标实例激活。

## B. crate、目录与文件清单

建议 package 名：`lumio-world-slot`。文件按所有权/职责切分，不创建 `common.rs`、`globals.rs`、`event_bus.rs` 或大锅文件。

| 文件 | 唯一职责 |
| --- | --- |
| `modules/world-slot/Cargo.toml` | 依赖 coreclr-host/pacing/persistence-host/transport 与 foundations。 |
| `modules/world-slot/src/lib.rs` | 导出 aggregate typed ports/views。 |
| `modules/world-slot/src/epoch.rs` | `SlotEpoch`、generation、stale check。 |
| `modules/world-slot/src/state.rs` | WorldSlotHost 状态机，名称严格对齐架构源。 |
| `modules/world-slot/src/aggregate.rs` | 唯一聚合 owner 与 transition reducer。 |
| `modules/world-slot/src/admission_gate.rs` | open/closed/reservation/commit/abort。 |
| `modules/world-slot/src/quota.rs` | session/queue/memory quota reservations。 |
| `modules/world-slot/src/handles.rs` | Runtime/Voxel/replication opaque handles。 |
| `modules/world-slot/src/owner_thread.rs` | Simulation Owner Thread runner。 |
| `modules/world-slot/src/tick_loop.rs` | drain→Managed tick→barrier result→egress/persist effect 顺序。 |
| `modules/world-slot/src/watchdog.rs` | Slot heartbeat 与 SRV-D-003；不复用 process watchdog。 |
| `modules/world-slot/src/quiesce.rs` | 关闭 admission、停止新 tick、drain/persist/stop。 |
| `modules/world-slot/src/migration.rs` | 仅 host aggregate transition/epoch gate；V1 不做在线跨 release migration。 |
| `modules/world-slot/src/commands.rs` | 所有命令含 expected slot epoch。 |
| `modules/world-slot/src/events.rs` | reservation/tick/quiesce/fault/ready events。 |
| `modules/world-slot/src/fault.rs` | Runtime witness validation 与 SlotStateUnproven 默认。 |
| `modules/world-slot/src/service.rs` | control reducer 与端口 effect dispatcher。 |
| `modules/world-slot/src/error.rs` | stale epoch/quota/state/port/fault witness errors。 |
| `modules/world-slot/tests/aggregate_state_test.rs` | 状态机和唯一 owner。 |
| `modules/world-slot/tests/tick_barrier_test.rs` | 权威输入只能在 barrier 应用。 |
| `modules/world-slot/tests/stale_epoch_test.rs` | 迁移/重建后旧命令全拒绝。 |
| `modules/world-slot/tests/fault_witness_test.rs` | 有/无 witness 分类。 |

## C. 公开/内部类型、Trait、命令、事件与端口

### C.1 类型、命令与事件
- `WorldSlotHost`、`WorldSlotHostState`（来自/对齐 generated contract）、`SlotEpoch`。
- `AdmissionGateState`、`SlotReservationId`、`SlotQuotaReservation`。
- `WorldSlotCommand::{ReserveAdmission, CommitAdmission, AbortAdmission, Quiesce, Stop, TickPermit, DependencyAck, InitiateAggregateMigration}`。
- `WorldSlotEvent::{AdmissionReserved, AdmissionRejected, SessionAssociated, TickCompleted, Quiesced, PersistenceRequired, FaultAdjudicated, ReadyToStop}`。
- `RuntimeFaultWitness { faultClass, witnessId, tick/correlation }` 只消费 generated contract。

### C.2 稳定仓内端口
| 接口 | 落点 | 契约 |
| --- | --- | --- |
| `WorldSlotCommandPort::try_send(WorldSlotCommand)` | `commands.rs` | 所有 transition 命令都携带 `expectedEpoch` 与 ack id。 |
| `WorldSlotEventPort::try_recv()` | `events.rs` | session/maintenance/process 消费；不共享 aggregate 引用。 |
| `WorldSlotQueryPort::snapshot()` | `service.rs` | 返回 immutable state/epoch/gate/quota view。 |
| `SimulationOwnerRunner::run()` | `owner_thread.rs` | host-runtime 唯一线程入口；内部固定 tick loop。 |
| `FaultAdjudicator::classify(optional_witness)` | `fault.rs` | 有见证原样消费 FaultClass；无见证返回 SlotStateUnproven。 |

### C.3 Rust 签名草案

以下签名是**仓内实现接口**，不是新增公共 ABI；实现时只能引用上游 generated 类型，不能复制公共字段。

```rust
impl WorldSlotCommandPort {
    pub fn try_send(&self, command: WorldSlotCommand) -> Result<(), WorldSlotPortError>;
}

impl WorldSlotHost {
    pub fn reduce(
        &mut self,
        command: WorldSlotCommand,
    ) -> Result<WorldSlotEffects, WorldSlotError>;

    pub fn snapshot(&self) -> WorldSlotView;
}

impl SimulationOwnerRunner {
    pub fn run(self, context: SimulationOwnerContext) -> SimulationOwnerTerminal;
}

impl FaultAdjudicator {
    pub fn classify(
        witness: Option<&GeneratedRuntimeFaultWitness>,
    ) -> FaultAdjudication;
}
```

## D. 状态、资源与生命周期所有权

- `WorldSlotHost` 聚合状态、`SlotEpoch`、唯一 active slot（D-001 provisional）、Admission Gate。
- slot quota/reservations、Session association view、Runtime/Voxel opaque handles。
- Simulation Owner Thread lifecycle、Tick ingress batch边界、heartbeat/watchdog evidence。
- Quiesce/Persist/Stop transition progress；所有 host aggregate migration 只能由这里发起并递增 epoch。

### D.1 模块红线
- 只有 world-slot 可发起 Host aggregate migration；所有外来命令必须含 epoch，旧 epoch=`StaleEpoch`。
- 网络/IO/Native completion 不得直接调用 Managed Gameplay。

## E. 线程、队列、背压与关闭

### E.1 线程/执行上下文
- Simulation Owner Thread 由 host-runtime 创建；只有它调用 Managed Tick 热路径、drain ingress、提交 egress。
- world-slot control reducer 在受界 control lane；控制命令通过 `AggregateInbox` 到 owner 安全点。
- pacing 只投递 `TickPermit`；Owner Thread 决定在何时进入 Runtime Tick entry。
- 不得 blocking I/O；persistence/transport 都用非阻塞有界端口与显式 ack。

### E.2 队列合同
| 队列 | 元素 | 所有者 | 生产者 | 消费者 | 顺序 | 容量配置 | 满载动作 | 关闭语义 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `WorldSlotAggregateInbox` | `WorldSlotCommand | dependency event` | world-slot | session/maintenance/process/pacing/coreclr/persistence/timer | world-slot control/owner | FIFO by slot epoch + sequence | `world_slot.aggregate.capacity` | 返回 `AggregateBusy`；quiesce/stop 使用保留槽 | stop 后只接收终态查询/迟到 ack 并拒绝 |
| `TickPermitQueue` | `TickPermit` | world-slot | pacing | Simulation Owner Thread | 严格 FIFO SPSC | `pacing.tick_permit.capacity` | pacing 记录 overrun，不堆积 catch-up | quiesce 时关闭 producer，drain 当前 permit |
| `NativeCompletionQueue` | generated native completion | Runtime/CoreEngine | native workers | Simulation Owner Thread | contract-defined order | `world_slot.native_completion.capacity` | 停止新 tick 并请求 Runtime witness；不丢权威 completion | slot stop 时按 ABI drain/abandon contract |

这些行必须写入仓库 Queue Contract Registry；数值来自配置快照，SRV-D 默认值不得提升为公共常量。

## F. 失败分类、恢复与显式 Ack

| 分类 | 实现要求 |
| --- | --- |
| 可拒绝 | stale epoch、gate closed、quota 不足、非法 transition；返回 typed ack，不改 aggregate。 |
| 可重试 | persistence/egress 暂时背压；停在当前安全状态并按有界 retry policy。 |
| Session 级 | Runtime witness 明确 Session fault 时发 session fault event，不扩大到 slot。 |
| Slot/Process | Runtime witness 明确 Slot/Process；无 witness 为 SlotStateUnproven，默认停止 slot 并收集 bundle，不猜测可恢复性。 |

## G. 编译依赖、成熟方案与 Adapter 隔离

### G.1 编译依赖
**允许：**
- coreclr-host
- pacing
- persistence-host
- transport
- host-runtime
- observability
- host-profiles
- generated architecture/Managed/Core contracts

**禁止：**
- session/auth/release/maintenance/process 反向依赖
- Gameplay/ECS/Voxel 实现源码
- protocol-dispatch
- shared mutable connection/session state

### G.2 成熟方案选择
| 方案 | 用途 | 成熟性、许可证与隔离 |
| --- | --- | --- |
| `rtrb`（经 host-runtime） | Tick permit/ingress SPSC | 固定容量、亲和；world-slot 不直接暴露供应商。 |
| `atomic-wait`/parking primitives（仅 host-runtime 内） | owner idle wake | 不在本 crate 自选；避免自研 scheduler。 |
| generated ABI crates | Runtime/CoreEngine entry | 单一事实源；不手写 FFI table。 |

### G.3 明确拒绝的自研项
- 不自研 ECS scheduler、Gameplay loop、native worker pool、线程池或 timer。
- 必须自有 aggregate reducer/epoch，因为现成 actor/workflow 框架无法保证 Tick Barrier、SPSC owner、无回调和 StaleEpoch 语义。

## H. 测试面与 Fixture

- 状态机：每个合法/非法 transition、幂等重试、epoch rollover policy。
- Property：任何命令序列中 gate/slot lifecycle 只有一个 owner且旧 epoch 不改变状态。
- Tick：network/native completion 仅在 owner thread barrier apply，控制命令只在安全点观察。
- 故障：Managed panic/exception、有/无 Runtime witness、persistence saturation、owner heartbeat stall。
- Soak：反复 create/quiesce/destroy 后线程、queue、handle、CoreCLR scope 资源归零或有明示 retained evidence。

## I. 决策门与配置默认

- D-001 单 active slot 是 provisional composition default；`SlotEpoch`/registry 设计保留未来多 slot，不实现其行为。
- SRV-D-003 只控制 Slot Watchdog 默认；必须通过 benchmark 校准。
- D-005 durability policy 由 persistence ack 表达，world-slot 不假设 fsync/group commit。

## J. 本模块任务卡

| 任务 | Wave | 单一目标 | 依赖 |
| --- | --- | --- | --- |
| [`implement-world-slot-aggregate-epoch-admission-and-quota`](../../../.spec/tasks/implement-world-slot-aggregate-epoch-admission-and-quota.md) | Wave 6 | 建立唯一aggregate reducer、slot epoch、reservation/commit/abort和所有命令的StaleEpoch门。 | `implement-host-runtime-supervision-cancellation-and-join`, `implement-pacing-timer-driven-scheduler`, `implement-coreclr-lifecycle-and-fault-passthrough`, `implement-persistence-durable-streams-queues-and-acks`, `implement-transport-registry-bounded-ingress-egress` |
| [`implement-world-slot-simulation-owner-loop`](../../../.spec/tasks/implement-world-slot-simulation-owner-loop.md) | Wave 7 | 建立host-runtime owned runner，固定执行permit→bounded ingress/native completion drain→Managed Tick→barrier outcome→egress/persistence effects。 | `implement-world-slot-aggregate-epoch-admission-and-quota`, `implement-coreclr-netcorehost-adapter`, `implement-persistence-recovery-checkpoint-and-migration-adapter`, `implement-transport-local-embedded-fidelity-adapter` |
| [`implement-world-slot-quiesce-migration-and-fault-adjudication`](../../../.spec/tasks/implement-world-slot-quiesce-migration-and-fault-adjudication.md) | Wave 8 | 封闭close admission→stop new tick→drain→persist→stop流程，并确保只有world-slot发起aggregate migration/epoch更新。 | `implement-world-slot-simulation-owner-loop`, `implement-persistence-durability-fault-matrix`, `implement-observability-failure-bundle-and-emergency-path` |
| [`implement-world-slot-resource-and-watchdog-soak`](../../../.spec/tasks/implement-world-slot-resource-and-watchdog-soak.md) | Wave 9 | 验证create/quiesce/destroy/recreate和owner stall下线程、队列、handle、epoch、evidence终态。 | `implement-world-slot-quiesce-migration-and-fault-adjudication` |

## K. 完成定义

本模块的任务卡全部通过；模块单测/fixture/故障测试通过；Queue Registry、Cargo DAG、许可证与源码红线检查通过；没有供应商类型泄漏、无界队列、任意回调或未见证 FaultClass 推断。
