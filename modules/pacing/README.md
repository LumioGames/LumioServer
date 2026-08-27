# pacing 模块

> Host Wall Clock、Tick 触发与 Deadline、暂停/恢复、输入批次切割与配置快照的 Tick 边界切换。

## 模块定位与目标

`pacing` 拥有 Host Wall Clock，决定**何时**进入一个逻辑 Tick；Runtime 决定 Tick **内部**是什么（13 相语义，架构源 §4.1）。这条分界是架构源 ADR-001 的核心裁决：Runtime 从不直接读 Wall Clock，Host 从不定义 Phase 语义。`pacing` 同时是"时间相关的运维动作"（暂停、维护窗口、配置切换）与模拟世界之间的唯一闸门。

## 负责什么

- Host Wall Clock 所有权：单调时钟读取、TickRate 管理、下一 Tick 的调度决策。
- Tick 触发：在 Simulation Owner Thread（[world-slot](../world-slot/README.md) 拥有）的执行循环中提供"现在应该进入第 N 个 Tick"的判定；实际调用 Runtime Tick 入口经 [coreclr-host](../coreclr-host/README.md) 的稳定封装。
- Deadline 管理：每 Tick 预算、超限检测与归因上报（预算声明遵循架构源 ADR-002/016；归因到 Processor 由 Runtime 完成，`pacing` 提供宿主侧计时）。
- 暂停/恢复：维护（Quiesce）、调试与 Snapshot 固定期间停止触发新 Tick；恢复时不补跑欠账 Tick 还是快进由部署策略声明，且对 Runtime 表现为显式的 Tick 序列，不产生隐式时间跳变。
- 输入批次切割：为 Ingress 消费提供到达时间戳与"本 Tick 批次边界"；迟到输入的 `ArrivalClass` 语义分类（当前 Tick / 下一 Tick / 拒绝）归 Runtime，`pacing` 只提供时间事实。
- 配置快照切换协调：新配置版本只在 Tick 边界原子生效（架构源 §11.3）；`pacing` 提供该边界回调点，切换动作由 [process](../process/README.md) 发起。

## 明确不负责什么

- 不拥有 Logical `TickId`、Phase Graph、Determinism 规则（归 Runtime，架构源 §2.3 RACI）。
- 不拥有 Simulation Owner Thread（归 [world-slot](../world-slot/README.md)）；`pacing` 是该线程循环中被调用的判定器，不是线程本身。
- 不消费或解析任何消息内容；不接触 Ingress 队列内容（只提供批次边界）。
- 不决定维护是否发生（归 [maintenance](../maintenance/README.md)）；只执行"停止/恢复触发"这一机械动作。

## 拥有的状态与资源

- Wall Clock 源与单调时间基准。
- TickRate、每 Tick 预算、暂停位、下一 Tick 调度时刻。
- Tick 计时统计（宿主侧 p50/p95/p99/max 原始样本）。

## 输入、输出与稳定接口

- **输入**：TickRate 与预算配置（来自不可变配置快照）、暂停/恢复指令（来自维护编排与关闭流程）、Tick 完成回执（来自 Simulation Owner Thread 循环）。
- **输出**：Tick 触发判定、批次切割时间戳、Deadline 超限事件、Tick 边界回调（配置切换点）。
- **稳定接口**：`should_tick(now) -> TickDecision`；`pause(reason)` / `resume(reason)`；`on_tick_boundary(callback)` 注册；`tick_budget() -> Duration`。

## 上游与下游依赖

- **上游**：[world-slot](../world-slot/README.md)（Simulation Owner Thread 循环调用判定）、[maintenance](../maintenance/README.md) 与 [process](../process/README.md)（经编排的暂停/恢复与关闭）。
- **下游**：仅 [observability](../observability/README.md)（计时事件与 Metrics）。

## 生命周期与状态机

```text
Stopped -> Running <-> Paused -> Stopped
```

- `Paused` 进入前必须先完成 Ingress 关闭或在途事务处置（顺序由维护/关闭编排保证，架构源 §3.3：先关 Ingress，再排空/记录在途事务，固定 SnapshotCut，最后停 Tick）。
- 状态迁移只能由编排层发起；Runtime 与 Gameplay 回调不能改变 pacing 状态。

## 线程、队列与并发所有权

- 无自有线程、无队列；全部判定在调用方线程（Simulation Owner Thread 或编排线程）上执行。
- 暂停位是原子标志；配置切换回调只在 Tick 边界的单线程上下文执行，无并发可变共享。

## 正常数据流与失败路径

- **正常**：`should_tick` 判定到期 → Simulation Owner Thread 取 Ingress 批（按批次边界）→ 经 coreclr-host 调 Runtime Tick → 完成回执 → 计时入样本 → 调度下一 Tick。
- **失败路径**：
  - Tick 超预算：记录归因样本并上报；连续超限达到 Slot Watchdog 阈值（SRV-D-003，归 [world-slot](../world-slot/README.md)）时由 Slot 处置——`pacing` 只报事实不做裁决。
  - 时钟异常（系统时间跳变）：单调时钟基准隔离墙上时间跳变；检测到基准异常上报进程级诊断。
  - 暂停期间收到触发请求：返回 `Paused` 判定，不排队补偿。

## 错误分类、恢复与降级

- **可重试**：无（判定是纯函数式的，无失败重试语义）。
- **可拒绝**：非法配置（TickRate 为零、预算为负）在配置编译期拒绝。
- **可致命**：单调时钟源不可用（平台故障）——上报进程级处置。
- **降级**：过载时的 Tick 降频属部署策略声明（不是隐式行为）；任何降频决定记入 Diagnostic 并影响 Deadline 归因基准。

## 配置、Capability 与安全约束

- TickRate、预算、补跑策略来自不可变配置快照；`ClockProfile`（含确定性时钟用于测试）由 [host-profiles](../host-profiles/README.md) 声明。
- 确定性测试 Profile 下 Wall Clock 可被确定性时钟替换——替换发生在 Adapter 层，Runtime 感知不到差异（Level 2 Determinism 支撑，架构源 §4.4）。

## 日志、Metrics、Trace 与 Audit

- Metrics：Tick p50/p95/p99/max、超预算次数与归因、暂停时长、批次大小分布（对应架构源 ADR-016 指标）。
- 暂停/恢复带原因写 Audit（维护关联 `maintenanceId`）。
- 宿主计时属 Diagnostic 域，不进权威 Simulation Hash。

## 测试面、故障矩阵与性能指标

- **测试面**：Tick pacing 精度、暂停/恢复顺序（先关 Ingress 后停 Tick）、配置只在 Tick 边界切换、确定性时钟替换下的可重放性。
- **故障矩阵**：系统时间跳变、Tick 持续超预算、暂停中触发、恢复后的序列连续性。
- **性能指标**：触发判定开销（纳秒级，不占 Tick 预算）、1/10/25/50/100/150/200 Bot Workload 下的 Tick 尾延迟曲线。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-001-session-lifecycle.md`（时钟所有权分界：Host 调 Runtime 于 Tick 边界，Runtime 不读 Wall Clock）、`docs/adr/ADR-002-tick-determinism.md`（13 相、预算与队列语义）、`docs/adr/ADR-016-benchmark-workload.md`（Tick 预算与测量口径）。
- 无本模块专属 Schema；Tick 相关公共语义经 `schemas/processor-descriptor.schema.json`（正例 `fixtures/valid/processor-place-voxel.json`，反例 `fixtures/invalid/processor-read-write-conflict.json`）由 Runtime 消费，本模块只对齐预算口径。

## 尚未批准的决策门

- 无本模块专属决策门。Tick 预算与降频策略的具体数值随架构源 ADR-016 的首个基线测量确认；Slot 失活判定阈值归 SRV-D-003（登记见 [modules/README.md](../README.md) §11.2）。
