---
status: pending
---
# 封闭 Slot 资源、Watchdog 与重复生命周期 Soak

## 涉及范围

- **Wave：** 9
- **归属：** `world-slot`
- **唯一目标：** 验证create/quiesce/destroy/recreate和owner stall下线程、队列、handle、epoch、evidence终态。
- **文件集：
  - `modules/world-slot/tests/resource_soak_test.rs`
  - `modules/world-slot/tests/watchdog_fault_test.rs`

## 验收标准

- [ ] 重复生命周期后所有host-owned资源归零或列入retained evidence。
- [ ] owner stall触发Slot Watchdog event，不触发process watchdog阈值复用。
- [ ] recreate后所有旧timer/completion/command被epoch/generation拒绝。
- [ ] 无unbounded RSS增长、无detached thread、join可完成。
- [ ] 无Runtime witness的stall结果仍是SlotStateUnproven。

## 依赖

- [`implement-world-slot-quiesce-migration-and-fault-adjudication`](./implement-world-slot-quiesce-migration-and-fault-adjudication.md)

## 接口

Consumes:
- WorldSlot lifecycle、host-runtime supervisor

Produces:
- world-slot soak/fault验收证据
