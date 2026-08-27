---
status: pending
---
# 组装进程启动、Readiness、维护与结构化关闭

## 涉及范围

- **Wave：** 12
- **归属：** `process`
- **唯一目标：** 在wiring中连接所有具体typed ports，落实恢复前置、listener/admission开放门、maintenance ReadyToExit和逆序join。
- **文件集：
  - `modules/process/src/wiring.rs`
  - `modules/process/src/shutdown.rs`
  - `modules/process/src/lib.rs`
  - `modules/process/src/main.rs`
  - `modules/process/tests/shutdown_order_test.rs`
  - `modules/process/tests/wiring_graph_test.rs`

## 验收标准

- [ ] 每条wiring边能映射到modules/README编译/命令/事件图；图外边测试失败。
- [ ] 启动顺序至少满足：contracts/config/profile→runtime/obs→release verify→storage recovery→CoreCLR/slot→auth/session/transport→readiness/admission。
- [ ] RemoteDS缺D-010/D-011/D-004所需production adapter时在listener前精确拒绝；LocalEmbedded测试plan可运行。
- [ ] OS shutdown沿 `process -> world-slot` 发送 `QuiesceForShutdown`；外部维护仍由maintenance-agent；两条路径最终都由process cancel/join/flush并退出。
- [ ] 无TargetActivated、无通用callback、无共享mutable registry。

## 依赖

- [`implement-maintenance-orchestration-and-dual-durable-ack`](./implement-maintenance-orchestration-and-dual-durable-ack.md)
- [`implement-process-signal-watchdog-and-crash-evidence`](./implement-process-signal-watchdog-and-crash-evidence.md)
- [`implement-world-slot-resource-and-watchdog-soak`](./implement-world-slot-resource-and-watchdog-soak.md)

## 接口

Consumes:
- 全部模块具体 factories/ports、ValidatedHostCompositionPlan

Produces:
- 可启动/关闭的`lumio-server` composition root
