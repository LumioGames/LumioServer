---
status: pending
---
# 接入三个只读生成契约包

## 涉及范围

- **Wave：** 1
- **归属：** `contracts`
- **唯一目标：** 建立架构公共契约、Managed Host ABI、Core Engine contract 的只读消费边界与 lock manifest，禁止手写第二套 Schema。
- **文件集：
  - `generated/lumio-architecture-contracts/Cargo.toml`
  - `generated/lumio-architecture-contracts/src/lib.rs`
  - `generated/lumio-managed-host-contracts/Cargo.toml`
  - `generated/lumio-managed-host-contracts/src/lib.rs`
  - `generated/lumio-core-engine-contracts/Cargo.toml`
  - `generated/lumio-core-engine-contracts/src/lib.rs`
  - `contracts/architecture-contracts.lock.toml`
  - `contracts/managed-host-contracts.lock.toml`
  - `contracts/core-engine-contracts.lock.toml`
  - `tools/xtask/src/contracts.rs`

## 验收标准

- [ ] 三个 crate 仅含生成/受控 re-export，不出现本仓手写公共字段或 enum。
- [ ] lock manifest 记录 source repository、BaselineId/version、content hash、generator identity；缺任一项 xtask 失败。
- [ ] 正反 fixtures 可由 `cargo xtask contracts verify` 定位并执行；生成目录手改会被 hash 检测。
- [ ] camelCase JSON、PascalCase 类型、snake_case C ABI 三种拼写规则分别验证。
- [ ] 不从上传的 v0.3 compatibility pointer生成契约。

## 依赖

- [`establish-cargo-workspace-and-rust-standards`](./establish-cargo-workspace-and-rust-standards.md)

## 接口

Consumes:
- `LGE-V1.2-2026-08-27` 架构源产物；GameRuntime Managed ABI；CoreEngine contract

Produces:
- 三个只读 generated crates与 contract verification command
