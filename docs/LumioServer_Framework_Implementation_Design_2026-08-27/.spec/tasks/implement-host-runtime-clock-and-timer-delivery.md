---
status: pending
---
# 实现单调时钟与到期命令投递

## 涉及范围

- **Wave：** 3
- **归属：** `host-runtime`
- **唯一目标：** 使用 Tokio time/DelayQueue 实现可取消 timer，目标是 typed `TimerDeliveryPort`，不执行业务回调。
- **文件集：
  - `modules/host-runtime/src/clock.rs`
  - `modules/host-runtime/src/timer.rs`
  - `modules/host-runtime/src/timer_delivery.rs`
  - `modules/host-runtime/tests/timer_delivery_test.rs`

## 验收标准

- [ ] 生产时间只用 monotonic instant；wall clock不能决定 timer排序。
- [ ] `schedule`只接收 deadline/class/typed sender，不接收 closure/function pointer。
- [ ] 同 deadline按注册 sequence稳定投递；cancel/fire race以 generation拒绝迟到项。
- [ ] TimerDeliveryPort满载产生监督事件/metric，不执行目标业务作为fallback。
- [ ] Tokio paused time测试覆盖advance/cancel/shutdown。

## 依赖

- [`implement-host-runtime-bounded-ports`](./implement-host-runtime-bounded-ports.md)

## 接口

Consumes:
- `PortSpec`、Tokio官方time

Produces:
- `MonotonicClock`、`TimerService`、`TimerFired`
