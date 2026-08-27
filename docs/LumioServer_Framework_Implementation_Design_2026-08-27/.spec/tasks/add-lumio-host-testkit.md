---
status: pending
---
# 建立非生产 Reference Host 测试支撑

## 涉及范围

- **Wave：** 1
- **归属：** `testkit`
- **唯一目标：** 创建仅 dev-dependency 可用的受控时钟、故障注入、typed port probe、fixture loader，不被生产模块依赖。
- **文件集：
  - `crates/lumio-host-testkit/Cargo.toml`
  - `crates/lumio-host-testkit/src/lib.rs`
  - `crates/lumio-host-testkit/src/clock.rs`
  - `crates/lumio-host-testkit/src/fault.rs`
  - `crates/lumio-host-testkit/src/ports.rs`
  - `crates/lumio-host-testkit/src/fixtures.rs`
  - `crates/lumio-host-testkit/src/assertions.rs`
  - `crates/lumio-host-testkit/tests/self_test.rs`

## 验收标准

- [ ] crate 仅在 workspace dev-dependencies/test targets 使用；`cargo xtask policy check` 拒绝生产 dependency。
- [ ] 提供 deterministic sequence、fault point、bounded port probe 和上游 fixture loader，不复制 Schema。
- [ ] 不提供 bypass codec/auth/queue 的 convenience API。
- [ ] 测试时钟只用于 host-runtime adapter测试，生产类型不依赖 testkit。
- [ ] `cargo nextest run -p lumio-host-testkit` 通过。

## 依赖

- [`establish-cargo-workspace-and-rust-standards`](./establish-cargo-workspace-and-rust-standards.md)

## 接口

Consumes:
- 上游 fixtures 与 host-runtime 将定义的 supplier-neutral测试接口

Produces:
- `lumio-host-testkit` dev crate
