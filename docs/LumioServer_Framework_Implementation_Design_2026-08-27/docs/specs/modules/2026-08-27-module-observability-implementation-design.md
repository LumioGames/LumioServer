# LumioServer `observability` 模块实现级设架

> 架构基线：`LGE-V1.2-2026-08-27`  
> 实现优先级：**P1**  
> crate：`lumio-observability`  
> 本文只定义仓内实现结构；公共 Schema / ABI / ID / 状态机仍由 `LumioGameEngineArchitecture` 维护。

## A. 一句话职责与非职责

**职责：** 提供公共 LoggingEvent 的脱敏/有界异步输送、Metrics/Trace adapter、Audit durable writer 与显式 ack、Failure Bundle 证据汇集和 crash-safe emergency path。

**明确不负责：**
- 不充当控制总线、durable gameplay journal、WAL/TxnJournal/CommandLog 或共享状态仓库。
- 不把 `tracing`/metrics exporter/vendor 类型写入稳定模块端口。
- 不在脱敏前入队，不记录 secret/credential/key，不让低价值日志挤占 Audit。
- 不通过任意运行时 provider 回调注册；Failure Bundle sources 在 composition 时静态列举为 typed ports。

## B. crate、目录与文件清单

建议 package 名：`lumio-observability`。文件按所有权/职责切分，不创建 `common.rs`、`globals.rs`、`event_bus.rs` 或大锅文件。

| 文件 | 唯一职责 |
| --- | --- |
| `modules/observability/Cargo.toml` | tracing/metrics/hdrhistogram/tracing-appender + storage primitives。 |
| `modules/observability/src/lib.rs` | 导出 supplier-neutral emit/audit/evidence ports。 |
| `modules/observability/src/event.rs` | generated LoggingEvent wrapper 与 construction validation。 |
| `modules/observability/src/redaction.rs` | allowlist/secret classification；入队前执行。 |
| `modules/observability/src/diagnostic.rs` | diagnostic queue、sampling/drop policy。 |
| `modules/observability/src/audit.rs` | Audit record、writer、durable ack。 |
| `modules/observability/src/metrics.rs` | 仓内 metric facade→`metrics` adapter。 |
| `modules/observability/src/trace.rs` | trace/span facade→`tracing` adapter。 |
| `modules/observability/src/evidence.rs` | typed request/fragment contracts。 |
| `modules/observability/src/bundle.rs` | generated FailureBundle assembly/hash/partial metadata。 |
| `modules/observability/src/emergency.rs` | crash-safe append/console fallback。 |
| `modules/observability/src/sinks/mod.rs` | DiagnosticSink/AuditSink SPI。 |
| `modules/observability/src/sinks/console.rs` | 开发 console adapter。 |
| `modules/observability/src/sinks/rolling_file.rs` | tracing-appender rolling file adapter。 |
| `modules/observability/src/sinks/local_audit.rs` | local durable audit adapter。 |
| `modules/observability/src/commands.rs` | flush/rotate/bundle/stop。 |
| `modules/observability/src/events.rs` | AuditAck/SinkHealth/BundleCompleted。 |
| `modules/observability/src/error.rs` | validation/redaction/queue/sink/durability/bundle errors。 |
| `modules/observability/tests/redaction_test.rs` | secret corpus/allowlist。 |
| `modules/observability/tests/diagnostic_saturation_test.rs` | bounded/drop/emergency。 |
| `modules/observability/tests/audit_durability_test.rs` | ack 与 fsync policy。 |
| `modules/observability/tests/failure_bundle_test.rs` | partial/missing/hash fixture。 |

## C. 公开/内部类型、Trait、命令、事件与端口

### C.1 类型、命令与事件
- `DiagnosticEmitter`、`AuditWriterPort`、`AuditDurableAck`。
- `MetricRecorder`、`TraceEmitter` 为仓内 facade；方法只接 primitive/ID。
- `FailureBundleRequest`、`EvidenceSourceId`、`EvidenceRequest`、`EvidenceFragment`、`BundleCompletion`。
- `ObservabilityHealth`、`AuditAvailability`。

### C.2 稳定仓内端口
| 接口 | 落点 | 契约 |
| --- | --- | --- |
| `DiagnosticEmitter::try_emit(LoggingEvent)` | `diagnostic.rs` | 入队前 validate+redact；返回 emitted/dropped/backpressured。 |
| `AuditWriterPort::try_append(AuditRecord)` | `audit.rs` | 返回 request id；durable ack 通过 event port。 |
| `AuditEventPort::try_recv()` | `events.rs` | maintenance-agent/world-slot 消费 durable ack/failure；其他 producer 只接收同步 enqueue outcome。 |
| `FailureBundlePort::request(FailureBundleRequest)` | `bundle.rs` | process/world-slot 发起。 |
| `EvidenceRequestPort/EvidenceFragmentPort` | `evidence.rs` | 固定 source 列表的 typed queues；不存 closure。 |
| `MetricRecorder::{counter,gauge,histogram}` | `metrics.rs` | 标签键白名单；供应商 recorder 隐藏。 |

### C.3 Rust 签名草案

以下签名是**仓内实现接口**，不是新增公共 ABI；实现时只能引用上游 generated 类型，不能复制公共字段。

```rust
impl DiagnosticEmitter {
    pub fn try_emit(
        &self,
        event: GeneratedLoggingEvent,
    ) -> Result<DiagnosticEmitOutcome, DiagnosticError>;
}

impl AuditWriterPort {
    pub fn try_append(&self, record: AuditRecord) -> Result<AuditRequestId, AuditError>;
}

impl FailureBundlePort {
    pub fn request(
        &self,
        request: FailureBundleRequest,
    ) -> Result<FailureBundleRequestId, FailureBundleError>;
}

impl MetricRecorder {
    pub fn counter(&self, key: MetricKey, value: u64, labels: MetricLabels) -> Result<(), MetricError>;
    pub fn gauge(&self, key: MetricKey, value: f64, labels: MetricLabels) -> Result<(), MetricError>;
    pub fn histogram(&self, key: MetricKey, value: f64, labels: MetricLabels) -> Result<(), MetricError>;
}
```

## D. 状态、资源与生命周期所有权

- Diagnostic queue/sink workers、采样/丢弃计数、redaction policy snapshot。
- Audit durable queue/writer、audit sequence、`AuditDurableAck` 与不可丢失策略。
- Failure Bundle request/fragment/assembly 状态、partial/missing-provider evidence。
- Metrics recorder/trace exporter adapter lifecycle、emergency append-only fallback。

### D.1 模块红线
- 禁止用 observability event 代替控制命令或 durable journal。
- 任何数据在 redaction 前不得进入异步队列。

## E. 线程、队列、背压与关闭

### E.1 线程/执行上下文
- diagnostic sink、audit writer、bundle assembler 均经 host-runtime 受监督创建；队列互相独立。
- 生产模块只 `try_emit`，热路径不得阻塞 sink。
- Audit full/durable failure 必须显式反馈，不可降级成普通日志。
- crash hook 只写预分配/最小字段 emergency record，不做 heap-heavy formatting。

### E.2 队列合同
| 队列 | 元素 | 所有者 | 生产者 | 消费者 | 顺序 | 容量配置 | 满载动作 | 关闭语义 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `DiagnosticQueue` | generated `LoggingEvent`/normalized diagnostic | observability | all modules | diagnostic sink worker | per-producer FIFO; global merge no total order guarantee | `observability.diagnostic.capacity.total` | 按 severity/category 丢弃低级；Error/Fatal走 emergency path | close 时 bounded drain，记录 dropped count |
| `AuditDurableQueue` | `AuditRecord` | observability | auth/control/maintenance/process | audit writer | strict audit sequence | `observability.audit.capacity` | 拒绝 producer command/发 `AuditUnavailable`，绝不丢 | flush+fsync policy完成后 ack；失败终态 |
| `FailureBundleRequestQueue` | `FailureBundleRequest` | observability | process/world-slot | bundle assembler | FIFO | `observability.bundle.request.capacity` | 合并同 correlation request；critical满载 emergency summary | 终止前尽力完成或写 partial |
| `EvidenceFragmentQueue` | `EvidenceFragment` | observability | fixed typed providers | bundle assembler | per-provider request FIFO | `observability.bundle.fragment.capacity` | 记录 missing/late provider；不无限等待 | deadline 后封 bundle，迟到 fragment 丢并计数 |

这些行必须写入仓库 Queue Contract Registry；数值来自配置快照，SRV-D 默认值不得提升为公共常量。

## F. 失败分类、恢复与显式 Ack

| 分类 | 实现要求 |
| --- | --- |
| 可拒绝 | LoggingEvent Schema 不合法、redaction policy 拒绝、未知 metric label；返回调用者。 |
| 可降级 | 低级 diagnostic queue full/sink unavailable；drop/sample并记录，不影响权威路径。 |
| 必须升级 | Audit queue/durable sink失败；发 `AuditUnavailable`，相关控制/认证操作不得假装完成。 |
| Bundle partial | provider timeout允许封 partial bundle，但必须列 missing source/原因/hash，不伪造完整。 |

## G. 编译依赖、成熟方案与 Adapter 隔离

### G.1 编译依赖
**允许：**
- host-runtime
- host-profiles
- generated LoggingEvent/FailureBundle/IDs
- `tracing`、`tracing-subscriber`、`tracing-appender`、`metrics`、`hdrhistogram`、storage primitives

**禁止：**
- 业务模块反向依赖
- persistence-host journal writer
- OpenTelemetry/vendor SDK type in public API
- unbounded subscriber/event bus

### G.2 成熟方案选择
| 方案 | 用途 | 成熟性、许可证与隔离 |
| --- | --- | --- |
| `tracing 0.1` + `tracing-subscriber 0.3` | structured trace/log | Rust 事实标准、MIT；只在 sink adapter，模块 emit 公共 LoggingEvent。 |
| `tracing-appender 0.2` | 非阻塞 rolling writer | 成熟、MIT；外层仍施加总容量与 shutdown guard。 |
| `metrics 0.24` | metrics facade | 成熟、MIT；vendor exporter后接，不自研 metrics 协议。 |
| `hdrhistogram 7.6` | 延迟分布 | 成熟、Apache-2.0；用于固定 workload 指标。 |
| `serde_json`/generated validator | bundle/event fixtures | 仅 adapter/测试，Schema 来源上游。 |

### G.3 明确拒绝的自研项
- 不自研日志内核、metrics 协议、tracing runtime、通用 event bus、云 exporter。
- 自有 redaction/queue policy 和 dual-class pipeline 是必要 glue，因为 Audit durability 与 diagnostic 丢弃语义不可合并。

## H. 测试面与 Fixture

- Golden：logging-event、failure-bundle 正反 fixtures/camelCase。
- 安全：secret corpus、credential-like values、panic text 脱敏。
- 容量：每 producer/总队列上限，RSS bounded，低级 drop不阻塞。
- Audit：durable ack 不早于 policy、queue full拒绝 producer、crash recovery sequence。
- Bundle：provider absent/late/panic，partial合法且 hash/manifest一致。

## I. 决策门与配置默认

- D-008 决定外部 sink；当前 console/rolling file 是 adapter默认，不冻结供应商。
- SRV-D-008 diagnostic 容量必须是总预算，而非无限 per-producer 乘积。
- Audit durability policy需配置和测试；其 ack与 persistence 完全独立。

## J. 本模块任务卡

| 任务 | Wave | 单一目标 | 依赖 |
| --- | --- | --- | --- |
| [`implement-observability-diagnostic-metrics-trace-pipeline`](../../../.spec/tasks/implement-observability-diagnostic-metrics-trace-pipeline.md) | Wave 3 | 使用 tracing/metrics 成熟生态建立入队前脱敏、总预算有界的 diagnostic pipeline 和供应商隔离 facade。 | `implement-host-runtime-bounded-ports`, `consume-upstream-generated-contract-artifacts` |
| [`implement-observability-audit-durable-pipeline`](../../../.spec/tasks/implement-observability-audit-durable-pipeline.md) | Wave 4 | 建立与 diagnostic 完全分离的有界 audit writer、durability policy、序列和显式 durable ack。 | `implement-observability-diagnostic-metrics-trace-pipeline`, `implement-host-runtime-clock-and-timer-delivery` |
| [`implement-observability-failure-bundle-and-emergency-path`](../../../.spec/tasks/implement-observability-failure-bundle-and-emergency-path.md) | Wave 5 | 以固定 typed evidence ports 汇集 generated FailureBundle，支持partial/missing provider并提供最小崩溃写入路径。 | `implement-observability-audit-durable-pipeline`, `implement-host-runtime-supervision-cancellation-and-join` |

## K. 完成定义

本模块的任务卡全部通过；模块单测/fixture/故障测试通过；Queue Registry、Cargo DAG、许可证与源码红线检查通过；没有供应商类型泄漏、无界队列、任意回调或未见证 FaultClass 推断。
