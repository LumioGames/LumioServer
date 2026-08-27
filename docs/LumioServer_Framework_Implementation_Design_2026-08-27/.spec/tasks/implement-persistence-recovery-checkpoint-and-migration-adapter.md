---
status: pending
---
# 实现恢复、Checkpoint 与 Migration 消费 Adapter

## 涉及范围

- **Wave：** 6
- **归属：** `persistence-host`
- **唯一目标：** 从合法active snapshot与durable logs生成可重复RecoveryPlan，并以typed timer/tick evidence触发checkpoint。
- **文件集：
  - `modules/persistence-host/src/checkpoint.rs`
  - `modules/persistence-host/src/recovery.rs`
  - `modules/persistence-host/src/migration.rs`
  - `modules/persistence-host/tests/recovery_fixture_test.rs`
  - `modules/persistence-host/tests/checkpoint_trigger_test.rs`

## 验收标准

- [ ] 恢复扫描拒绝bad hash/length/activation state，选择规则确定且可重复。
- [ ] 坏尾部不默默吞；输出明确truncate/indeterminate/fatal plan evidence。
- [ ] checkpoint不读取wall clock或sleep；只消费TimerFired或明确Runtime tick evidence。
- [ ] migration只执行上游manifest定义节点/顺序，不在本仓发明DAG。
- [ ] listener/admission在RecoveryCompleted成功前不可开放（由process集成验证）。

## 依赖

- [`implement-persistence-durable-streams-queues-and-acks`](./implement-persistence-durable-streams-queues-and-acks.md)
- [`implement-host-runtime-clock-and-timer-delivery`](./implement-host-runtime-clock-and-timer-delivery.md)

## 接口

Consumes:
- active snapshot/log streams、generated migration manifest

Produces:
- `RecoveryPlan/Report`、Checkpoint command/effects
