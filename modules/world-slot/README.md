# world-slot 模块

> Host 侧唯一聚合根：WorldSlotHost 状态机、生命周期 epoch、Host Admission Gate、Quiesce/Drain/Snapshot/Stop 原子序列、故障分级裁决、资源配额与 Simulation Owner Thread。

## 模块定位与目标

`world-slot` 拥有 Host 侧唯一聚合根 `WorldSlotHost`（架构源 ADR-001，v1.1 起为公共契约）：Host Admission Gate、pacing 启停、Quiesce/Drain/Snapshot/Stop 原子序列与 Host 生命周期 epoch 全部归本模块。其他 Host 子组件（接入执行、pacing 调度、传输、持久化、维护进度）可以持有内部状态，但**不得发起聚合迁移**——它们只执行本模块下发的类型化命令并返回显式 ack。本模块同时拥有承载逻辑 Session 的资源与故障单元 `WorldSlot`：持有 Runtime `SimulationSession`、GameWorld、VoxelWorld 的**句柄**而非内部状态，拥有每个 active Slot 的 Simulation Owner Thread。V1 每进程一个 active WorldSlot；多 Slot 接口保留，但共享进程故障域。

## 负责什么

- **聚合根职责（v1.1 收权）**：
  - Host Admission Gate：接入闸门的唯一所有者；开/关只能由本模块发起（关闭请求来自维护/关闭编排的类型化命令，本模块执行并广播 `GateStateChanged` 事件；[session](../session/README.md) 在 Admission 管道读取闸门状态）。
  - 生命周期 epoch：每次聚合迁移携带 epoch；旧 epoch 的命令或 ack 以稳定错误 `StaleEpoch` 拒绝且不能改写聚合（销毁后迟到回调的拒绝由此机制统一实现）。
  - Quiesce/Drain/Snapshot/Stop 原子序列：`关闭 Gate → 排空/记录在途事务 → 固定 SnapshotCut → 暂停 pacing → 停止` 的顺序由本模块作为单一序列执行，任何一步失败进入 `Faulted`，不留半完成状态；序列各步骤对 [maintenance-agent](../maintenance-agent/README.md)/[process](../process/README.md) 报告带 epoch 的进度 ack。
  - pacing 启停：向 [pacing](../pacing/README.md) 下发 `StartPacing/PausePacing/ResumePacing` 类型化命令；pacing 不再接受任何其他模块的暂停/恢复指令。
- **故障分级裁决（v1.1 收权，架构源 ADR-006）**：消费 [coreclr-host](../coreclr-host/README.md) 转交的 Runtime 见证 `FaultClass`：
  - `SessionLocalProven`：Runtime 证明该 Session 的 Tick 效果未提交或已回滚——本模块向 [session](../session/README.md) 下发该 Session 的隔离终结命令，Slot 继续服务。
  - `SlotStateUnproven`：Runtime 无法证明权威状态完整——本模块把 Slot 转 `Faulted`，从最近有效 Snapshot 恢复；**缺 FaultClass 见证的捕获故障一律按此处理**（默认从严）。
  - `ProcessFault`：转交 [process](../process/README.md) 进程级处置。
  - Host 不从"异常是否被捕获"推断故障域；hosting 桥不自行裁决。
- WorldSlotHost 状态机执行（公共契约，见下节）；状态迁移只能由本模块发起，Game 的初始化/迁移/销毁回调不能改变 Host 状态机。
- Slot 创建前置校验编排：Release、ABI、Capability 与资源预算校验通过后才 `Allocated`（校验分别委托 [release-agent](../release-agent/README.md)、[coreclr-host](../coreclr-host/README.md)、[host-profiles](../host-profiles/README.md)）。
- World 创建编排：经 Runtime 创建 GameWorld、经 VoxelEngine 创建 VoxelWorld；两者 Ready 后才进入 `Running`；保存句柄与 Context，不复制、不直接访问内部 Storage。
- Simulation Owner Thread 所有权：每 active Slot 一个；该线程是唯一权威写提交线程与唯一 Managed Tick 入口（架构源 ADR-002/006）。
- 资源配额：Slot 级内存/CPU/实体/队列预算的声明、跟踪与超限处置；Session 绑定的容量裁决。
- Slot Watchdog：Tick 超时/失活判定（SRV-D-003）与 `Faulted` 处置。
- 销毁顺序执行：停止新输入 → 完成/中止事务 → 导出证据 → 卸载 Gameplay Scope → 释放 Voxel → 释放 ECS → 关闭 Host（架构源 §3.3）。
- Slot 级诊断：状态迁移历史（含 epoch）、配额水位、Watchdog 证据。

## 明确不负责什么

- 不拥有 GameWorld/VoxelWorld/ECS/Coordinator 内部状态（归 Runtime/VoxelEngine）；不创建、销毁或直接访问 ECS Storage。
- 不拥有 Logical `TickId` 与 Phase 语义（归 Runtime）；不做 Tick 到期判定（归 [pacing](../pacing/README.md)——但 pacing 的启停归本模块）。
- 不拥有 SimulationSession 状态机（`Created -> ... -> Disposed` 归 Runtime；本模块只经稳定接口驱动其生命周期入口）。
- 不做身份认证、Admission 管道编排或 Session 注册表（归 [session](../session/README.md)；本模块只拥有闸门状态与容量裁决）。
- 不拥有维护命令进度（归 [maintenance-agent](../maintenance-agent/README.md)）；本模块执行其下发的 Quiesce 命令并回 ack，不理解维护语义。
- 不产生 `FaultClass` 见证（归 Runtime，经 coreclr-host 原样转交）；本模块只依据见证裁决处置。

## 拥有的状态与资源

- WorldSlot 注册表与每 Slot 的公共状态机当前态 + 生命周期 epoch。
- Host Admission Gate 状态（开/关 + 原因 + epoch）。
- Simulation Owner Thread（每 active Slot 一个，经 [host-runtime](../host-runtime/README.md) 受监督创建）。
- Runtime SimulationSession/GameWorld/VoxelWorld 句柄与 Snapshot 元数据。
- Slot 资源配额账本与 Watchdog 状态（Watchdog 定时经 host-runtime Timer 投递）。
- 聚合命令收件箱（有界，SRV-D-015 约定）。

## 输入、输出与稳定接口

- **输入**：Slot 分配请求（启动/恢复流程）、Session 绑定/解绑命令（来自 [session](../session/README.md)）、`QuiesceForMaintenance/QuiesceForShutdown/ConfigActivation` 类型化命令（来自 maintenance-agent/process；`ConfigActivation` 携带待生效配置快照，由 Owner Thread 在 Tick Barrier 上原子应用）、Tick 到期判定（来自 pacing，Owner Thread 循环内拉取）、`FaultClass` 见证（来自 coreclr-host）。
- **输出**：Slot 状态视图与 `GateStateChanged` 事件、带 epoch 的序列进度 ack（`AdmissionClosed/Drained/SnapshotCut/Stopped`）、容量/配额裁决、Snapshot 提交（送 [persistence-host](../persistence-host/README.md)）、Session 隔离命令（送 session）、`Faulted` 事件与证据。
- **稳定接口**：`allocate(budget) -> SlotRef | StableError`；`bind_session(slotRef, sessionRef) -> Ok | StableError`（闸门关闭或配额不足时稳定拒绝）；`quiesce(slotRef, reason, epoch) -> 进度 ack 流`；`snapshot(slotRef) -> SnapshotCutRef`；`destroy(slotRef, epoch)`；`gate() -> GateView`；`capacity() -> QuotaView`；`report_fault(slotRef, errorCode, faultClass, epoch)`。

## 上游与下游依赖

- **上游**：[session](../session/README.md)（绑定与容量/闸门查询）、[maintenance-agent](../maintenance-agent/README.md) 与 [process](../process/README.md)（类型化 Quiesce/销毁命令）。
- **下游**：[coreclr-host](../coreclr-host/README.md)（Tick 入口、ALC Scope）、[pacing](../pacing/README.md)（启停命令与到期判定）、[persistence-host](../persistence-host/README.md)（Snapshot/WAL 提交）、[transport](../transport/README.md)（Owner Thread 拉取 Ingress 批）、[host-runtime](../host-runtime/README.md)（线程监督、Watchdog Timer、取消树）、[observability](../observability/README.md)（事件）。

## 生命周期与状态机

公共契约（架构源 §3.2，规范性）：

```text
Allocated -> Bootstrapping -> NativeReady -> ManagedReady
 -> LoadingSession -> Running <-> Quiescing
 -> Snapshotting / Reloading / Migrating -> Stopping -> Destroyed
任一活动状态 -> Faulted
```

- 每次迁移携带并递增生命周期 epoch；旧 epoch 命令/ack 以 `StaleEpoch` 稳定拒绝（架构源 ADR-001，ID Registry `ErrorCode.StaleEpoch`）。
- 任一初始化/卸载/恢复失败进入 `Faulted`，不留半初始化对象。
- `Quiescing` 序列内部保证先关 Gate 再停 Tick；顺序不再依赖外部编排纪律——它是本模块的原子序列实现。

## 线程、队列与并发所有权

- 拥有 Simulation Owner Thread：从 [transport](../transport/README.md) Ingress 取批 → 经 [coreclr-host](../coreclr-host/README.md) 入口执行 Runtime Tick → 产物入 Egress；权威状态只在该线程的 Tick Barrier 提交。
- 聚合命令收件箱：有界 FIFO，命令携带 epoch，消费在 Owner Thread 或聚合控制上下文串行执行——聚合迁移永远单线程裁决。
- Native Job Pool/Completion Queue 归 CoreEngine/Runtime 侧；Native Completion 只在 Tick Barrier 应用，本模块不消费其内容。
- V1 单 active Slot；多 Slot 时每 Slot 独立线程，但 OOM/CoreCLR 崩溃仍是进程级共享故障域——该限制必须在容量规划中显式声明。

## 正常数据流与失败路径

- **正常**：分配 → 前置校验 → World 创建 → `Running`（Tick 循环）→ 维护/关闭时收到 Quiesce 命令 → 原子序列（关 Gate → Drain → SnapshotCut → 停 pacing）→ `Stopping -> Destroyed`，每步回 ack。
- **失败路径**：
  - 前置校验失败：不进入 `Allocated` 之后的任何状态，稳定错误返回。
  - World 创建失败（Runtime/Voxel 任一）：`Faulted`，已创建部分按销毁顺序回收。
  - `SessionLocalProven` 故障：隔离命令下发 session，Slot 继续；隔离结果写 Audit。
  - `SlotStateUnproven` 故障（含缺见证的默认）：Slot `Faulted`，从最近有效 Snapshot 恢复。
  - Watchdog 判定失活（SRV-D-003）：`Faulted`，导出证据；V1 单 Slot 场景下通常升级为进程级处置。
  - 配额超限：按声明策略处置（拒绝新绑定/触发维护），计入 Metrics 与 Audit。
  - 旧 epoch 命令/ack 到达：`StaleEpoch` 拒绝并计数，聚合状态不受影响。
  - 销毁中某步失败：继续执行可安全执行的后续步骤，最终态仍为 `Faulted` 并保留证据，不得留下半销毁对象。

## 错误分类、恢复与降级

- **可重试**：无（Slot 级动作不隐式重试；重建由编排层决定）。
- **可拒绝**：容量不足或闸门关闭时的绑定请求、旧 epoch 的命令与 ack、销毁后的迟到操作。
- **可致命**：Slot `Faulted` 在单 Slot 部署下升级为进程级故障，走 Snapshot/WAL 恢复。
- **降级**：无隐式降级；配额收紧是显式配置动作。

## 配置、Capability 与安全约束

- Slot 资源预算（内存/实体/队列）来自不可变配置快照；预算声明遵循架构源 ADR-016 的 per-Session/Pool 预算口径。
- 多 Slot 能力位由 [host-profiles](../host-profiles/README.md) 声明；V1 生产 Profile 固定单 active Slot。
- Slot 隔离不等于安全边界：进程级故障波及全部 Slot，安全隔离以进程/Pool 为界。

## 日志、Metrics、Trace 与 Audit

- 每次状态迁移写 Audit（关联 `worldSlotId`、epoch、`sessionId`、`tickId`；correlation `scope` 按事件层级取 `World` 或 `Session`）。
- Metrics：Slot 状态驻留时长、配额水位、Watchdog 触发数、Tick 循环取批大小、`StaleEpoch` 拒绝数、FaultClass 三类计数。
- `Faulted` 产出 Failure Bundle 素材（状态迁移历史 + epoch 轨迹 + 配额水位 + 最后 N 个 Tick 计时），以持续发布的不可变证据快照形式供 [observability](../observability/README.md) 装配（架构源 ADR-011 provider 模型：装配器不回调故障模块）。

## 测试面、故障矩阵与性能指标

- **测试面**：每个状态迁移、epoch 递增与 `StaleEpoch` 拒绝、重复销毁、销毁后回调拒绝、配额超限处置、Quiesce 原子序列的步骤顺序断言（Gate 先于停 Tick）、`SessionLocalProven` 隔离单 Session 而 `SlotStateUnproven` 强制 Slot 恢复（架构源 ADR-006 验证清单）、LocalEmbedded 两棵树隔离。
- **故障矩阵**：World 创建失败回收、Watchdog 失活、OOM 升级进程级、恢复流程重建 Slot、缺 FaultClass 见证按 SlotStateUnproven 处理。
- **性能指标**：Slot 冷启动时长（Allocated 到 Running）、Quiesce/Snapshot 停顿、1/10/25/50/100/150/200 Bot Workload 下的 Tick p50/p95/p99（对应架构源 ADR-016）。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-001-session-lifecycle.md`（聚合根、epoch、`StaleEpoch`）、`docs/adr/ADR-002-tick-determinism.md`（Simulation Owner Thread 唯一提交）、`docs/adr/ADR-006-native-managed-abi.md`（FaultClass 裁决义务）、`docs/adr/ADR-016-benchmark-workload.md`（预算与测量）。
- 状态机图源：架构源 `docs/architecture/LumioGameEngine_Architecture_v1.2.md` §3.2（本仓镜像 [docs/architecture/LumioGameEngine_Architecture_v1.2.md](../../docs/architecture/LumioGameEngine_Architecture_v1.2.md)）。
- 架构源 `ids/index.json`：`FaultClass` 命名空间（`SessionLocalProven`/`SlotStateUnproven`/`ProcessFault`）、`ErrorCode.StaleEpoch`。
- 架构源 `schemas/common.schema.json`（`sessionRevisionVector`，SnapshotCut 身份）：正例 `fixtures/valid/session-revision-vector.json`。

## 尚未批准的决策门

- **SRV-D-003**（Slot Watchdog 判定阈值）：临时默认值为连续 3 个 Tick Deadline 超限或 5 秒无心跳判定失活；Foundation 阶段测量 Tick p99 后确认。登记见 [modules/README.md](../README.md) §11.2。
- **SRV-D-015**（聚合命令收件箱容量与 ack 超时）：见 [modules/README.md](../README.md) §11.2。
- 多 Slot 共享与自动扩缩是架构基线声明的 P2 后置能力（非决策门）；接口预留但 V1 不实现，激活须按架构源 §14.3 走独立 ADR。
