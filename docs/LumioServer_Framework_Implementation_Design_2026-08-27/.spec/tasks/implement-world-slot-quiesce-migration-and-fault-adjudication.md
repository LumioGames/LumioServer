---
status: pending
---
# 实现 Quiesce、聚合迁移与 Runtime Witness 裁决

## 涉及范围

- **Wave：** 8
- **归属：** `world-slot`
- **唯一目标：** 封闭close admission→stop new tick→drain→persist→stop流程，并确保只有world-slot发起aggregate migration/epoch更新。
- **文件集：
  - `modules/world-slot/src/quiesce.rs`
  - `modules/world-slot/src/migration.rs`
  - `modules/world-slot/src/fault.rs`
  - `modules/world-slot/tests/fault_witness_test.rs`
  - `modules/world-slot/tests/quiesce_test.rs`

## 验收标准

- [ ] quiesce命令幂等，任一步失败保留可证明状态，不出现半活gate/tick。
- [ ] InitiateAggregateMigration只有本aggregate reducer生成；成功前递增epoch并使旧命令失效。
- [ ] 有Runtime witness时FaultClass逐字段消费；无witness固定SlotStateUnproven。
- [ ] coreclr异常可捕获性/transport错误不被用作FaultClass依据。
- [ ] ReadyToStop只在required persistence effect已ack或明确fatal终态后发。

## 依赖

- [`implement-world-slot-simulation-owner-loop`](./implement-world-slot-simulation-owner-loop.md)
- [`implement-persistence-durability-fault-matrix`](./implement-persistence-durability-fault-matrix.md)
- [`implement-observability-failure-bundle-and-emergency-path`](./implement-observability-failure-bundle-and-emergency-path.md)

## 接口

Consumes:
- Runtime witness、persistence events、quiesce command

Produces:
- FaultAdjudicated、Quiesced、ReadyToStop
