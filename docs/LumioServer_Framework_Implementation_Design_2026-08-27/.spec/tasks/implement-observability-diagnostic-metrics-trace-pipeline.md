---
status: pending
---
# 实现脱敏诊断、Metrics 与 Trace Pipeline

## 涉及范围

- **Wave：** 3
- **归属：** `observability`
- **唯一目标：** 使用 tracing/metrics 成熟生态建立入队前脱敏、总预算有界的 diagnostic pipeline 和供应商隔离 facade。
- **文件集：
  - `modules/observability/Cargo.toml`
  - `modules/observability/src/lib.rs`
  - `modules/observability/src/event.rs`
  - `modules/observability/src/redaction.rs`
  - `modules/observability/src/diagnostic.rs`
  - `modules/observability/src/metrics.rs`
  - `modules/observability/src/trace.rs`
  - `modules/observability/src/sinks/mod.rs`
  - `modules/observability/src/sinks/console.rs`
  - `modules/observability/src/sinks/rolling_file.rs`
  - `modules/observability/src/error.rs`
  - `modules/observability/tests/redaction_test.rs`
  - `modules/observability/tests/diagnostic_saturation_test.rs`

## 验收标准

- [ ] LoggingEvent只来自generated contract；字段拼写fixture通过。
- [ ] redaction在queue send前执行；secret corpus从Debug/log/sink均不可见。
- [ ] 总容量而非无限per-producer；低级drop/采样与Error emergency行为可测。
- [ ] 公开 API无 tracing/metrics/vendor类型；label key有白名单。
- [ ] 满载压力下RSS有界，drop/reject/depth指标齐全。

## 依赖

- [`implement-host-runtime-bounded-ports`](./implement-host-runtime-bounded-ports.md)
- [`consume-upstream-generated-contract-artifacts`](./consume-upstream-generated-contract-artifacts.md)

## 接口

Consumes:
- generated LoggingEvent、PortSpec

Produces:
- `DiagnosticEmitter`、`MetricRecorder`、`TraceEmitter`
