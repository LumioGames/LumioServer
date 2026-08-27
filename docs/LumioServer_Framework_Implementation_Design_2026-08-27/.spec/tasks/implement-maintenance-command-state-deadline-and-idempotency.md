---
status: pending
---
# 实现 MaintenanceCommand 状态、Deadline 与幂等

## 涉及范围

- **Wave：** 9
- **归属：** `maintenance-agent`
- **唯一目标：** 包装generated command、monotonic grace deadline、run state和duplicate/conflict行为。
- **文件集：
  - `modules/maintenance-agent/Cargo.toml`
  - `modules/maintenance-agent/src/lib.rs`
  - `modules/maintenance-agent/src/command.rs`
  - `modules/maintenance-agent/src/semantics.rs`
  - `modules/maintenance-agent/src/deadline.rs`
  - `modules/maintenance-agent/src/state.rs`
  - `modules/maintenance-agent/src/progress.rs`
  - `modules/maintenance-agent/src/acks.rs`
  - `modules/maintenance-agent/src/commands.rs`
  - `modules/maintenance-agent/src/events.rs`
  - `modules/maintenance-agent/src/evidence.rs`
  - `modules/maintenance-agent/src/error.rs`
  - `modules/maintenance-agent/tests/idempotency_test.rs`

## 验收标准

- [ ] MaintenanceCommand字段/enum/scope只来自generated contract，`graceDeadlineSeconds`拼写fixture通过。
- [ ] deadline=receipt monotonic+grace，不依赖wall-clock跳变；旧timer generation拒绝。
- [ ] duplicate same payload返回既有progress，conflicting command明确拒绝。
- [ ] state中不存在TargetActivated；终态只有ReadyToExit/Failed/Rejected。
- [ ] 只有control-plane-adapter可生产VerifiedMaintenanceCommand；process关闭不经过本模块。

## 依赖

- [`implement-control-plane-injected-channel-and-status-reporting`](./implement-control-plane-injected-channel-and-status-reporting.md)
- [`implement-host-runtime-clock-and-timer-delivery`](./implement-host-runtime-clock-and-timer-delivery.md)
- [`implement-session-reconnect-window-and-epoch-races`](./implement-session-reconnect-window-and-epoch-races.md)
- [`implement-world-slot-quiesce-migration-and-fault-adjudication`](./implement-world-slot-quiesce-migration-and-fault-adjudication.md)

## 接口

Consumes:
- VerifiedMaintenanceCommand/LocalShutdown、dependency ports

Produces:
- MaintenanceRun state/commands/events/ack slots
