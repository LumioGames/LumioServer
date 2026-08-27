---
status: pending
---
# 完善仓库 DAG、队列、源码与许可证 Gate

## 涉及范围

- **Wave：** 13
- **归属：** `repository`
- **唯一目标：** 把最终Cargo图和源码提交到policy/cargo-deny/audit检查，验证零环、零无界、零旧名、零GPL热路径。
- **文件集：
  - `tests/policy/workspace_dag_test.rs`
  - `tests/policy/queue_matrix_coverage_test.rs`
  - `tests/policy/source_redline_test.rs`
  - `tests/policy/license_policy_test.rs`
  - `.github/workflows/rust-foundation.yml`

## 验收标准

- [ ] cargo metadata图符合允许DAG且无cycle；protocol-dispatch零package/edge。
- [ ] Queue Matrix每个实现队列有唯一owner/producer/consumer/order/capacity/full/close并可反查测试。
- [ ] source guard拒绝直接spawn/sleep/unbounded/callback registry/ClientReplicaSession/TargetActivated/旧一等模块路径。
- [ ] cargo deny/audit以lockfile执行；生产热路径无GPL/未知许可证，例外必须为空或显式批准。
- [ ] fmt/clippy/nextest/contract/policy/deny/audit在CI单一入口通过。

## 依赖

- [`assemble-process-startup-readiness-maintenance-and-shutdown`](./assemble-process-startup-readiness-maintenance-and-shutdown.md)
- [`add-architecture-policy-xtask`](./add-architecture-policy-xtask.md)

## 接口

Consumes:
- 最终workspace/queue registry/Cargo.lock

Produces:
- Foundation architecture CI gate
