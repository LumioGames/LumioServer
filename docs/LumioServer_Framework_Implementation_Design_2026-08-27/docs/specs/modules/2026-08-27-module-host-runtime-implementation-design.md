# LumioServer `host-runtime` 模块实现级设架

> 架构基线：`LGE-V1.2-2026-08-27`  
> 实现优先级：**P0**  
> crate：`lumio-host-runtime`  
> 本文只定义仓内实现结构；公共 Schema / ABI / ID / 状态机仍由 `LumioGameEngineArchitecture` 维护。

## A. 一句话职责与非职责

**职责：** 提供进程级单调时钟、到期命令投递、点到点有界端口、结构化取消、受监督线程/任务与受界执行预算；不执行任何业务状态迁移。

**明确不负责：**
- 不拥有 Logical Tick、pacing policy、Session/Slot/Release 状态或 Gameplay。
- Timer 到期只投递 `TimerFired`，绝不持有或调用业务闭包。
- 不提供全局 EventBus、任意字符串 topic、无界 executor 或 detached task。
- 不替代 transport reactor、Simulation Owner Thread、persistence worker 的领域循环。

## B. crate、目录与文件清单

建议 package 名：`lumio-host-runtime`。文件按所有权/职责切分，不创建 `common.rs`、`globals.rs`、`event_bus.rs` 或大锅文件。

| 文件 | 唯一职责 |
| --- | --- |
| `modules/host-runtime/Cargo.toml` | Tokio、tokio-util、crossbeam-channel、rtrb 与 lint。 |
| `modules/host-runtime/src/lib.rs` | 只导出稳定的机械 primitives。 |
| `modules/host-runtime/src/runtime.rs` | Tokio runtime 构造、启动和 shutdown。 |
| `modules/host-runtime/src/clock.rs` | `MonotonicClock`、`MonotonicInstant` 与生产 adapter。 |
| `modules/host-runtime/src/timer.rs` | Timer schedule/cancel 状态和 `DelayQueue` adapter。 |
| `modules/host-runtime/src/timer_delivery.rs` | 只发送 `TimerFired` 的目标端口。 |
| `modules/host-runtime/src/port.rs` | `BoundedSender/Receiver` 与容量、close、metrics。 |
| `modules/host-runtime/src/spsc.rs` | `rtrb` 隔离封装；只允许一个 producer/consumer。 |
| `modules/host-runtime/src/cancellation.rs` | 基于 `CancellationToken` 的层级取消。 |
| `modules/host-runtime/src/supervision.rs` | 任务/线程登记、panic 捕获、heartbeat 与终态。 |
| `modules/host-runtime/src/thread.rs` | 命名 owned-thread launcher 与 affinity metadata。 |
| `modules/host-runtime/src/executor.rs` | 有界 permit + queue 的 control executor。 |
| `modules/host-runtime/src/join.rs` | 结构化 join barrier 和超时证据。 |
| `modules/host-runtime/src/backoff.rs` | 基于成熟 backoff 组合的重试计划；不 sleep。 |
| `modules/host-runtime/src/error.rs` | `PortFull/Closed`、timer、supervision、join 错误。 |
| `modules/host-runtime/tests/port_contract_test.rs` | 满载、FIFO、关闭、SPSC owner。 |
| `modules/host-runtime/tests/timer_delivery_test.rs` | 暂停时钟、取消、同 deadline 顺序。 |
| `modules/host-runtime/tests/supervision_test.rs` | panic、cancel、join、迟到 heartbeat。 |
| `modules/host-runtime/tests/loom_port_close_test.rs` | 关闭竞态模型检查。 |

## C. 公开/内部类型、Trait、命令、事件与端口

### C.1 类型、命令与事件
- `HostRuntimeBuilder`、`HostRuntime`、`HostRuntimeHandle`。
- `BoundedSender<T>`、`BoundedReceiver<T>`、`SpscProducer<T>`、`SpscConsumer<T>`。
- `TimerService`、`TimerHandle`、`TimerFired`、`TimerClass`、`MonotonicInstant`。
- `CancellationScope`、`TaskSupervisor`、`ThreadSupervisor`、`SupervisorEvent`、`JoinReport`。
- `ExecutorBudget`、`ExecutionPermit`。

### C.2 稳定仓内端口
| 接口 | 落点 | 契约 |
| --- | --- | --- |
| `bounded_port<T>(PortSpec)` | `port.rs` | 创建唯一队列；`PortSpec` 必含 owner、producer、consumer、capacity、full、close 策略。 |
| `spsc_port<T>(PortSpec)` | `spsc.rs` | 运行时断言唯一生产/消费端并暴露非阻塞操作。 |
| `TimerService::schedule(deadline, class, delivery)` | `timer.rs` | 保存 typed sender，不接受回调；返回 generation-aware handle。 |
| `TimerService::cancel(handle)` | `timer.rs` | 幂等；旧 generation 的到期消息由消费者以 token 拒绝。 |
| `TaskSupervisor::spawn(spec, future)` | `supervision.rs` | 仅 composition/模块 runner 调用；登记名称、故障政策和 join。 |
| `ThreadSupervisor::spawn_owned(spec, runner)` | `thread.rs` | 只接受实现 `OwnedThreadRunner` 的命名类型，禁止裸闭包导出。 |

### C.3 Rust 签名草案

以下签名是**仓内实现接口**，不是新增公共 ABI；实现时只能引用上游 generated 类型，不能复制公共字段。

```rust
pub struct PortSpec {
    pub port_id: PortId,
    pub owner: &'static str,
    pub producer: &'static str,
    pub consumer: &'static str,
    pub capacity: usize,
    pub full_policy: PortFullPolicy,
    pub close_policy: PortClosePolicy,
}

pub fn bounded_port<T: Send + 'static>(
    spec: PortSpec,
) -> Result<(BoundedSender<T>, BoundedReceiver<T>), PortBuildError>;

pub fn spsc_port<T: Send + 'static>(
    spec: PortSpec,
) -> Result<(SpscProducer<T>, SpscConsumer<T>), PortBuildError>;

impl TimerService {
    pub fn schedule(
        &self,
        deadline: MonotonicInstant,
        class: TimerClass,
        delivery: BoundedSender<TimerFired>,
    ) -> Result<TimerHandle, TimerError>;
    pub fn cancel(&self, handle: TimerHandle) -> Result<CancelOutcome, TimerError>;
}

pub trait OwnedThreadRunner: Send + 'static {
    fn run(self, context: OwnedThreadContext) -> ThreadTerminal;
}
```

## D. 状态、资源与生命周期所有权

- `HostRuntime`/`HostRuntimeHandle`、任务与线程监督注册表、取消树、join 状态。
- `MonotonicInstant`、`TimerId`、`TimerGeneration`、活动 timer 表和关闭屏障。
- 机械有界端口的容量/关闭/深度统计；消息语义仍归消费模块。
- `ExecutorBudget` 与 permit 使用量；不拥有被执行命令的业务结果。

### D.1 模块红线
- 不得出现 `on_timer(Box<dyn Fn...>)`、全局 subscriber bus 或 `spawn_detached`。
- Timer 消息必须含 timer id/generation，消费方必须能拒绝迟到项。

## E. 线程、队列、背压与关闭

### E.1 线程/执行上下文
- 一个受监督 Tokio runtime 承载异步 I/O/control tasks；线程名、panic、join 均登记。
- 一个 timer driver 执行 `tokio_util::time::DelayQueue`；到期只 `try_send(TimerFired)`。
- 需要强亲和的 Simulation/IO/worker 线程由 `ThreadSupervisor::spawn_owned` 创建，线程函数是命名 runner，不是任意回调注册。
- 测试使用 Tokio 官方 paused time；生产只使用 monotonic clock。

### E.2 队列合同
| 队列 | 元素 | 所有者 | 生产者 | 消费者 | 顺序 | 容量配置 | 满载动作 | 关闭语义 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `BoundedPort<T>` | 具体 typed command/event | 端口 consumer | PortSpec 声明的 producer | PortSpec 声明的 consumer | 按端口声明 FIFO | 每个调用方配置键 | 返回 `PortFull`，业务 owner 按矩阵决策 | 显式 close；drain/abort 固定 |
| `SpscPort<T>` | 热路径元素 | 端口 consumer | 唯一 producer | 唯一 consumer | 严格 FIFO | 配置键 | `try_push` 失败，不覆盖旧值 | producer/consumer 任一关闭后终止 |
| `TimerDeliveryPort` | `TimerFired` | 目标模块 | timer driver | 目标 owner inbox | deadline + sequence 稳定序 | `timer.delivery.capacity` | 记录饱和并升级目标监督状态 | runtime cancel 后不再投递；已到期项可 drain |
| `SupervisorEventPort` | `SupervisorEvent` | host-runtime | 所有 supervisor | process | per-source FIFO | `runtime.supervisor.capacity` | 高严重度走 emergency fallback | join 完成后关闭 |

这些行必须写入仓库 Queue Contract Registry；数值来自配置快照，SRV-D 默认值不得提升为公共常量。

## F. 失败分类、恢复与显式 Ack

| 分类 | 实现要求 |
| --- | --- |
| 可拒绝 | 端口满/关闭、executor 无 permit、timer 已取消；同步返回稳定仓内错误。 |
| 可重试 | 短暂端口满按调用模块策略重试；runtime 不自行无限重试。 |
| 进程升级 | timer driver、supervisor 或 runtime worker panic；发 `SupervisorEvent::CriticalFailure`。 |
| 边界规则 | 业务异常不能由 runtime 解释为 FaultClass；只报告执行单元证据。 |

## G. 编译依赖、成熟方案与 Adapter 隔离

### G.1 编译依赖
**允许：**
- `tokio 1.53`
- `tokio-util 0.7`
- `crossbeam-channel 0.5`
- `rtrb 0.4`
- `thiserror`
- `tracing-core` 仅内部诊断桥

**禁止：**
- 任何业务模块 crate
- 公共架构 Schema 类型（除纯 correlation ID 时也优先 opaque）
- 无界 channel
- 业务 callback registry

### G.2 成熟方案选择
| 方案 | 用途 | 成熟性、许可证与隔离 |
| --- | --- | --- |
| `tokio` | Reactor、async task、signal/time 基础 | 生态成熟、MIT；只在 runtime adapter 后，稳定端口无 Tokio 类型。 |
| `tokio-util::time::DelayQueue` | timer 到期排序与取消 | 避免自研 timer wheel；timer driver 不执行业务。 |
| `tokio-util::sync::CancellationToken` | 结构化取消 | 成熟、与 Tokio 同维护；包装成 `CancellationScope`。 |
| `crossbeam-channel::bounded` | 阻塞/控制面有界 MPMC | 成熟、MIT/Apache-2.0；封装队列指标与 close 语义。 |
| `rtrb` | Simulation 热路径 SPSC ring | 无锁、固定容量、MIT；仅 `spsc.rs` 可见。 |
| `loom`（dev） | 并发模型检查 | Tokio 生态成熟测试工具；只进 dev-dependencies。 |

### G.3 明确拒绝的自研项
- 不自研 reactor、线程池、通用 timer wheel、取消 runtime、通用 channel。
- 只自研极薄的 supplier-neutral wrapper，因为必须强制 Queue Contract Matrix 元数据、SPSC owner 和禁止供应商类型泄漏。

## H. 测试面与 Fixture

- 单元：容量、FIFO、close、generation、permit 归还。
- 官方测试时钟：同 deadline 稳定次序、取消竞态、shutdown 不再投递。
- Loom：send/close、cancel/fire、producer drop/consumer drain。
- 压力：固定容量下 RSS 不随 producer 速率增长；深度和拒绝 metric 可观测。
- 故障：runner panic 必有 SupervisorEvent，且 join 不永久等待。

## I. 决策门与配置默认

- SRV-D 中所有容量只是 `PortSpec` 默认值来源；不得做 `const` 公共契约。
- Timer model 已固定为“到期投递命令”；任何模块自建 sleep/poll thread 由 policy test 拒绝。

## J. 本模块任务卡

| 任务 | Wave | 单一目标 | 依赖 |
| --- | --- | --- | --- |
| [`implement-host-runtime-bounded-ports`](../../../.spec/tasks/implement-host-runtime-bounded-ports.md) | Wave 2 | 以 crossbeam-channel/rtrb 封装 supplier-neutral 点到点端口，并强制 owner/producer/consumer/capacity/full/close 元数据。 | `consume-upstream-generated-contract-artifacts`, `add-lumio-host-testkit` |
| [`implement-host-runtime-clock-and-timer-delivery`](../../../.spec/tasks/implement-host-runtime-clock-and-timer-delivery.md) | Wave 3 | 使用 Tokio time/DelayQueue 实现可取消 timer，目标是 typed `TimerDeliveryPort`，不执行业务回调。 | `implement-host-runtime-bounded-ports` |
| [`implement-host-runtime-supervision-cancellation-and-join`](../../../.spec/tasks/implement-host-runtime-supervision-cancellation-and-join.md) | Wave 4 | 建立统一的 task/thread supervisor、CancellationScope、bounded executor permits、heartbeat 和 join barrier。 | `implement-host-runtime-clock-and-timer-delivery` |

## K. 完成定义

本模块的任务卡全部通过；模块单测/fixture/故障测试通过；Queue Registry、Cargo DAG、许可证与源码红线检查通过；没有供应商类型泄漏、无界队列、任意回调或未见证 FaultClass 推断。
