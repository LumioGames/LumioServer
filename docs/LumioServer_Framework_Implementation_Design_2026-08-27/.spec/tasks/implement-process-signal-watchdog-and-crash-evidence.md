---
status: pending
---
# 实现 Signal、独立 Process Watchdog 与 Crash Evidence

## 涉及范围

- **Wave：** 6
- **归属：** `process`
- **唯一目标：** 通过host-runtime监督signal/watchdog，安装最小panic hook并请求Failure Bundle，不直接调用领域模块。
- **文件集：
  - `modules/process/src/signals.rs`
  - `modules/process/src/watchdog.rs`
  - `modules/process/src/crash.rs`
  - `modules/process/tests/panic_evidence_test.rs`
  - `modules/process/tests/watchdog_test.rs`

## 验收标准

- [ ] signal adapter只投ProcessControlCommand；重复SIGTERM合并，第二级强制策略有明确证据。
- [ ] Process Watchdog配置/heartbeat source与Slot Watchdog独立。
- [ ] panic hook不锁普通队列、不格式化secret，只触发emergency/bundle请求。
- [ ] 监督task/thread失效有terminal report；不自建spawn/sleep。
- [ ] watchdog不从Managed异常推断FaultClass。

## 依赖

- [`implement-process-config-lifecycle-and-explicit-components`](./implement-process-config-lifecycle-and-explicit-components.md)
- [`implement-host-runtime-supervision-cancellation-and-join`](./implement-host-runtime-supervision-cancellation-and-join.md)
- [`implement-observability-failure-bundle-and-emergency-path`](./implement-observability-failure-bundle-and-emergency-path.md)

## 接口

Consumes:
- SupervisorEvent/heartbeat、OS signals

Produces:
- ProcessControlCommand、crash/watchdog evidence
