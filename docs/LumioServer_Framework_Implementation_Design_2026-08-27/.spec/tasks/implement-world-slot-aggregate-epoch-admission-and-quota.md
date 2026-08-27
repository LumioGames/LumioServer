---
status: pending
---
# 实现 WorldSlotHost 聚合、Epoch、Admission Gate 与 Quota

## 涉及范围

- **Wave：** 6
- **归属：** `world-slot`
- **唯一目标：** 建立唯一aggregate reducer、slot epoch、reservation/commit/abort和所有命令的StaleEpoch门。
- **文件集：
  - `modules/world-slot/Cargo.toml`
  - `modules/world-slot/src/lib.rs`
  - `modules/world-slot/src/epoch.rs`
  - `modules/world-slot/src/state.rs`
  - `modules/world-slot/src/aggregate.rs`
  - `modules/world-slot/src/admission_gate.rs`
  - `modules/world-slot/src/quota.rs`
  - `modules/world-slot/src/handles.rs`
  - `modules/world-slot/src/commands.rs`
  - `modules/world-slot/src/events.rs`
  - `modules/world-slot/src/service.rs`
  - `modules/world-slot/src/error.rs`
  - `modules/world-slot/tests/aggregate_state_test.rs`
  - `modules/world-slot/tests/stale_epoch_test.rs`

## 验收标准

- [ ] WorldSlotHost state/admission/epoch/quota只有aggregate可写；无共享mutable view。
- [ ] Reserve/Commit/Abort幂等，quota不会负数/泄漏；gate closed后无新commit。
- [ ] 每条transition command含expected epoch；重建/迁移后旧epoch统一`StaleEpoch`。
- [ ] D-001只作为maxActiveSlots默认=1的配置，不是pub const/类型限制。
- [ ] API不依赖session/maintenance/process类型。

## 依赖

- [`implement-host-runtime-supervision-cancellation-and-join`](./implement-host-runtime-supervision-cancellation-and-join.md)
- [`implement-pacing-timer-driven-scheduler`](./implement-pacing-timer-driven-scheduler.md)
- [`implement-coreclr-lifecycle-and-fault-passthrough`](./implement-coreclr-lifecycle-and-fault-passthrough.md)
- [`implement-persistence-durable-streams-queues-and-acks`](./implement-persistence-durable-streams-queues-and-acks.md)
- [`implement-transport-registry-bounded-ingress-egress`](./implement-transport-registry-bounded-ingress-egress.md)

## 接口

Consumes:
- 下游typed ports、generated WorldSlotHost state

Produces:
- WorldSlot aggregate/command/event/query ports
