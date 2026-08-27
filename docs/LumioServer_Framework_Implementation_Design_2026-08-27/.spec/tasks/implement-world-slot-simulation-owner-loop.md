---
status: pending
---
# 实现 Simulation Owner Thread 与 Tick Barrier 主链

## 涉及范围

- **Wave：** 7
- **归属：** `world-slot`
- **唯一目标：** 建立host-runtime owned runner，固定执行permit→bounded ingress/native completion drain→Managed Tick→barrier outcome→egress/persistence effects。
- **文件集：
  - `modules/world-slot/src/owner_thread.rs`
  - `modules/world-slot/src/tick_loop.rs`
  - `modules/world-slot/src/watchdog.rs`
  - `modules/world-slot/tests/tick_barrier_test.rs`
  - `modules/world-slot/tests/owner_thread_affinity_test.rs`

## 验收标准

- [ ] 只有owner thread调用ManagedTickPort和drain ingress/native completion。
- [ ] 控制命令只在定义的安全点观察；网络/IOcompletion不得直接调用Managed。
- [ ] 每tick drain有items/bytes/time预算，不能饿死control/quiesce。
- [ ] Runtime返回的Logical Tick/correlation原样用于effects，host不自行递增权威Tick。
- [ ] heartbeat含slot epoch；SRV-D-003与process watchdog独立。

## 依赖

- [`implement-world-slot-aggregate-epoch-admission-and-quota`](./implement-world-slot-aggregate-epoch-admission-and-quota.md)
- [`implement-coreclr-netcorehost-adapter`](./implement-coreclr-netcorehost-adapter.md)
- [`implement-persistence-recovery-checkpoint-and-migration-adapter`](./implement-persistence-recovery-checkpoint-and-migration-adapter.md)
- [`implement-transport-local-embedded-fidelity-adapter`](./implement-transport-local-embedded-fidelity-adapter.md)

## 接口

Consumes:
- TickPermit、IngressReader、ManagedTickPort、Persistence/Egress ports

Produces:
- `SimulationOwnerRunner`、TickCompleted/effects
