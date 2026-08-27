# observability 模块

> 异步日志 Sink、Audit 管道、Metrics/Trace、Failure Bundle 组装、Error/Fatal 应急同步落盘与脱敏。

## 模块定位与目标

`observability` 把全仓的诊断、审计、指标、追踪和失败证据统一到架构源的 Lumio Event Schema 上：Simulation 线程永不等待慢 Sink，Diagnostic 可以按策略丢，Audit 不允许静默丢，Failure Bundle 必须可下载、可校验、可重放。Rust 与 C# 各用成熟日志框架，经 Adapter 汇入同一 Event Schema——不自研底层日志库，不把供应商 SDK 写进稳定契约（架构源 §12.1）。

## 负责什么

- Lumio Event Schema 的宿主侧 Adapter：把 Rust 日志生态与 CoreCLR 侧 Managed 日志统一映射到架构源 `schemas/logging-event.schema.json`（`category` 七类 × `severity` 六级 × `durability` 三档）。
- Diagnostic 管道：有界异步队列 + 专用 Sink 线程批量写入；满载按级别、类别和采样策略丢弃并计数。
- Audit 管道：独立持久队列；满载时**不静默丢失**，而是向编排层暴露背压状态（由 [session](../session/README.md)/[maintenance](../maintenance/README.md) 据此停止新接入或进入维护——本模块只暴露状态，不回调上层）。
- Metrics 与 Trace：可采样，但保留聚合指标；共享 correlation 字段。
- Failure Bundle 组装：按架构源 `schemas/failure-bundle.schema.json` 收集 `reasonCode`、correlation、`manifestHash`、`snapshotId` 与 artifact 哈希清单，保证可校验、可重放。
- Error/Fatal 同步应急落盘：绕过异步队列直接写，保证进程崩溃前最后证据不丢。
- 脱敏（redaction）：敏感字段在**入队前**脱敏；日志文件轮转、保留与权限策略框架。

## 明确不负责什么

- 不存储 TxnJournal、CommandLog、Snapshot、WAL（恢复输入归 [persistence-host](../persistence-host/README.md)）；日志不能替代 Txn Journal 或 Command Log（架构源 §12.2）。
- 不定义 Event Schema、类别语义或 correlation 字段名（归架构源）。
- 不决定"队列满时踢谁、何时进维护"（编排决策归 [session](../session/README.md) 与 [maintenance](../maintenance/README.md)）。
- 不承诺跨线程实时全局顺序；重建契约是每 Producer 的 `eventSeq` + Tick 关联。

## 拥有的状态与资源

- Diagnostic 有界异步队列（容量属 SRV-D-008）与 Sink 线程。
- Audit 独立持久队列与其背压状态位。
- Metrics 聚合器、Trace 采样器。
- Failure Bundle 装配缓冲与输出目录句柄。
- 日志文件句柄、轮转与保留状态。

## 输入、输出与稳定接口

- **输入**：全部模块发出的事件（含 CoreCLR 侧经 Adapter 转入的 Managed 事件）、Failure Bundle 装配请求（来自 [process](../process/README.md) 与各故障路径）。
- **输出**：文件与控制台 Sink 输出（外部 Sink 属决策门 D-008）、Failure Bundle 产物、Audit 背压状态、聚合 Metrics。
- **稳定接口**：`emit(event)` 非阻塞入队；`emit_sync(event)` 仅限 Error/Fatal 应急；`audit_backpressure()` 只读状态查询；`assemble_failure_bundle(request) -> FailureBundleRef`。

## 上游与下游依赖

- **上游**：全部模块（全员只读依赖的"读"方向是事件流入本模块）。
- **下游**：无仓内下游——本模块不得回调任何上层模块；持久介质经文件系统直接写入（Audit 的落盘由本模块自持，不经 persistence-host，因为二者 durability 语义不同，见 [modules/README.md](../README.md) §10.1 第 4 条）。

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
  - Diagnostic 队列满：按级别/类别/采样丢弃，丢弃量计入 Metrics；缺 correlation 的 Audit 事件在入队前拒绝（对应架构源反例 Fixture `fixtures/invalid/logging-audit-missing-correlation.json`）。
  - Audit 队列满：置背压状态位；编排层停止新接入或进维护；本模块不丢事件。
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

- **测试面**：多线程乱序下按 `producerId + eventSeq` 重建、队列满采样、Audit 背压联动、应急落盘、Failure Bundle 装配与重放、脱敏覆盖。
- **故障矩阵**：queue-full、sink-failure、磁盘满、崩溃时的证据完整性、缺 correlation 拒绝（架构源 ADR-011 验证清单）。
- **性能指标**：日志吞吐（条/秒）、入队 p99 延迟（必须不影响 Tick 预算）、日志背压 Soak（多小时运行不丢 Audit）。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-011-observability.md`。
- 架构源 `schemas/logging-event.schema.json`：正例 `fixtures/valid/logging-audit.json`，反例 `fixtures/invalid/logging-audit-missing-correlation.json`。
- 架构源 `schemas/failure-bundle.schema.json`：正例 `fixtures/valid/failure-bundle.json`，反例 `fixtures/invalid/failure-bundle-bad-hash.json`。

## 尚未批准的决策门

- **D-008**（外部日志 Sink 与保留/PII 政策）：临时默认值为文件 + 控制台 Adapter 先行，外部 Sink 与保留策略属部署选择；Event Schema 保持稳定，Sink 契约单独版本化。
- **SRV-D-008**（Diagnostic 队列容量与采样策略）：临时默认值为每 Producer 8192 条有界队列、满载按级别丢弃并计数；日志 Soak 测试后确认。登记见 [modules/README.md](../README.md) §11。
