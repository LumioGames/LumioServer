---
status: pending
---
# 建立 E2E Reference Host Shell 与 Fixture Wiring

## 涉及范围

- **Wave：** 13
- **归属：** `e2e`
- **唯一目标：** 用dev-only testkit/injected adapters组装真实生产模块，提供可重复的LocalEmbedded场景驱动器。
- **文件集：
  - `tests/e2e/Cargo.toml`
  - `tests/e2e/src/lib.rs`
  - `tests/e2e/src/reference_host.rs`
  - `tests/e2e/src/scenario.rs`
  - `tests/e2e/src/fixtures.rs`
  - `tests/e2e/tests/reference_host_smoke_test.rs`

## 验收标准

- [ ] Reference Host引用生产crate，生产crate不反向依赖testkit/e2e。
- [ ] 场景驱动只通过byte carrier、typed control frame、timer/ports，不可直接修改模块state。
- [ ] 可注入deterministic clock/fault points且seed/evidence可重放。
- [ ] 启动/停止smoke后所有supervised units join。
- [ ] fixture只加载上游generated/schema资产，不复制公共字段。

## 依赖

- [`assemble-process-startup-readiness-maintenance-and-shutdown`](./assemble-process-startup-readiness-maintenance-and-shutdown.md)
- [`add-lumio-host-testkit`](./add-lumio-host-testkit.md)

## 接口

Consumes:
- 生产composition、testkit、upstream fixtures

Produces:
- E2E ReferenceHost/Scenario API
