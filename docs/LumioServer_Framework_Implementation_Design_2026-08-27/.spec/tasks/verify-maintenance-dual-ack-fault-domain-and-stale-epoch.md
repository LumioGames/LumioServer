---
status: pending
---
# 验收维护双 Ack、Fault Domain 与 StaleEpoch

## 涉及范围

- **Wave：** 15
- **归属：** `e2e`
- **唯一目标：** 组合Graceful/Forced、persistence/audit ack顺序、Runtime witness缺失/存在和slot重建旧命令场景。
- **文件集：
  - `tests/e2e/tests/maintenance_dual_ack_test.rs`
  - `tests/e2e/tests/fault_domain_test.rs`
  - `tests/e2e/tests/stale_epoch_test.rs`
  - `tests/e2e/fixtures/maintenance_fault_matrix.toml`

## 验收标准

- [ ] 任一durable ack缺失不得ReadyToExit；两者任意顺序均可收敛。
- [ ] 无Runtime witness统一SlotStateUnproven；有witness按FaultClass隔离Session/Slot/Process。
- [ ] slot epoch更新后旧maintenance/session/native completion全`StaleEpoch`且不改状态。
- [ ] 终态只有ReadyToExit/进程退出，无TargetActivated。
- [ ] 每个失败场景生成合法partial/full FailureBundle与审计事实。

## 依赖

- [`verify-local-embedded-vertical-skeleton`](./verify-local-embedded-vertical-skeleton.md)
- [`implement-maintenance-orchestration-and-dual-durable-ack`](./implement-maintenance-orchestration-and-dual-durable-ack.md)

## 接口

Consumes:
- ReferenceHost fault injection、Runtime witness fixtures

Produces:
- 跨模块故障/维护验收报告
