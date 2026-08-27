# pacing 模块

> Tick 触发判定与 Deadline 计时：TickRate 管理、输入批次切割、Tick 边界事实的唯一判定者——启停受 world-slot 聚合根指挥。

## 模块定位与目标

`pacing` 决定**何时**进入一个逻辑 Tick；Runtime 决定 Tick **内部**是什么（13 相语义，架构源 §4.1）。Runtime 从不直接读 Wall Clock，Host 从不定义 Phase 语义（架构源 ADR-001）。v1.1 收权后，`pacing` 是 [world-slot](../world-slot/README.md) 聚合根的**从属调度器**：时钟原语（单调时钟）来自 [host-runtime](../host-runtime/README.md)，启动/暂停/恢复只接受聚合根下发的类型化命令——维护、关闭或任何其他模块都不得直接指挥 pacing。

## 负责什么

- Tick 调度决策：TickRate 管理、下一 Tick 调度时刻计算；在 Simulation Owner Thread（world-slot 拥有）的执行循环中提供"现在应该进入第 N 个 Tick"的判定。时钟读数一律来自 [host-runtime](../host-runtime/README.md) 的单调时钟。
- Deadline 管理：每 Tick 预算、超限检测与归因上报（预算声明遵循架构源 ADR-002/016；归因到 Processor 由 Runtime 完成，`pacing` 提供宿主侧计时）。
- 启停执行：响应 world-slot 的 `StartPacing/PausePacing/ResumePacing` 类型化命令；恢复时不补跑欠账 Tick 还是快进由部署策略声明，且对 Runtime 表现为显式的 Tick 序列，不产生隐式时间跳变。
- 输入批次切割：为 Ingress 消费提供到达时间戳与"本 Tick 批次边界"；迟到输入的 `ArrivalClass` 语义分类归 Runtime，`pacing` 只提供时间事实。
- Tick 边界事实供给：`should_tick` 的判定结果同时标记"当前处于 Tick 边界"这一事实。配置快照等只允许在 Tick 边界生效的动作（架构源 §11.3），由 [world-slot](../world-slot/README.md) 在其 Simulation Owner Thread 的 Tick Barrier 上依据该事实自行应用（切换请求由 [process](../process/README.md) 经聚合命令送达 world-slot）；`pacing` 不提供回调注册，不持有任何回调登记表——回调注册是门审驳回项（全局硬约束 5）。

## 明确不负责什么

- 不拥有单调时钟源（归 [host-runtime](../host-runtime/README.md)）；本模块拥有的是"用时钟做 Tick 调度"的决策状态。
- 不拥有 Logical `TickId`、Phase Graph、Determinism 规则（归 Runtime，架构源 §2.3 RACI）。
- 不拥有 Simulation Owner Thread（归 world-slot）；`pacing` 是该线程循环中被调用的判定器，不是线程本身。
- 不接受 world-slot 之外任何模块的启停指令；维护/关闭对 Tick 的影响只能经聚合根的 Quiesce 序列传导。
- 不消费或解析任何消息内容；不接触 Ingress 队列内容（只提供批次边界）。
- 不换算维护 deadline（`graceDeadlineSeconds` 的单调换算发生在维护路径，属 [maintenance-agent](../maintenance-agent/README.md)/[control-plane-adapter](../control-plane-adapter/README.md) 一侧；Tick 域不参与，架构源 ADR-012）。

## 拥有的状态与资源

- TickRate、每 Tick 预算、暂停位、下一 Tick 调度时刻。
- Tick 计时统计（宿主侧 p50/p95/p99/max 原始样本）。

## 输入、输出与稳定接口

- **输入**：TickRate 与预算配置（来自不可变配置快照）、`StartPacing/PausePacing/ResumePacing` 命令（仅来自 world-slot）、Tick 完成回执（来自 Simulation Owner Thread 循环）。
- **输出**：Tick 触发判定（`TickDecision`，含 Tick 边界事实）、批次切割时间戳、Deadline 超限事件（报 world-slot 与 observability）。
- **稳定接口**：`should_tick(now) -> TickDecision`；`start(epoch)` / `pause(reason, epoch)` / `resume(reason, epoch)`（聚合命令，携带 Slot epoch）；`tick_budget() -> Duration`。无回调注册接口。

## 上游与下游依赖

- **上游**：[world-slot](../world-slot/README.md)（唯一启停指挥方；Simulation Owner Thread 循环调用判定）。
- **下游**：[host-runtime](../host-runtime/README.md)（单调时钟）、[observability](../observability/README.md)（计时事件与 Metrics）。

## 生命周期与状态机

```text
Stopped -> Running <-> Paused -> Stopped
```

- 全部迁移由 world-slot 聚合命令驱动并携带 Slot epoch；旧 epoch 命令以 `StaleEpoch` 拒绝。
- "暂停前先关 Gate、先处置在途事务"的顺序由聚合根的 Quiesce 原子序列保证——本模块只在被命令时执行，不再依赖调用方纪律。
- Runtime 与 Gameplay 回调不能改变 pacing 状态。

## 线程、队列与并发所有权

- 无自有线程、无队列、无回调；全部判定在调用方线程（Simulation Owner Thread 或聚合控制上下文）上执行。
- 暂停位是原子标志；配置切换的应用发生在 world-slot Owner Thread 的 Tick Barrier 上，本模块无并发可变共享。

## 正常数据流与失败路径

- **正常**：`should_tick` 判定到期 → Simulation Owner Thread 取 Ingress 批（按批次边界）→ 经 coreclr-host 调 Runtime Tick → 完成回执 → 计时入样本 → 调度下一 Tick。
- **失败路径**：
  - Tick 超预算：记录归因样本并上报；连续超限达到 Slot Watchdog 阈值（SRV-D-003）时由 world-slot 处置——`pacing` 只报事实不做裁决。
  - 时钟源异常：由 host-runtime 上报进程级诊断；单调基准隔离墙钟跳变。
  - 暂停期间收到触发请求：返回 `Paused` 判定，不排队补偿。
  - 旧 epoch 启停命令：`StaleEpoch` 拒绝并计数。

## 错误分类、恢复与降级

- **可重试**：无（判定是纯函数式的，无失败重试语义）。
- **可拒绝**：非法配置（TickRate 为零、预算为负）在配置编译期拒绝；旧 epoch 命令。
- **可致命**：无独立致命路径（时钟源故障归 host-runtime 上报）。
- **降级**：过载时的 Tick 降频属部署策略声明（不是隐式行为）；任何降频决定记入 Diagnostic 并影响 Deadline 归因基准。

## 配置、Capability 与安全约束

- TickRate、预算、补跑策略来自不可变配置快照；确定性时钟替换由 [host-runtime](../host-runtime/README.md) 在 Adapter 层完成、[host-profiles](../host-profiles/README.md) 声明（Level 2 Determinism 支撑，架构源 §4.4）——本模块对替换无感知。

## 日志、Metrics、Trace 与 Audit

- Metrics：Tick p50/p95/p99/max、超预算次数与归因、暂停时长、批次大小分布（对应架构源 ADR-016 指标）。
- 启停命令执行带原因与 epoch 写 Audit（维护关联 `maintenanceId` 由命令载荷携带）。
- 宿主计时属 Diagnostic 域，不进权威 Simulation Hash。

## 测试面、故障矩阵与性能指标

- **测试面**：Tick pacing 精度、聚合命令启停（含旧 epoch 拒绝）、配置只在 Tick 边界切换、确定性时钟替换下的可重放性。
- **故障矩阵**：系统时间跳变（单调基准免疫）、Tick 持续超预算、暂停中触发、恢复后的序列连续性。
- **性能指标**：触发判定开销（纳秒级，不占 Tick 预算）、1/10/25/50/100/150/200 Bot Workload 下的 Tick 尾延迟曲线。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-001-session-lifecycle.md`（聚合根拥有 Wall Clock 与 pacing 启停；Host 调 Runtime 于 Tick 边界）、`docs/adr/ADR-002-tick-determinism.md`（13 相、预算与队列语义）、`docs/adr/ADR-016-benchmark-workload.md`（Tick 预算与测量口径）。
- 无本模块专属 Schema；Tick 相关公共语义经 `schemas/processor-descriptor.schema.json`（正例 `fixtures/valid/processor-place-voxel.json`，反例 `fixtures/invalid/processor-read-write-conflict.json`）由 Runtime 消费，本模块只对齐预算口径。

## 尚未批准的决策门

- 无本模块专属决策门。Tick 预算与降频策略的具体数值随架构源 ADR-016 的首个基线测量确认；Slot 失活判定阈值归 SRV-D-003；执行器/时钟模型归 SRV-D-012（登记见 [modules/README.md](../README.md) §11.2）。
