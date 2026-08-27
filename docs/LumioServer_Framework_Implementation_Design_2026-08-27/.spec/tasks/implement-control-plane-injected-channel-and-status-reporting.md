---
status: pending
---
# 实现 Injected Channel 与本地状态上报

## 涉及范围

- **Wave：** 6
- **归属：** `control-plane-adapter`
- **唯一目标：** 提供测试专用injected channel、bounded status queue、report coalescing与ReadyToExit不可丢语义。
- **文件集：
  - `modules/control-plane-adapter/src/reports.rs`
  - `modules/control-plane-adapter/src/adapters/injected.rs`
  - `modules/control-plane-adapter/src/adapters/production_gate.rs`
  - `modules/control-plane-adapter/tests/report_delivery_test.rs`
  - `modules/control-plane-adapter/tests/production_gate_test.rs`

## 验收标准

- [ ] Injected adapter仍输出UnverifiedControlFrame并经过authenticator，不直送verified command。
- [ ] health/progress可按sequence合并，ReadyToExit evidence必须显式delivery ack或terminal failure。
- [ ] production profile引用injected adapter时host-profile validation失败。
- [ ] channel retry只用host-runtime timer/backoff，无sleep/poll thread。
- [ ] 报告不包含cluster desired state/TargetActivated。

## 依赖

- [`implement-control-plane-behavior-core`](./implement-control-plane-behavior-core.md)
- [`implement-host-profile-fault-decorator-declarations`](./implement-host-profile-fault-decorator-declarations.md)
- [`implement-host-runtime-clock-and-timer-delivery`](./implement-host-runtime-clock-and-timer-delivery.md)

## 接口

Consumes:
- ControlChannel SPI、LocalStatusReport

Produces:
- InjectedControlChannel、StatusReportPort
