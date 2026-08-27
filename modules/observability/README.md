# observability 模块

> 异步日志 Sink、Audit 管道、Metrics/Trace、Failure Bundle 组装、Error/Fatal 应急同步落盘与脱敏。

## 模块定位与目标

`observability` 把全仓的诊断、审计、指标、追踪和失败证据统一到架构源的 Lumio Event Schema 上：Simulation 线程永不等待慢 Sink，Diagnostic 可以按策略丢，Audit 不允许静默丢，Failure Bundle 必须可下载、可校验、可重放。本模块是 **Audit 队列、其 Sink 与 Failure Bundle 装配的唯一所有者**；WAL/TxnJournal/CommandLog 归 [persistence-host](../persistence-host/README.md)——两个所有者可共享底层 IO 原语，但不共享队列状态或 ack 通道（架构源 ADR-011，v1.1）。Rust 与 C# 各用成熟日志框架，经 Adapter 汇入同一 Event Schema——不自研底层日志库，不把供应商 SDK 写进稳定契约（架构源 §12.1）。

## 负责什么

- Lumio Event Schema 的宿主侧 Adapter：把 Rust 日志生态与 CoreCLR 侧 Managed 日志统一映射到架构源 `schemas/logging-event.schema.json`（`category` 七类 × `severity` 六级 × 必填 `durability` 三档）。
- correlation 作用域校验：每个事件必须声明 `correlation.scope`（`Process`/`Release`/`Session`/`World`/`Txn`）——基础字段（`productId`、`gameReleaseId`、`traceId`、`producerId`、`eventSeq`）恒必填，层级 ID 只在对应作用域必填且**不得伪造**（进程启动、Manifest 校验、认证拒绝等早期事件用 `Process`/`Release` 作用域）；违规事件入队前拒绝并计数（对应架构源反例 Fixture `fixtures/invalid/logging-scope-fabricated-session.json`）。
- Diagnostic 管道：有界异步队列 + 专用 Sink 线程批量写入；满载按级别、类别和采样策略丢弃并计数。
- Audit 管道：独立持久队列；写入返回**显式 durable ack**——需要落盘证据的编排步骤（如维护 Persisting）把 Audit durable ack 与 persistence commit ack 当作两个独立完成信号等待（架构源 ADR-011）。满载时**不静默丢失**，而是暴露背压状态事件（聚合根据此关闸或进维护——本模块只暴露状态，不回调上层）。
- Metrics 与 Trace：可采样，但保留聚合指标；共享 correlation 字段。
- Failure Bundle 装配（唯一所有者）：按架构源 `schemas/failure-bundle.schema.json` 收集 `reasonCode`、correlation、`manifestHash`、`snapshotId` 与 artifact 哈希清单；每个 Bundle 声明 `incidentKind`（`Simulation`/`CoreEngineLoad`/`SupplyChain`/`BuildValidation`）——`Simulation` 必须引用 SnapshotId，`CoreEngineLoad`/`SupplyChain` 必须携带 `coreEngine` 块且 correlation 作用域为 `Process`（加载期失败先于任何 Session/World，SnapshotId 不适用而非必填；架构源 §12.2，v1.2）。证据提供方（各模块）在正常运行期**持续发布不可变证据快照**；装配器只读已发布快照，装配时**不回调**故障或已销毁模块；缺席或超预算的提供方产出记录缺失项的**部分 Bundle**（SRV-D-017）。崩溃路径写崩溃安全的最小证据集，下次启动补全。
- Error/Fatal 同步应急落盘：绕过异步队列直接写；`EmergencySync` 持久级仅限 Error/Fatal 严重度（架构源 Schema 语义规则）。
- 脱敏（redaction）：敏感字段在**入队前**脱敏；日志文件轮转、保留与权限策略框架。

## 明确不负责什么

- 不存储 TxnJournal、CommandLog、Snapshot、WAL（恢复输入归 [persistence-host](../persistence-host/README.md)）；日志不能替代 Txn Journal 或 Command Log（架构源 §12.2）。
- 不定义 Event Schema、类别语义或 correlation 字段名（归架构源）。
- 不决定"队列满时踢谁、何时进维护"——关闸与处置裁决归 [world-slot](../world-slot/README.md) 聚合根（消费本模块的背压状态事件），维护进度编排归 [maintenance-agent](../maintenance-agent/README.md)；本模块只暴露状态。
- 不承诺跨线程实时全局顺序；重建契约是每 Producer 的 `eventSeq` + Tick 关联。

## 拥有的状态与资源

- Diagnostic 有界异步队列（容量属 SRV-D-008）与 Sink 线程。
- Audit 独立持久队列与其背压状态位。
- Metrics 聚合器、Trace 采样器。
- Failure Bundle 装配缓冲与输出目录句柄。
- 日志文件句柄、轮转与保留状态。

## 输入、输出与稳定接口

- **输入**：全部模块发出的事件（含 CoreCLR 侧经 Adapter 转入的 Managed 事件）、Failure Bundle 装配请求（唯一触发方是 [process](../process/README.md)；故障模块只持续发布证据快照，不发装配请求）。
- **输出**：文件与控制台 Sink 输出（外部 Sink 属决策门 D-008）、Audit durable ack、Failure Bundle 产物、Audit 背压状态事件、聚合 Metrics。
- **稳定接口**：`emit(event)` 非阻塞入队；`emit_durable(event) -> DurableAck`（Audit 类别，显式落盘回执）；`emit_sync(event)` 仅限 Error/Fatal 应急；`audit_backpressure()` 只读状态查询；`publish_evidence(provider, snapshotRef)` 证据快照发布；`assemble_failure_bundle(request) -> FailureBundleRef`。

## 上游与下游依赖

- **上游**：全部模块（全员只读依赖的"读"方向是事件流入本模块）。
- **下游**：[host-runtime](../host-runtime/README.md)（Sink 线程监督与有界通道原语）。此外无仓内下游——本模块不得回调任何上层模块；Audit 的落盘由本模块自持，不经 persistence-host：Audit 是合规证据、WAL/Journal 是恢复输入，两者队列与 ack 通道分立（架构源 ADR-011，v1.1 起为公共契约条款）。

## 生命周期与状态机

- 在 [process](../process/README.md) 启动序列中**最早**初始化、**最晚**析构（Flush 全部持久队列后才允许进程退出）。
- 队列状态：`Normal -> Saturated（Diagnostic 采样丢弃 / Audit 背压置位）-> Normal`；Sink 故障：`SinkHealthy -> SinkFailed`（产出 Failure Bundle，不伪装持久化成功）。

## 线程、队列与并发所有权

- 拥有异步 Sink 线程（数量为部署配置）；任意线程可非阻塞 `emit`。
- Simulation Owner Thread 不等待 Diagnostic Sink（架构源 §12.1）——入队是无锁或有界非阻塞操作，满载走丢弃/计数路径而不是阻塞。
- Audit 队列与 Diagnostic 队列物理隔离，容量与满载策略独立声明。

## 正常数据流与失败路径

- **正常**：`emit` → 脱敏 → 有界队列 → Sink 线程批量写文件/控制台 → 轮转/保留。
- **失败路径**：
  - Diagnostic 队列满：按级别/类别/采样丢弃，丢弃量计入 Metrics；缺 correlation、缺 `durability`、作用域伪造或 `EmergencySync` 配低严重度的事件在入队前拒绝（对应架构源反例 Fixture `fixtures/invalid/logging-audit-missing-correlation.json`、`fixtures/invalid/logging-audit-missing-durability.json`、`fixtures/invalid/logging-scope-fabricated-session.json`）。
  - Audit 队列满：置背压状态位并发状态事件；聚合根关闸或进维护；本模块不丢事件，未落盘前不发 durable ack。
  - Sink 写失败：切换应急落盘路径，产出 Failure Bundle；不向调用方谎报成功。
  - 进程崩溃：Error/Fatal 已同步落盘的证据 + crash marker 供恢复启动装配 Failure Bundle。

## 错误分类、恢复与降级

- **可重试**：Sink 暂时写失败（有限重试后转 SinkFailed）。
- **可拒绝**：格式非法事件、缺必填 correlation 的 Audit 事件——入队前拒绝并计数。
- **可致命**：应急落盘也失败（磁盘满且无备用路径）——升级为进程级故障处置。
- **降级**：Diagnostic 采样率上调、Trace 关闭采样，均为声明式策略且可在 Metrics 中观测。

## 配置、Capability 与安全约束

- 队列容量、采样策略、轮转/保留策略来自不可变配置快照。
- 脱敏在入队前执行（架构源 ADR-011）；密钥/凭据不进日志（本仓 [rules/system.md](../../.spec/rules/system.md)）。
- 外部 Sink 未获批准前只有文件 + 控制台 Adapter（决策门 D-008）。

## 日志、Metrics、Trace 与 Audit

- 本模块自身的运行指标：各队列深度/丢弃数/入队延迟、Sink 批量大小与写延迟、应急落盘次数、Failure Bundle 装配次数。
- 网络时间戳和队列状态进入 Diagnostic Hash，不进入权威 Simulation Hash（架构源 §12.2）。

## 测试面、故障矩阵与性能指标

- **测试面**：多线程乱序下按 `producerId + eventSeq` 重建、队列满采样、Audit 背压联动、durable ack 与 persistence commit ack 相互独立（单 ack 不蕴含另一 ack）、作用域校验（启动/认证拒绝事件合法、伪造 sessionId 拒绝）、应急落盘、Failure Bundle 部分装配（缺席提供方）、脱敏覆盖。
- **故障矩阵**：queue-full、sink-failure、磁盘满、崩溃时的证据完整性、缺 correlation/durability 拒绝、装配时提供方已销毁（读快照不回调）（架构源 ADR-011 验证清单）。
- **性能指标**：日志吞吐（条/秒）、入队 p99 延迟（必须不影响 Tick 预算）、日志背压 Soak（多小时运行不丢 Audit）。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-011-observability.md`（所有权分立、双 ack、correlation 作用域、provider 模型）。
- 架构源 `schemas/logging-event.schema.json`：正例 `fixtures/valid/logging-audit.json`、`fixtures/valid/logging-startup-audit.json`（Process 作用域）、`fixtures/valid/logging-auth-reject-audit.json`（Release 作用域）；反例 `fixtures/invalid/logging-audit-missing-correlation.json`、`fixtures/invalid/logging-audit-missing-durability.json`、`fixtures/invalid/logging-scope-fabricated-session.json`。
- 架构源 `schemas/failure-bundle.schema.json`：正例 `fixtures/valid/failure-bundle.json`，反例 `fixtures/invalid/failure-bundle-bad-hash.json`。

## 尚未批准的决策门

- **D-008**（外部日志 Sink 与保留/PII 政策）：临时默认值为文件 + 控制台 Adapter 先行，外部 Sink 与保留策略属部署选择；Event Schema 保持稳定，Sink 契约单独版本化。
- **SRV-D-008**（Diagnostic 队列容量与采样策略，含全进程总内存上界）、**SRV-D-014**（durable 队列容量与背压阈值，Audit 侧）、**SRV-D-017**（Failure Bundle 提供方预算与部分装配策略）：临时默认值与批准条件见 [modules/README.md](../README.md) §11.2。
