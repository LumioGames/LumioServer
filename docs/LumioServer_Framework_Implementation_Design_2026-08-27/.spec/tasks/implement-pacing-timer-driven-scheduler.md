---
status: pending
---
# 实现 Timer 驱动的 Permit Scheduler

## 涉及范围

- **Wave：** 4
- **归属：** `pacing`
- **唯一目标：** 接入 host-runtime TimerService 和SPSC permit，不自建线程或catch-up backlog。
- **文件集：
  - `modules/pacing/src/scheduler.rs`
  - `modules/pacing/src/metrics.rs`
  - `modules/pacing/tests/paused_clock_test.rs`
  - `modules/pacing/tests/permit_backpressure_test.rs`

## 验收标准

- [ ] 每次至多一个timer generation；迟到TimerFired拒绝。
- [ ] permit queue满时不覆盖、不busy-loop、不追加无界catch-up。
- [ ] pause/quiesce后不再发新permit；resume创建新generation。
- [ ] jitter/debt/missed permit metrics可观察。
- [ ] paused time集成测试通过。

## 依赖

- [`implement-pacing-state-and-decision-core`](./implement-pacing-state-and-decision-core.md)
- [`implement-host-runtime-clock-and-timer-delivery`](./implement-host-runtime-clock-and-timer-delivery.md)

## 接口

Consumes:
- TimerService、SpscProducer<TickPermit>

Produces:
- 可运行 `PacingController`
