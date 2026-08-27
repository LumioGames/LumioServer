---
status: pending
---
# 实现本地 Release Member State、Health 与 Report

## 涉及范围

- **Wave：** 6
- **归属：** `release-agent`
- **唯一目标：** 建立本进程local state reducer、timer-driven health和control-plane report，不拥有全局Pool。
- **文件集：
  - `modules/release-agent/src/member_state.rs`
  - `modules/release-agent/src/health.rs`
  - `modules/release-agent/src/reports.rs`
  - `modules/release-agent/src/service.rs`
  - `modules/release-agent/tests/local_state_test.rs`

## 验收标准

- [ ] local state名称明确不冒充/替代公共Pool desired state。
- [ ] health由TimerFired驱动，无自建线程；旧generation拒绝。
- [ ] Serving/Draining/ReadyToExit/Fault evidence report sequence单调。
- [ ] 不存在TargetActivated、实例创建或跨进程route decision代码/测试。
- [ ] report通道不可用不静默改变本地state。

## 依赖

- [`implement-release-catalog-manifest-verification`](./implement-release-catalog-manifest-verification.md)
- [`implement-host-runtime-clock-and-timer-delivery`](./implement-host-runtime-clock-and-timer-delivery.md)
- [`implement-control-plane-behavior-core`](./implement-control-plane-behavior-core.md)

## 接口

Consumes:
- VerifiedReleaseBundle、StatusReportPort

Produces:
- `ReleaseAgent` reducer、`LocalReleaseReport`
