# world-slot 模块

> WorldSlotHost 状态机、资源配额、Simulation Owner Thread 所有权、Runtime/Voxel 句柄持有与 Slot Watchdog。

## 模块定位与目标

`world-slot` 拥有 Host 内承载一个逻辑 Session 的资源与故障单元——`WorldSlot`（架构源 §1.2 术语）。它持有 Runtime `SimulationSession`、GameWorld、VoxelWorld 的**句柄**而非内部状态，拥有每个 active Slot 的 Simulation Owner Thread，并以公共 `WorldSlotHost` 状态机（架构源 §3.2）管理从分配到销毁的全生命周期。V1 建议每进程一个 active WorldSlot；多 Slot 接口保留，但共享进程故障域。

## 负责什么

- WorldSlotHost 状态机执行（公共契约，见下节）；状态迁移只能由本模块发起，Game 的初始化/迁移/销毁回调不能改变 Host 状态机。
- Slot 创建前置校验编排：Release、ABI、Capability 与资源预算校验通过后才 `Allocated`（架构源 §3.3；校验分别委托 [release-router](../release-router/README.md)、[coreclr-host](../coreclr-host/README.md)、[host-profiles](../host-profiles/README.md)）。
- World 创建编排：经 Runtime 创建 GameWorld、经 VoxelEngine 创建 VoxelWorld；两者 Ready 后才进入 `Running`；保存句柄与 Context，不复制、不直接访问内部 Storage。
- Simulation Owner Thread 所有权：每 active Slot 一个；该线程是唯一权威写提交线程与唯一 Managed Tick 入口（架构源 ADR-002/006）。
- 资源配额：Slot 级内存/CPU/实体/队列预算的声明、跟踪与超限处置。
- Slot Watchdog：Tick 超时/失活判定（SRV-D-003）与 `Faulted` 处置。
- 销毁顺序执行：停止新输入 → 完成/中止事务 → 导出证据 → 卸载 Gameplay Scope → 释放 Voxel → 释放 ECS → 关闭 Host（架构源 §3.3）。
- Slot 级诊断：状态迁移历史、配额水位、Watchdog 证据。

## 明确不负责什么

- 不拥有 GameWorld/VoxelWorld/ECS/Coordinator 内部状态（归 Runtime/VoxelEngine）；不创建、销毁或直接访问 ECS Storage。
- 不拥有 Logical `TickId` 与 Phase 语义（归 Runtime）；不决定何时 Tick（归 [pacing](../pacing/README.md)）。
- 不拥有 SimulationSession 状态机（`Created -> ... -> Disposed` 归 Runtime；本模块只经稳定接口驱动其生命周期入口）。
- 不做 Admission 或 Session 路由（归 [session](../session/README.md)）。
- 不裁决进程级故障的恢复策略（归 [process](../process/README.md) 与 [persistence-host](../persistence-host/README.md)）。

## 拥有的状态与资源

- WorldSlot 注册表与每 Slot 的公共状态机当前态。
- Simulation Owner Thread（每 active Slot 一个）。
- Runtime SimulationSession/GameWorld/VoxelWorld 句柄与 Snapshot 元数据。
- Slot 资源配额账本与 Watchdog 计时器。

## 输入、输出与稳定接口

- **输入**：Slot 分配请求（启动/恢复流程）、Session 绑定（来自 [session](../session/README.md)）、Quiesce/Snapshot/销毁指令（来自维护与关闭编排）、Tick 触发判定（来自 [pacing](../pacing/README.md)）。
- **输出**：Slot 状态视图、容量/配额裁决（Admission 输入之一）、Snapshot 提交（送 [persistence-host](../persistence-host/README.md)）、`Faulted` 事件与证据。
- **稳定接口**：`allocate(budget) -> SlotRef | StableError`；`bind_session(slotRef, sessionRef)`；`quiesce(slotRef, reason)`；`snapshot(slotRef) -> SnapshotCutRef`；`destroy(slotRef)`；`capacity() -> QuotaView`。

## 上游与下游依赖

- **上游**：[session](../session/README.md)（绑定与容量查询）、[maintenance](../maintenance/README.md) 与 [process](../process/README.md)（经编排的 Quiesce/销毁）。
- **下游**：[coreclr-host](../coreclr-host/README.md)（Tick 入口、ALC Scope）、[pacing](../pacing/README.md)（Tick 判定）、[persistence-host](../persistence-host/README.md)（Snapshot/WAL 提交）、[observability](../observability/README.md)（事件）。

## 生命周期与状态机

公共契约（架构源 §3.2，规范性）：

```text
Allocated -> Bootstrapping -> NativeReady -> ManagedReady
 -> LoadingSession -> Running <-> Quiescing
 -> Snapshotting / Reloading / Migrating -> Stopping -> Destroyed
任一活动状态 -> Faulted
```

- 任一初始化/卸载/恢复失败进入 `Faulted`，不留半初始化对象；销毁后的迟到回调以稳定错误拒绝且不能改写新 Slot（架构源 ADR-001）。
- `Quiescing` 前必须先关闭 Ingress（顺序由维护/关闭编排保证）。

## 线程、队列与并发所有权

- 拥有 Simulation Owner Thread：从 [network](../network/README.md) Ingress 取批 → 经 [coreclr-host](../coreclr-host/README.md) 入口执行 Runtime Tick → 产物入 Egress；权威状态只在该线程的 Tick Barrier 提交。
- Native Job Pool/Completion Queue 归 CoreEngine/Runtime 侧；Native Completion 只在 Tick Barrier 应用，本模块不消费其内容。
- V1 单 active Slot；多 Slot 时每 Slot 独立线程，但 OOM/CoreCLR 崩溃仍是进程级共享故障域——该限制必须在容量规划中显式声明。

## 正常数据流与失败路径

- **正常**：分配 → 前置校验 → World 创建 → `Running`（Tick 循环）→ 维护/关闭时 `Quiescing -> Snapshotting -> Stopping -> Destroyed`。
- **失败路径**：
  - 前置校验失败：不进入 `Allocated` 之后的任何状态，稳定错误返回。
  - World 创建失败（Runtime/Voxel 任一）：`Faulted`，已创建部分按销毁顺序回收。
  - Watchdog 判定失活（SRV-D-003）：`Faulted`，导出证据；V1 单 Slot 场景下通常升级为进程级处置。
  - 配额超限：按声明策略处置（拒绝新负载/触发维护），计入 Metrics 与 Audit。
  - 销毁中某步失败：继续执行可安全执行的后续步骤，最终态仍为 `Faulted` 并保留证据，不得留下半销毁对象。

## 错误分类、恢复与降级

- **可重试**：无（Slot 级动作不隐式重试；重建由编排层决定）。
- **可拒绝**：容量不足的分配请求、销毁后的迟到操作。
- **可致命**：Slot `Faulted` 在单 Slot 部署下升级为进程级故障，走 Snapshot/WAL 恢复。
- **降级**：无隐式降级；配额收紧是显式配置动作。

## 配置、Capability 与安全约束

- Slot 资源预算（内存/实体/队列）来自不可变配置快照；预算声明遵循架构源 ADR-016 的 per-Session/Pool 预算口径。
- 多 Slot 能力位由 [host-profiles](../host-profiles/README.md) 声明；V1 生产 Profile 固定单 active Slot。
- Slot 隔离不等于安全边界：进程级故障波及全部 Slot，安全隔离以进程/Pool 为界。

## 日志、Metrics、Trace 与 Audit

- 每次状态迁移写 Audit（关联 `worldSlotId`、`sessionId`、`tickId`）。
- Metrics：Slot 状态驻留时长、配额水位（内存/实体/队列）、Watchdog 触发数、Tick 循环取批大小。
- `Faulted` 产出 Failure Bundle 素材（状态迁移历史 + 配额水位 + 最后 N 个 Tick 计时）。

## 测试面、故障矩阵与性能指标

- **测试面**：每个状态迁移、重复销毁、销毁后回调拒绝、配额超限处置、Quiesce 先关 Ingress 的顺序断言、LocalEmbedded 两棵树隔离（Server Slot 与 Client 树无共享对象，架构源 ADR-001 验证清单）。
- **故障矩阵**：World 创建失败回收、Watchdog 失活、OOM 升级进程级、恢复流程重建 Slot。
- **性能指标**：Slot 冷启动时长（Allocated 到 Running）、Quiesce/Snapshot 停顿、1/10/25/50/100/150/200 Bot Workload 下的 Tick p50/p95/p99（对应架构源 ADR-016）。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-001-session-lifecycle.md`（WorldSlotHost 所有权与状态机）、`docs/adr/ADR-002-tick-determinism.md`（Simulation Owner Thread 唯一提交）、`docs/adr/ADR-016-benchmark-workload.md`（预算与测量）。
- 状态机图源：架构源 `docs/architecture/LumioGameEngine_Architecture_v1.0.md` §3.2（本仓镜像 [docs/architecture/LumioGameEngine_Architecture_v1.0.md](../../docs/architecture/LumioGameEngine_Architecture_v1.0.md)）。
- 架构源 `schemas/common.schema.json`（`sessionRevisionVector`，SnapshotCut 身份）：正例 `fixtures/valid/session-revision-vector.json`。

## 尚未批准的决策门

- **SRV-D-003**（Slot Watchdog 判定阈值）：临时默认值为连续 3 个 Tick Deadline 超限或 5 秒无心跳判定失活；Foundation 阶段测量 Tick p99 后确认。登记见 [modules/README.md](../README.md) §11.2。
- 多 Slot 共享与自动扩缩是架构基线声明的 P2 后置能力（非决策门）；接口预留但 V1 不实现，激活须按架构源 §14.3 走独立 ADR。
