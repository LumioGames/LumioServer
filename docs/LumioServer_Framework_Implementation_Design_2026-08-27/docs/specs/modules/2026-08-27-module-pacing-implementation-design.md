# LumioServer `pacing` 模块实现级设架

> 架构基线：`LGE-V1.2-2026-08-27`  
> 实现优先级：**P0**  
> crate：`lumio-pacing`  
> 本文只定义仓内实现结构；公共 Schema / ABI / ID / 状态机仍由 `LumioGameEngineArchitecture` 维护。

## A. 一句话职责与非职责

**职责：** 把 host-runtime 的单调时间与配置化 pacing policy 转换为有界 `TickPermit`，拥有 scheduler 内部状态但不拥有 Logical Tick、WorldSlotHost transition 或业务回调。

**明确不负责：**
- 不提供通用 timer，不拥有 reconnect/maintenance/checkpoint deadlines。
- 不调用 Runtime/Gameplay，不生成 TickId，不应用权威状态。
- pause/resume/quiesce 只能接受 world-slot typed command，不接受任意模块直接修改。
- 不自建 sleep/轮询线程或 timer wheel。

## B. crate、目录与文件清单

建议 package 名：`lumio-pacing`。文件按所有权/职责切分，不创建 `common.rs`、`globals.rs`、`event_bus.rs` 或大锅文件。

| 文件 | 唯一职责 |
| --- | --- |
| `modules/pacing/Cargo.toml` | 只依赖 host-runtime/foundations。 |
| `modules/pacing/src/lib.rs` | 导出 pacing commands/permits/views。 |
| `modules/pacing/src/config.rs` | 私有配置与 provisional defaults。 |
| `modules/pacing/src/state.rs` | Idle/Scheduled/Paused/Stopped internal state。 |
| `modules/pacing/src/decision.rs` | 纯函数计算 next deadline/overrun action。 |
| `modules/pacing/src/scheduler.rs` | TimerFired→permit→next schedule reducer。 |
| `modules/pacing/src/commands.rs` | Configure/Start/Pause/Resume/Stop/TimerFired。 |
| `modules/pacing/src/permit.rs` | `TickPermit` supplier-neutral value。 |
| `modules/pacing/src/metrics.rs` | jitter/debt/missed permit。 |
| `modules/pacing/src/error.rs` | state/stale timer/port full。 |
| `modules/pacing/tests/decision_property_test.rs` | deadline monotonic/no unbounded debt。 |
| `modules/pacing/tests/paused_clock_test.rs` | Tokio paused time。 |
| `modules/pacing/tests/permit_backpressure_test.rs` | full SPSC 不 busy loop。 |

## C. 公开/内部类型、Trait、命令、事件与端口

### C.1 类型、命令与事件
- `PacingController`、`PacingStateView`、`PacingConfig`。
- `PacingCommand::{Configure, Start, Pause, Resume, Stop, TimerFired}`。
- `TickPermit { permitGeneration, scheduledAt, observedAt }`；不含 Logical TickId。
- `OverrunAction::{EmitOne, Skip, PauseAndSignal}` 为内部 policy 结果。

### C.2 稳定仓内端口
| 接口 | 落点 | 契约 |
| --- | --- | --- |
| `PacingCommandPort::try_send(PacingCommand)` | `commands.rs` | 只有 world-slot/timer 可生产。 |
| `TickPermitProducer::try_push(TickPermit)` | `permit.rs` | 单 producer；满载不覆盖。 |
| `PacingDecision::next(state, now)` | `decision.rs` | 纯函数，便于 property test。 |

### C.3 Rust 签名草案

以下签名是**仓内实现接口**，不是新增公共 ABI；实现时只能引用上游 generated 类型，不能复制公共字段。

```rust
impl PacingCommandPort {
    pub fn try_send(&self, command: PacingCommand) -> Result<(), PacingPortError>;
}

impl PacingDecision {
    pub fn next(
        state: &PacingState,
        now: MonotonicInstant,
    ) -> Result<PacingAction, PacingError>;
}

pub struct TickPermit {
    pub permit_generation: PermitGeneration,
    pub scheduled_at: MonotonicInstant,
    pub observed_at: MonotonicInstant,
}
```

## D. 状态、资源与生命周期所有权

- `PacingState`、目标周期、下一单调 deadline、overrun/debt 统计、permit generation。
- 当前 scheduler enable/pause 状态，仅为内部执行状态；Host lifecycle owner 仍是 world-slot。
- tick permit SPSC producer 与 pacing metrics。
- 不保存业务 callback/World mutable reference。

### D.1 模块红线
- 禁止 `on_tick_boundary(callback)`。
- 禁止在 timer 线程执行 Tick 或业务。

## E. 线程、队列、背压与关闭

### E.1 线程/执行上下文
- 无自有线程；通过 host-runtime TimerService 收到 `TimerFired`。
- control reducer 在 host-runtime serial executor；permit 发送到 world-slot SPSC。
- 同一时间最多一个活动 tick timer generation；迟到 timer 拒绝。
- overrun 不通过 busy-loop 补 tick；按 policy drop/cap debt 并显式 metric。

### E.2 队列合同
| 队列 | 元素 | 所有者 | 生产者 | 消费者 | 顺序 | 容量配置 | 满载动作 | 关闭语义 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `PacingCommandInbox` | `PacingCommand` | pacing | world-slot/timer | pacing reducer | FIFO | `pacing.command.capacity` | 返回 busy；Stop/Pause 预留槽 | stop 后拒绝 schedule |
| `TickPermitQueue` | `TickPermit` | world-slot | pacing | Simulation Owner Thread | 严格 FIFO SPSC | `pacing.tick_permit.capacity` | 不覆盖；记录 missed permit/overrun | pause/quiesce 关闭或 drain |

这些行必须写入仓库 Queue Contract Registry；数值来自配置快照，SRV-D 默认值不得提升为公共常量。

## F. 失败分类、恢复与显式 Ack

| 分类 | 实现要求 |
| --- | --- |
| 可拒绝 | 非法 state command、stale timer generation、无效 rate；返回 ack。 |
| 可重试 | permit queue 满；不 schedule 额外 backlog，等待 world-slot 恢复命令。 |
| Slot 级候选 | 持续 overrun 只发 pacing health event；是否 fault 由 world-slot/Runtime witness 政策决定。 |
| 进程级 | TimerService/supervisor failure 由 host-runtime 报告，pacing 不分类。 |

## G. 编译依赖、成熟方案与 Adapter 隔离

### G.1 编译依赖
**允许：**
- host-runtime
- observability
- host-profiles
- `thiserror`

**禁止：**
- world-slot crate 反向类型（用 pacing-owned epoch value）
- coreclr/session/transport
- Tokio type 进入 public API
- callback

### G.2 成熟方案选择
| 方案 | 用途 | 成熟性、许可证与隔离 |
| --- | --- | --- |
| host-runtime `TimerService`（Tokio DelayQueue） | deadline | 复用成熟 timer；pacing 只处理命令。 |
| `hdrhistogram`（经 observability） | jitter distribution | 成熟 Apache-2.0；pacing 只提交数值。 |

### G.3 明确拒绝的自研项
- 不自研 timer wheel、sleep loop、线程或通用 scheduler。
- 自有纯 pacing 决策函数是必需领域策略；不能交给通用 interval 因为需有界 backlog、pause/epoch 和显式 overrun。

## H. 测试面与 Fixture

- 单元/Property：deadline 单调、pause 后无 permit、resume generation 更新、debt 有上界。
- 故障：timer late/cancel race、permit full、clock advance 大跳。
- 确定性：相同 `(state, now)` 得到相同 action；不读取 wall clock。
- 集成：world-slot quiesce 后不再开始新 tick。

## I. 决策门与配置默认

- 所有 tick rate/catch-up 数值是配置默认，非公共常量；需 benchmark 后回写 measured status。
- Logical Tick 仍归 Runtime；pacing 只发 permit。

## J. 本模块任务卡

| 任务 | Wave | 单一目标 | 依赖 |
| --- | --- | --- | --- |
| [`implement-pacing-state-and-decision-core`](../../../.spec/tasks/implement-pacing-state-and-decision-core.md) | Wave 3 | 定义不含Logical TickId的 scheduler state、deadline/overrun纯函数和typed commands。 | `implement-host-runtime-bounded-ports`, `implement-host-profile-resolution-and-capability-matching` |
| [`implement-pacing-timer-driven-scheduler`](../../../.spec/tasks/implement-pacing-timer-driven-scheduler.md) | Wave 4 | 接入 host-runtime TimerService 和SPSC permit，不自建线程或catch-up backlog。 | `implement-pacing-state-and-decision-core`, `implement-host-runtime-clock-and-timer-delivery` |

## K. 完成定义

本模块的任务卡全部通过；模块单测/fixture/故障测试通过；Queue Registry、Cargo DAG、许可证与源码红线检查通过；没有供应商类型泄漏、无界队列、任意回调或未见证 FaultClass 推断。
