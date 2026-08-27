# persistence-host 模块

> Snapshot/WAL/TxnJournal/CommandLog 的宿主侧落盘编排、Checkpoint 调度、崩溃恢复编排与存储 Adapter。

## 模块定位与目标

`persistence-host` 拥有"恢复所需的一切字节如何安全落盘、如何被找回"的宿主侧编排。Canonical 字节格式与 `Encode/Decode` 契约归 Runtime（架构源 ADR-010），域 payload Schema 归 Voxel/Game；本模块负责的是 durability：staging、校验、fsync、原子替换、保留策略、恢复定位与只重放已提交记录。失败的写入永远不能污染最近有效 Checkpoint。

## 负责什么

- Snapshot 落盘编排：接收 Runtime 在 SnapshotCut 产出的 Canonical 字节，按 `临时文件 -> 校验 -> fsync -> 原子替换` 写入；Header 遵循架构源 `schemas/snapshot-header.schema.json`（`magic` 为 `LUMIOSNP1`，`activationState` 为 `Staged/Active/Invalid`）。
- WAL/Command Log 持久化：权威确认前按部署策略保证可恢复（决策门 D-005）；只有带提交标记的记录在恢复时重放。
- TxnJournal 持久化：`CrossWorldTxnV1` 的 `CommitIntent`、参与者标记与 `Committed` 标记的落盘顺序保证（写第一个参与者前先持久化 `CommitIntent`，架构源 §6.2）；协调语义归 Runtime Coordinator，本模块保证日志顺序与持久性。
- Checkpoint 调度：周期与保留数量（SRV-D-009）；保留最近有效 Checkpoint 与失败 Bundle，升级不覆盖旧 Release/Snapshot。
- 恢复编排：定位最近有效 Checkpoint（校验 Magic/SchemaVersion/Hash/Checksum）→ 重放已提交 WAL/CommandLog → 向 Runtime 提交恢复输入 → `Indeterminate` 事务按 Journal 标记与状态查询解决。
- 存储 Adapter：本地文件/目录是第一阶段权威存储；对象存储/数据库经 Adapter 预留，后端更换不改变 Canonical 字节（架构源 ADR-010）。
- Migration 执行环境编排：从不可变 `snapshotId + SessionRevisionVector` 读取、Staging 目录执行、原子版本指针激活、失败保留旧数据与可重跑证据（DAG 与业务语义归 Game/Runtime，架构源 §13.4）。
- 磁盘压力处置：磁盘满/IO 错误时 durable 队列拒绝新命令或触发进维护（联动 [maintenance](../maintenance/README.md) 经编排层）。

## 明确不负责什么

- 不定义 Canonical Serializer、Snapshot/WAL 字节格式或压缩规则（归 Runtime）；不定义域 payload Schema（归 Voxel/Game）。
- 不拥有 Cross-World 事务协调（归 Runtime Coordinator）；只保证 Journal 的持久与顺序。
- 不存储 Diagnostic/Audit/Metrics/Trace（归 [observability](../observability/README.md)；分工依据见 [modules/README.md](../README.md) §10.1 第 4 条）。
- 不决定何时做 SnapshotCut（Tick Barrier 语义归 Runtime；维护触发归 [maintenance](../maintenance/README.md)）。
- 不做备份站点/多副本策略（部署层能力，经 Adapter 预留）。

## 拥有的状态与资源

- Snapshot 目录布局、Active/Staged 版本指针、保留集。
- WAL/CommandLog/TxnJournal 的写入句柄、持久队列与提交水位。
- Checkpoint 调度器状态（上次 Checkpoint 的 `tickId`/时间）。
- 存储 Adapter 实例与磁盘配额水位。

## 输入、输出与稳定接口

- **输入**：Runtime 产出的 Canonical Snapshot 字节与 WAL/Journal 记录（经 Simulation Owner Thread 提交到持久队列）、恢复请求（来自 [process](../process/README.md) 启动流程）、维护落盘请求（来自 [maintenance](../maintenance/README.md) 编排）。
- **输出**：落盘完成回执（含 `snapshotId` 与 Hash）、恢复输入流（交 Runtime materialize）、磁盘压力状态、Snapshot 元数据（本仓只保存元数据与句柄，不解析内部状态）。
- **稳定接口**：`persist_snapshot(cut) -> SnapshotRef | StableError`；`append_wal(record) -> Ack`；`append_txn_journal(marker) -> Ack`（顺序保证）；`recover() -> RecoveryPlan`；`disk_pressure() -> PressureState`。

## 上游与下游依赖

- **上游**：[world-slot](../world-slot/README.md)（Snapshot/WAL 提交路径）、[maintenance](../maintenance/README.md)（维护落盘与 Migration 编排）、[process](../process/README.md)（恢复启动）。
- **下游**：仅 [observability](../observability/README.md)（事件、Metrics、Failure Bundle 素材）。

## 生命周期与状态机

- 写入路径状态：`Idle -> Staging -> Verifying -> Fsync -> AtomicSwap -> Active`；任一失败 → `Invalid`（保留证据，旧 Active 不受影响）。
- 恢复路径状态：`Locating -> Validating -> Replaying -> Handover`；无有效 Checkpoint 时按部署策略进入全新初始化或拒绝启动。
- 模块随 [process](../process/README.md) 启动序列初始化，析构前必须完成全部持久队列的 Flush。

## 线程、队列与并发所有权

- 拥有 IO/Persistence Worker 线程（数量为部署配置）。
- 持久队列（WAL/CommandLog/TxnJournal）独立于 Diagnostic 日志队列；满载时**不丢**——拒绝新命令或触发进维护（架构源 ADR-011）。
- Simulation Owner Thread 只做非阻塞入队；fsync 与原子替换在 Worker 线程执行；group-commit 与 sync 模式的取舍属 D-005 待测量。

## 正常数据流与失败路径

- **正常**：Tick Barrier 产出 → 持久队列 → Worker 落盘（staging/校验/fsync/原子替换）→ 回执 → Checkpoint 调度推进 → 旧版本按保留策略清理。
- **失败路径**：
  - 校验失败（长度/Hash 不匹配）：数据标 `Invalid` 不激活，旧 Snapshot 原封不动（对应架构源反例 Fixture `fixtures/invalid/snapshot-length-mismatch.json`）。
  - 崩溃发生在参与者提交之间：恢复得到 `Indeterminate`，查 Journal 标记只重放缺失的幂等步骤，绝不双重扣费（对应 `fixtures/invalid/cross-world-txn-partial-commit.json`）。
  - 磁盘满：durable 队列拒新命令/触发维护；Diagnostic 走 observability 的采样路径，二者不混。
  - Migration 失败：Staging 证据保留、源 Snapshot 不动、不激活版本指针。
  - 解压炸弹/截断/未知必需字段：Decode 阶段拒绝（拒绝逻辑归 Runtime，本模块保证拒绝结果不产生半写状态）。

## 错误分类、恢复与降级

- **可重试**：瞬时 IO 错误（有限重试后升级）。
- **可拒绝**：校验失败的输入、超预算的写入请求、磁盘配额超限时的新命令。
- **可致命**：权威存储目录不可写且无法恢复——进程级处置。
- **降级**：Singleplayer 可声明轻量落盘模式但必须声明丢失边界（架构源 §11.1）；DS 生产 Profile 不降级。

## 配置、Capability 与安全约束

- 存储路径、保留数量、fsync 策略来自不可变配置快照；可选加密元数据遵循 SnapshotHeader Schema（`encryption` 枚举）。
- 升级不覆盖旧 Release/Snapshot；所有操作可审计、可恢复、可回放（本仓 [repository-architecture.md](../../.spec/knowledge/standards/repository-architecture.md)）。
- Snapshot 内容可能含用户数据：文件权限与脱敏策略随部署声明；密钥管理不入库。

## 日志、Metrics、Trace 与 Audit

- 每次 Checkpoint/恢复/Migration 激活写 Audit（关联 `snapshotId`、`sessionId`、`tickId`）。
- Metrics：持久化延迟（入队到 fsync 完成）、WAL 追加吞吐、Checkpoint 时长、磁盘水位、恢复演练时长（对应架构源 ADR-016 的 persistence latency 指标）。
- 失败写入产出 Failure Bundle 素材（保留 Staging 证据引用）。

## 测试面、故障矩阵与性能指标

- **测试面**：Snapshot round-trip 与旧版本读取（golden）、原子激活的崩溃注入（每个写入阶段断电点）、恢复演练、保留策略、Adapter 更换不改字节。
- **故障矩阵**：磁盘满、损坏输入（fuzz/校验和不符/解压限制）、崩溃于参与者提交之间、重复 `TxnId` 幂等、Migration 失败保留现场（架构源 ADR-003/010 验证清单）。
- **性能指标**：fsync 与 group-commit 的延迟/吞吐对比（D-005 测量输入）、Checkpoint 对 Tick 尾延迟的影响、恢复时间目标。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-010-persistence-config.md`（Canonical 序列化、staging/原子激活、配置快照）、`docs/adr/ADR-003-cross-world-txn.md`（TxnJournal 顺序与恢复）、`docs/adr/ADR-011-observability.md`（durable 队列语义）。
- 架构源 `schemas/snapshot-header.schema.json`：正例 `fixtures/valid/snapshot-active.json`，反例 `fixtures/invalid/snapshot-length-mismatch.json`。
- 架构源 `schemas/cross-world-txn.schema.json`：正例 `fixtures/valid/cross-world-txn-committed.json`、`fixtures/valid/cross-world-txn-aborted.json`；反例 `fixtures/invalid/cross-world-txn-partial-commit.json`。
- 架构源 `schemas/migration-manifest.schema.json`：正例 `fixtures/valid/migration-manifest.json`，反例 `fixtures/invalid/migration-cycle.json`。

## 尚未批准的决策门

- **D-005**（Snapshot/WAL 持久与保留强度）：临时默认值为 DS 在权威确认前保证可恢复，group-commit 与 sync 模式待测量后记入架构源 ADR-010 与部署 Manifest；格式保持 Canonical 不变。
- **SRV-D-009**（Checkpoint 周期与保留数量）：临时默认值为每 5 分钟或每 6000 Tick 取先到者、保留最近 3 个有效 Checkpoint；随 D-005 测量一并确认。登记见 [modules/README.md](../README.md) §11。
