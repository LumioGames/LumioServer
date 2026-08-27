---
status: pending
---
# 实现独立 Audit Durable Pipeline 与 Ack

## 涉及范围

- **Wave：** 4
- **归属：** `observability`
- **唯一目标：** 建立与 diagnostic 完全分离的有界 audit writer、durability policy、序列和显式 durable ack。
- **文件集：
  - `modules/observability/src/audit.rs`
  - `modules/observability/src/sinks/local_audit.rs`
  - `modules/observability/src/commands.rs`
  - `modules/observability/src/events.rs`
  - `modules/observability/tests/audit_durability_test.rs`

## 验收标准

- [ ] Audit queue满载返回 `AuditUnavailable`，不丢不降级成普通日志。
- [ ] `AuditDurableAck`只在配置的flush/fsync证据成立后发出，sequence严格单调。
- [ ] 重复request id幂等返回同一终态；写失败有typed failure event。
- [ ] API与PersistenceCommitAck类型互不转换。
- [ ] crash recovery测试能区分已ack/未ack尾部。

## 依赖

- [`implement-observability-diagnostic-metrics-trace-pipeline`](./implement-observability-diagnostic-metrics-trace-pipeline.md)
- [`implement-host-runtime-clock-and-timer-delivery`](./implement-host-runtime-clock-and-timer-delivery.md)

## 接口

Consumes:
- AuditRecord、DurabilityPolicy

Produces:
- `AuditWriterPort`、`AuditDurableAck`、`AuditAvailability`
