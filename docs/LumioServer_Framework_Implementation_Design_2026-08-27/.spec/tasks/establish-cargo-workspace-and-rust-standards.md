---
status: pending
---
# 建立 Cargo Workspace 与 Rust 质量基线

## 涉及范围

- **Wave：** 0
- **归属：** `repository`
- **唯一目标：** 一次性创建可解析、可 lint、可测试但不含生产行为的 workspace 骨架，并把首次 Rust 引入要求回写到 code-style/testing 标准。
- **文件集：
  - `Cargo.toml`
  - `Cargo.lock`
  - `rust-toolchain.toml`
  - `rustfmt.toml`
  - `.cargo/config.toml`
  - `clippy.toml`
  - `deny.toml`
  - `nextest.toml`
  - `.spec/knowledge/standards/code-style.md`
  - `.spec/knowledge/standards/testing.md`
  - `modules/process/Cargo.toml`
  - `modules/process/src/lib.rs`
  - `modules/process/src/main.rs`
  - `tools/xtask/Cargo.toml`
  - `tools/xtask/src/main.rs`

## 验收标准

- [ ] `cargo metadata --locked --no-deps` 成功且 workspace resolver=2。
- [ ] toolchain 固定 1.98.0，所有 package 继承 edition/rust-version/license/lints。
- [ ] `cargo fmt --check`、`cargo clippy --workspace --all-targets --all-features -- -D warnings`、`cargo nextest run --workspace` 可在空骨架通过。
- [ ] code-style/testing 新增上述命令、unsafe/lint/test 分类纪律，未修改公共架构镜像。
- [ ] workspace 不含 `protocol-dispatch`，不存在 `common/globals/event_bus/everything` crate/file。

## 依赖

- 无仓内前置任务。

## 接口

Consumes:
- 仓库现有 15 个模块 README 与 `.spec` 标准

Produces:
- 可供各模块并行增加代码的锁定 workspace；Rust 标准回写
