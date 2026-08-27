---
status: pending
---
# 实现结构化监督、取消、执行预算与 Join

## 涉及范围

- **Wave：** 4
- **归属：** `host-runtime`
- **唯一目标：** 建立统一的 task/thread supervisor、CancellationScope、bounded executor permits、heartbeat 和 join barrier。
- **文件集：
  - `modules/host-runtime/src/runtime.rs`
  - `modules/host-runtime/src/cancellation.rs`
  - `modules/host-runtime/src/supervision.rs`
  - `modules/host-runtime/src/thread.rs`
  - `modules/host-runtime/src/executor.rs`
  - `modules/host-runtime/src/join.rs`
  - `modules/host-runtime/src/backoff.rs`
  - `modules/host-runtime/tests/supervision_test.rs`

## 验收标准

- [ ] 任何受监督执行单元有名称、owner、fault policy、heartbeat、terminal report。
- [ ] 没有 detached task；cancel后join在配置deadline内终态或返回精确未终止单元。
- [ ] owned thread只接受命名 `OwnedThreadRunner`，policy扫描拒绝模块直接spawn/sleep。
- [ ] executor permit/queue满载同步失败且内存有界；backoff只生成下一deadline。
- [ ] runner panic转换为 `SupervisorEvent`，不吞panic证据。

## 依赖

- [`implement-host-runtime-clock-and-timer-delivery`](./implement-host-runtime-clock-and-timer-delivery.md)

## 接口

Consumes:
- bounded ports、TimerService

Produces:
- `HostRuntime`、`TaskSupervisor`、`ThreadSupervisor`、`CancellationScope`、`JoinReport`
