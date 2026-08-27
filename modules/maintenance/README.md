# maintenance 模块

> 滚动更新编排、Drain、Graceful/Forced 强制维护、MaintenanceKick 与 Rollback。

## 模块定位与目标

`maintenance` 拥有"让一个 Release Pool 安全地开始、停止、替换服务"的全部运维编排。维护命令永远携带 `productId + gameReleaseId + releasePoolId` 作用域，默认只影响目标 Pool——`A 1.1` 的维护不能动 `BOE 2.1` 的任何连接；集群级维护必须显式列出多个 Pool（架构源 §13.3、ADR-012）。两种模式（`Graceful`/`Forced`）都必须确保没有连接留在旧实例，全部断开、失败与恢复动作可审计、可恢复、可回放。

## 负责什么

- 维护命令接收与 scope 校验：按架构源 `schemas/maintenance-command.schema.json` 校验 `maintenanceId`、作用域三元组、`mode`、`deadlineTick`、`reason`、`operator`、`action`；缺 scope 直接拒绝（对应反例 Fixture `fixtures/invalid/maintenance-missing-scope.json`）。
- `Graceful` 流程编排（`action` 为 `DrainAndKick`）：停止新接入（经 [session](../session/README.md)）→ 广播原因与截止时间 → 排空在途事务 → Snapshot/WAL/Audit 落盘（经 [persistence-host](../persistence-host/README.md)）→ deadline 到达后对剩余连接广播 `MaintenanceKick` 并断开。
- `Forced` 流程编排（`action` 为 `StopInputAndKick`）：立即停止新输入与 Tick 提交 → 先写维护事件、尽最大努力完成当前 WAL/Failure Bundle → 广播 `MaintenanceKick` 并断开目标 Pool 全部连接；未提交命令不得假定生效，恢复时从最近有效 Snapshot + WAL 重放。
- 滚动更新编排：驱动 [release-router](../release-router/README.md) 执行 `Published -> Verified -> Warmup -> Serving` 与旧 Pool `Draining -> Empty -> Retired`；新 Pool 健康检查通过后才接新 Session；旧 Pool 服务存量直至自然排空、显式迁移或期限（D-002）。
- Rollback 编排：保留旧的已验证 Pool 与 Snapshot；升级不覆盖旧 Release/Snapshot。
- 关旧起新：确认目标 Pool 无存留连接后关闭旧实例、启动目标 Release；重连只能被路由到 Catalog 允许的目标 Release。
- 维护证据：所有用户断开、未提交事务与恢复动作写入 Audit 与 Failure Bundle。

## 明确不负责什么

- 不定义维护命令 Schema、`MaintenanceKick` 错误码或 Pool 状态枚举（归架构源）。
- 不执行传输层断开与广播的机械动作（归 [network](../network/README.md)，经 session 编排）；不直接迁移 Pool 状态数据结构（归 [release-router](../release-router/README.md) 执行）。
- 不做 Snapshot/WAL 的写盘（归 [persistence-host](../persistence-host/README.md)）；只发起并等待完成回执。
- 不决定 Tick 停止的机械动作（归 [pacing](../pacing/README.md) 的 `pause`，经编排调用）。
- 不实现在线 Session 无感跨 Release 迁移（V1 非目标；需要新协议、存档切割与回滚契约，D-002）。

## 拥有的状态与资源

- 活动维护命令注册表（`maintenanceId` → 进度状态机、作用域、deadline）。
- 滚动更新进度（新旧 Pool 配对与阶段）。
- 维护证据缓冲（断开清单、未提交事务清单、时间线）。

## 输入、输出与稳定接口

- **输入**：签名维护命令（运维通道下发）、Pool 健康视图（来自 [release-router](../release-router/README.md)）、落盘完成回执（来自 [persistence-host](../persistence-host/README.md)）、drain 进度（来自 [session](../session/README.md) 的存量 Session 计数）。
- **输出**：对 session/release-router/persistence-host/pacing 的编排指令、`MaintenanceKick` 广播请求、维护 Audit 事件与 Failure Bundle。
- **稳定接口**：`execute(command) -> MaintenanceRef | StableError`；`progress(maintenanceId) -> Progress`；`rollback(poolPair, evidence) -> Ok | StableError`。

## 上游与下游依赖

- **上游**：运维命令通道（外部，须携带 `operator` 身份并经签名校验）；[process](../process/README.md)（关闭流程复用 Graceful 骨架）。
- **下游**：[release-router](../release-router/README.md)、[session](../session/README.md)、[persistence-host](../persistence-host/README.md)、[observability](../observability/README.md)；经编排间接触达 [network](../network/README.md)（广播/断开）与 [pacing](../pacing/README.md)（停止 Tick）。

## 生命周期与状态机

维护命令执行状态机（本仓细化设计；两种模式共享骨架，`Forced` 跳过 Draining）：

```text
Received -> ScopeValidated
 -> AdmissionClosed（停止新接入）
 -> Draining（仅 Graceful：广播原因/deadline，排空事务）
 -> Persisting（Snapshot/WAL/Audit 落盘；Forced 为尽力而为）
 -> Kicking（MaintenanceKick 广播与断开）
 -> OldInstanceClosed -> TargetActivated -> Completed
任一阶段失败 -> Failed（证据落 Failure Bundle，Pool 转 Faulted 或 Rollback）
```

- `deadlineTick` 到达即从 `Draining` 强制进入 `Kicking`（SRV-D-010 提供默认 deadline）。
- 滚动更新沿用公共 Pool 状态机（见 [release-router](../release-router/README.md)），本模块只推进不改写枚举。

## 线程、队列与并发所有权

- 无自有热路径线程；编排在低频控制线程串行执行，同一 Pool 同时只允许一个活动维护命令（重复命令以稳定错误拒绝并返回原命令进度）。
- 不拥有消息队列；广播与断开经 [network](../network/README.md) 的既有队列执行。

## 正常数据流与失败路径

- **正常（Graceful）**：命令校验 → 停接入 → 广播 → 排空 → 落盘 → deadline 踢余量 → 关旧 → 起新 → `Completed`，全程 Audit。
- **正常（滚动更新）**：新 Pool `Published -> Verified -> Warmup` → 健康通过 → `Serving` 接新 → 旧 Pool `Draining` → 排空/期限 → `Retired`。
- **失败路径**：
  - 缺 scope/签名无效/操作者无权限：`Received` 阶段拒绝，写 Audit。
  - 落盘失败（磁盘满等）：`Persisting` 失败 → Failure Bundle → 按模式决策（Graceful 可中止并回滚状态；Forced 继续踢人但明确标注证据不完整）。
  - 新 Pool 健康检查不过：不接新 Session，旧 Pool 保持 Serving，走 `Rollback`。
  - 踢人后发现残留连接：不得关闭旧实例，重新执行 Kicking 并升级告警——"无连接留在旧实例"是硬性完成条件。
  - 恢复期间：只重放带 WAL 提交标记的命令，未提交命令视为未生效（根 [README.md](../../README.md) 维护章节）。

## 错误分类、恢复与降级

- **可重试**：广播/断开的瞬时传输失败（幂等重发，`MaintenanceKick` 语义不变）。
- **可拒绝**：非法命令（缺 scope、签名、越权）、目标 Pool 已有活动维护。
- **可致命**：维护中进程崩溃——恢复后从最近有效 Checkpoint/WAL 继续，全部被踢、断开与 `Indeterminate` Session 记录在案（架构源 ADR-012 失败语义）。
- **降级**：`Graceful` 超时自动收敛为踢人（这是契约行为而非降级）；不存在"跳过落盘"的 Graceful 变体。

## 配置、Capability 与安全约束

- 维护命令必须携带 `operator` 且经运维通道签名校验；命令与结果全程 Audit——这是管理面安全红线。
- 默认模式政策：计划性工作 `Graceful`、紧急/安全事件 `Forced`（D-003，政策记入部署配置而非代码）。
- 目标 Pool 之外的产品/Release 不受影响是硬约束；跨 Pool 命令必须显式列举。

## 日志、Metrics、Trace 与 Audit

- 每个阶段迁移写 Audit（关联 `maintenanceId` + 作用域三元组 + `operator`）。
- Metrics：drain 时长、被踢连接数、落盘完成时间对 deadline 的余量、Rollback 次数。
- 维护窗口内的全部断开与失败进入 Failure Bundle（含 `replayCommand` 可重放引用）。

## 测试面、故障矩阵与性能指标

- **测试面**：Graceful deadline 踢余量、Forced 全员踢出、并发 Pool 隔离（维护 A 不影响 BOE）、滚动更新全阶段、Rollback 保留旧资产、恢复后命令重放（架构源 ADR-012 验证清单）。
- **故障矩阵**：缺 scope 拒绝、落盘失败分支、健康检查不过回滚、维护中崩溃恢复、残留连接检测。
- **性能指标**：维护完成时间分布、踢人广播的收敛时长、滚动更新期间新旧 Pool 的接入切换延迟。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-012-release-update-maintenance.md`。
- 架构源 `schemas/maintenance-command.schema.json`：正例 `fixtures/valid/maintenance-graceful.json`、`fixtures/valid/maintenance-forced.json`；反例 `fixtures/invalid/maintenance-missing-scope.json`。
- `MaintenanceKick` 广播码在 `schemas/maintenance-command.schema.json`（`broadcastCode`）与 `schemas/replication-envelope.schema.json`（`messageType` 枚举）中均为公共契约。

## 尚未批准的决策门

- **D-002**（滚动更新 drain 深度）：临时默认值为 Service-level drain——存量 Session 自然结束或 deadline 踢出；在线 Session 迁移需新 ADR 与协议 epoch。
- **D-003**（维护默认模式）：临时默认值为计划性工作 `Graceful`、紧急/安全事件 `Forced`；政策记入 Server 部署配置，无 wire 变更。
- **SRV-D-010**（Graceful 默认 deadline）：临时默认值为 15 分钟；运维手册评审后确认。均登记于 [modules/README.md](../README.md) §11。
