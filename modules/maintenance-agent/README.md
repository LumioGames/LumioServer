# maintenance-agent 模块

> 维护命令进度状态机、滚动更新推进、MaintenanceKick 编排与维护证据——不拥有生命周期，只指挥聚合根并等待 ack。

## 模块定位与目标

`maintenance-agent` 把一条已验证的维护命令翻译为对聚合根与各所有者的**类型化命令序列**，跟踪进度、收集证据。它不拥有任何生命周期状态机：接入闸门、Quiesce/Drain/Snapshot/Stop 序列归 [world-slot](../world-slot/README.md) 聚合根；Session 终结归 [session](../session/README.md)；本模块只拥有"这条 `maintenanceId` 走到了哪一步、证据是什么"。集群期望状态与目标实例激活归外部控制面（架构源 ADR-012，v1.1）：本进程的维护终态是 `ReadyToExit`/退出——**起新实例不是本进程的动作**，旧版进度机中的 `TargetActivated` 阶段已按此裁决删除。维护命令永远携带 `productId + gameReleaseId + releasePoolId` 作用域，默认只影响目标 Pool；集群级维护由控制面显式对多个 Pool 分别下发。

## 负责什么

- 命令消费与语义校验：从 [control-plane-adapter](../control-plane-adapter/README.md) 的已验证队列拉取命令（签名/Schema/fencing/幂等已在该模块完成）；本模块做语义校验——同一 Pool 同时只允许一个活动维护命令，`Forced` 必须 `graceDeadlineSeconds = 0`、`Graceful` 必须 `>= 1`（架构源 Schema 语义规则，对应反例 Fixture `fixtures/invalid/maintenance-forced-with-grace.json`）。
- deadline 换算：收到命令时**一次性**把 `graceDeadlineSeconds` 换算为 [host-runtime](../host-runtime/README.md) 单调时钟 deadline 并注册 Timer；deadline 属 Wall/单调时钟域，与 Logical Tick 无关，无活跃 Slot、Tick 暂停或墙钟跳变时照常收敛（架构源 ADR-012）。`issuedAt` 只用于审计排序。
- `Graceful` 编排：向 world-slot 下发 `QuiesceForMaintenance`（聚合根执行关 Gate → Drain → SnapshotCut → 停 pacing 的原子序列并回带 epoch 的进度 ack）→ 经 [transport](../transport/README.md) 广播原因与剩余宽限 → 等待落盘**双 ack**——persistence commit ack（来自 [persistence-host](../persistence-host/README.md)）与 Audit durable ack（来自 [observability](../observability/README.md)）是两个独立完成信号，互不蕴含（架构源 ADR-011）→ deadline 到达后向 session 下发 `KickRemaining`。
- `Forced` 编排：立即下发 Quiesce（零宽限变体：跳过 Drain 等待）→ 尽最大努力落盘（证据不完整须显式标注）→ 广播 `MaintenanceKick` 并下发全量踢出。
- 滚动更新推进：驱动 [release-agent](../release-agent/README.md) 执行本进程所属 Pool 的状态迁移（`Serving -> Draining -> Empty -> Retired` 侧）；新 Pool 的 `Published -> Verified -> Warmup -> Serving` 发生在**目标实例进程**内，由控制面对其下发，不归本进程。
- Rollback 证据：升级不覆盖旧 Release/Snapshot；回滚动作的证据归档。
- 退出证据：确认目标 Pool 无存留连接后，向 [process](../process/README.md) 请求进入 `Stopping` 并经 control-plane-adapter 报告 `ReadyToExit` 与退出证据；重连由 Catalog 路由到目标 Release（本进程不参与）。
- 维护证据：断开清单、未提交事务清单、时间线，进入 Audit 与 Failure Bundle。

## 明确不负责什么

- 不拥有接入闸门、Quiesce 序列、pacing 启停（归 world-slot 聚合根）；不执行传输断开与广播的机械动作（归 transport，经类型化命令）；不终结 Session（归 session）。
- 不拥有集群期望状态、Pool 替换时机或目标实例激活（归外部控制面）；不验证命令签名/fencing/幂等（归 control-plane-adapter）。
- 不定义维护命令 Schema、`MaintenanceKick` 错误码或 Pool 状态枚举（归架构源）。
- 不做 Snapshot/WAL 的写盘（归 persistence-host）；只等待完成 ack。
- 不实现在线 Session 无感跨 Release 迁移（V1 非目标，D-002）。

## 拥有的状态与资源

- 活动维护命令注册表（`maintenanceId` → 进度状态机、作用域、单调 deadline、Timer 引用）。
- 本进程侧滚动更新进度（所属 Pool 的 Draining 推进）。
- 维护证据缓冲（断开清单、未提交事务清单、时间线）。

## 输入、输出与稳定接口

- **输入**：已验证命令（拉自 control-plane-adapter）、world-slot 的带 epoch 进度 ack、session 的 drain 进度、persistence commit ack、Audit durable ack、host-runtime 投递的 deadline 到期命令。
- **输出**：对 world-slot/session/transport/release-agent 的类型化命令、进度回写（送 control-plane-adapter）、维护 Audit 事件与 Failure Bundle 素材、`ReadyToExit` 请求（送 process）。
- **稳定接口**：`execute(verifiedCommand) -> MaintenanceRef | StableError`；`progress(maintenanceId) -> Progress`（幂等重放的应答来源）；`rollback(poolScope, evidence) -> Ok | StableError`。

## 上游与下游依赖

- **上游**：[process](../process/README.md)（关闭流程复用 Graceful 骨架）。
- **下游**：[control-plane-adapter](../control-plane-adapter/README.md)（命令拉取与进度回写）、[world-slot](../world-slot/README.md)（Quiesce 命令）、[session](../session/README.md)（踢出命令、drain 查询）、[transport](../transport/README.md)（广播）、[release-agent](../release-agent/README.md)（Pool 状态推进）、[persistence-host](../persistence-host/README.md)（commit ack 等待）、[host-runtime](../host-runtime/README.md)（deadline Timer）、[observability](../observability/README.md)（Audit 与 durable ack）。

## 生命周期与状态机

维护命令进度状态机（本仓细化设计；两种模式共享骨架，`Forced` 跳过 Draining 等待）：

```text
Received -> SemanticsValidated
 -> QuiesceIssued（聚合根开始原子序列）
 -> AdmissionClosed（ack）
 -> Draining（仅 Graceful：广播原因/剩余宽限，等待排空或 deadline）
 -> Persisting（等待 persistence commit ack 与 Audit durable ack，双 ack 独立完成）
 -> Kicking（MaintenanceKick 广播与全量踢出）
 -> ReadyToExit（无存留连接，退出证据已报告）-> Completed（进程按分类退出码退出）
任一阶段失败 -> Failed（证据落 Failure Bundle，Pool 转 Faulted 或 Rollback）
```

- 单调 deadline 到达即从 `Draining` 强制进入 `Kicking`（Graceful 默认宽限窗口属 SRV-D-010）。
- 进度机状态只描述"命令走到哪"，对应的生命周期事实由聚合根 ack 证明；两者不重复建模。
- 滚动更新沿用公共 Pool 状态机（见 [release-agent](../release-agent/README.md)），本模块只推进本进程所属 Pool 的退役侧。

## 线程、队列与并发所有权

- 无自有热路径线程；编排在低频控制上下文串行执行（命令收件箱，SRV-D-015 约定）。
- 同一 Pool 的并发命令由 control-plane-adapter 的幂等队列与本模块的活动命令注册表双重排他；重复命令返回当前进度。
- 不拥有消息队列；广播与断开经 transport 的既有队列执行。

## 正常数据流与失败路径

- **正常（Graceful）**：命令拉取 → 语义校验 → deadline 单调换算 + Timer 注册 → Quiesce 命令 → ack 流（AdmissionClosed → Drained/deadline）→ 双 ack 落盘 → Kicking → 无存留连接 → `ReadyToExit` → 退出。
- **失败路径**：
  - 语义非法（Forced 带非零宽限、同 Pool 已有活动命令）：稳定拒绝，写 Audit。
  - 落盘失败（磁盘满等）：`Persisting` 失败 → Failure Bundle → 按模式决策（Graceful 可中止并回滚 Pool 状态；Forced 继续踢人但证据不完整显式标注）。
  - 双 ack 只到其一：不得进入 `Kicking` 后续的完成态；超时按失败处置——persistence ack 不蕴含 Audit ack，反之亦然。
  - 踢人后发现残留连接：不得报告 `ReadyToExit`，重新执行 Kicking 并升级告警——"无连接留在旧实例"是硬性完成条件。
  - 维护中崩溃：恢复后由 control-plane-adapter 的幂等重放返回进度，从 WAL 证据续推；未提交命令视为未生效。

## 错误分类、恢复与降级

- **可重试**：广播/断开的瞬时传输失败（幂等重发，`MaintenanceKick` 语义不变）。
- **可拒绝**：语义非法命令、同 Pool 并发命令。
- **可致命**：维护中进程崩溃——恢复后从最近有效 Checkpoint/WAL 继续，全部被踢、断开与 `Indeterminate` Session 记录在案（架构源 ADR-012 失败语义）。
- **降级**：`Graceful` 超时收敛为踢人是契约行为而非降级；不存在"跳过落盘"的 Graceful 变体。

## 配置、Capability 与安全约束

- 命令的签名、fencing 与幂等由 control-plane-adapter 把守；本模块信任其产出的 `VerifiedCommand`。
- 默认模式政策：计划性工作 `Graceful`、紧急/安全事件 `Forced`（D-003，政策记入部署配置而非代码）。
- 目标 Pool 之外的产品/Release 不受影响是硬约束。

## 日志、Metrics、Trace 与 Audit

- 每个阶段迁移写 Audit（correlation `scope` 为 `Release`，关联 `maintenanceId` + 作用域三元组 + `operator`）。
- Metrics：drain 时长、被踢连接数、双 ack 完成时间对 deadline 的余量、Rollback 次数。
- 维护窗口内的全部断开与失败进入 Failure Bundle（含 `replayCommand` 可重放引用）。

## 测试面、故障矩阵与性能指标

- **测试面**：Graceful deadline 踢余量、Forced 全员踢出、Forced 带宽限拒绝、双 ack 独立等待（单 ack 超时不得完成）、并发 Pool 隔离、幂等重放（进行中/已完成）、恢复后续推、残留连接阻止 `ReadyToExit`（架构源 ADR-012 验证清单）。
- **故障矩阵**：落盘失败分支、维护中崩溃恢复、残留连接检测、Tick 暂停状态下 deadline 照常收敛。
- **性能指标**：维护完成时间分布、踢人广播的收敛时长、Draining 期间存量 Session 衰减曲线。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-012-release-update-maintenance.md`（控制面所有权、单调 deadline、fencing、幂等）、`docs/adr/ADR-011-observability.md`（双 durable ack）。
- 架构源 `schemas/maintenance-command.schema.json`（`graceDeadlineSeconds`、`fencingToken`）：正例 `fixtures/valid/maintenance-graceful.json`、`fixtures/valid/maintenance-forced.json`；反例 `fixtures/invalid/maintenance-missing-scope.json`、`fixtures/invalid/maintenance-forced-with-grace.json`。
- `MaintenanceKick` 广播码在 `schemas/maintenance-command.schema.json`（`broadcastCode`）与 `schemas/replication-envelope.schema.json`（`messageType` 枚举）中均为公共契约。

## 尚未批准的决策门

- **D-002**（滚动更新 drain 深度）：临时默认值为 Service-level drain——存量 Session 自然结束或 deadline 踢出；在线 Session 迁移需新 ADR 与协议 epoch。
- **D-003**（维护默认模式）：临时默认值为计划性工作 `Graceful`、紧急/安全事件 `Forced`。
- **SRV-D-010**（Graceful 默认宽限窗口）：临时默认值为 900 秒（`graceDeadlineSeconds`）；运维手册评审后确认。均登记于 [modules/README.md](../README.md) §11。
