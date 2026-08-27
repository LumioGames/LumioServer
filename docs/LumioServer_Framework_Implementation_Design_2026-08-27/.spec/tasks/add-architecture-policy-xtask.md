---
status: pending
---
# 实现架构与依赖守卫 xtask

## 涉及范围

- **Wave：** 1
- **归属：** `repository`
- **唯一目标：** 把模块 DAG、旧名、禁止线程 API、Queue Matrix 登记和 protocol-dispatch 封锁变成机器检查。
- **文件集：
  - `tools/xtask/src/policy.rs`
  - `tools/xtask/src/dag.rs`
  - `tools/xtask/src/queues.rs`
  - `tools/xtask/src/source_scan.rs`
  - `.spec/guards/module-dag.toml`
  - `.spec/guards/queue-contracts.toml`
  - `tests/policy/invalid_cycle.toml`
  - `tests/policy/invalid_queue.toml`

## 验收标准

- [ ] `cargo xtask policy check` 对当前树通过。
- [ ] mutation fixture 对编译环、图外边、无界 channel、直接 `spawn/sleep`、旧一等模块名、缺 queue owner/full/close、protocol-dispatch Cargo/src 逐一失败。
- [ ] 只允许 process composition root 知道全部模块；host-profiles 零一等模块依赖。
- [ ] 扫描只针对源码/manifest，不误报文档中解释性的禁用词。
- [ ] guard manifest 与 modules/README 三张图逐条可追踪。

## 依赖

- [`establish-cargo-workspace-and-rust-standards`](./establish-cargo-workspace-and-rust-standards.md)

## 接口

Consumes:
- modules/README 编译/命令/事件图与 Queue Contract Matrix

Produces:
- `cargo xtask policy check` 与 mutation fixtures
