---
status: pending
---
# 实现维护编排与两个独立 Durable Ack

## 涉及范围

- **Wave：** 11
- **归属：** `maintenance-agent`
- **唯一目标：** 实现纯reducer和effect dispatcher：close gate→drain→quiesce→persist/audit→kick/escalate→ReadyToExit。
- **文件集：
  - `modules/maintenance-agent/src/orchestrator.rs`
  - `modules/maintenance-agent/tests/graceful_flow_test.rs`
  - `modules/maintenance-agent/tests/forced_flow_test.rs`
  - `modules/maintenance-agent/tests/dual_ack_test.rs`

## 验收标准

- [ ] 每个effect只沿modules/README已有命令边发出并带run/ack/slot epoch。
- [ ] Graceful deadline到期进入明确Forced effect，不停止等待必要durable终态。
- [ ] PersistenceCommitAck与AuditDurableAck可任意顺序/重复；仅两者都满足才ReadyToExit。
- [ ] 任一required ack永久失败产生Failed+FailureBundle request，不伪造成功。
- [ ] ReadyToExit event不可丢且不包含目标实例激活。

## 依赖

- [`implement-maintenance-command-state-deadline-and-idempotency`](./implement-maintenance-command-state-deadline-and-idempotency.md)
- [`implement-session-drain-kick-and-fault-isolation`](./implement-session-drain-kick-and-fault-isolation.md)
- [`implement-persistence-durability-fault-matrix`](./implement-persistence-durability-fault-matrix.md)
- [`implement-observability-failure-bundle-and-emergency-path`](./implement-observability-failure-bundle-and-emergency-path.md)
- [`implement-release-local-member-state-health-and-reporting`](./implement-release-local-member-state-health-and-reporting.md)

## 接口

Consumes:
- world/session/release/transport/persistence/audit typed ack/events

Produces:
- MaintenanceOrchestrator、ReadyToExit evidence
