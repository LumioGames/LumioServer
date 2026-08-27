# LumioServer `process` 模块实现级设架

> 架构基线：`LGE-V1.2-2026-08-27`  
> 实现优先级：**P0**  
> crate：`lumio-server-process（同一 package：薄 `lib` + `lumio-server` binary）`  
> 本文只定义仓内实现结构；公共 Schema / ABI / ID / 状态机仍由 `LumioGameEngineArchitecture` 维护。

## A. 一句话职责与非职责

**职责：** 唯一 Composition Root：把已选 Host Profile、配置快照与各模块的具体类型化端口组装成一个受监督的服务端进程，并负责信号、进程级 Watchdog、崩溃证据、关闭顺序和退出码。

**明确不负责：**
- 不拥有 `WorldSlotHost`、`ServerConnectionSession`、连接注册表、Release Pool、Runtime/Voxel 权威状态。
- 不逐步编排 Tick、Session drain、Snapshot 或 durable commit；这些只通过对应 owner 的类型化命令触发。
- 不提供通用 `init/shutdown/health` 回调注册，不保存任意闭包，不充当 Service Locator。
- 不启动目标实例，不裁决集群 desired state；OS signal 只产生进程控制事实，关闭命令沿既有 `process -> world-slot` 边发送 `QuiesceForShutdown`。

## B. crate、目录与文件清单

建议 package 名：`lumio-server-process（同一 package：薄 `lib` + `lumio-server` binary）`。文件按所有权/职责切分，不创建 `common.rs`、`globals.rs`、`event_bus.rs` 或大锅文件。

| 文件 | 唯一职责 |
| --- | --- |
| `modules/process/Cargo.toml` | binary/lib、依赖和 lint 配置；不声明协议 feature。 |
| `modules/process/src/main.rs` | 只调用 `lumio_server_process::run_from_os()` 并把结果映射为 OS exit code。 |
| `modules/process/src/lib.rs` | 导出 `ProcessApplication`、`run_from_os` 和测试可见构造入口。 |
| `modules/process/src/application.rs` | `ProcessApplication` 状态机、start/run/stop/join 主流程。 |
| `modules/process/src/components.rs` | 具体 `Components` 与明确的构造/析构顺序；无 trait-object service registry。 |
| `modules/process/src/wiring.rs` | 把各模块的命令/事件端口逐条连线；每条连线对应总图已有边。 |
| `modules/process/src/config.rs` | 加载、合并、校验并冻结 `ProcessConfigSnapshot`。 |
| `modules/process/src/signals.rs` | Tokio signal adapter；只产出 `ProcessControlCommand::OsSignal`。 |
| `modules/process/src/watchdog.rs` | 进程级心跳评估；与 `world-slot` Slot Watchdog 完全分离。 |
| `modules/process/src/crash.rs` | panic hook、crash evidence 请求和最小 emergency path。 |
| `modules/process/src/shutdown.rs` | 类型化关闭计划与 join barrier；不窥探模块内部状态。 |
| `modules/process/src/exit.rs` | 私有 `ProcessExitCode` 与最终原因映射。 |
| `modules/process/src/error.rs` | bootstrap/config/wiring/join 错误；不重定义公共 `ErrorCode`。 |
| `modules/process/tests/startup_order_test.rs` | 构造与 readiness 顺序。 |
| `modules/process/tests/shutdown_order_test.rs` | 取消、drain、join 和 observability 终刷顺序。 |
| `modules/process/tests/panic_evidence_test.rs` | panic 后只走 crash-safe evidence 路径。 |

## C. 公开/内部类型、Trait、命令、事件与端口

### C.1 类型、命令与事件
- `ProcessApplication`、`ProcessLifecycle`、`ProcessConfigSnapshot`、`Components`、`ProcessReadiness`。
- `ProcessControlCommand::{OsSignal, LocalStopRequest, WatchdogEscalation, WorldSlotReadyToExit}`。
- `ProcessEvent::{Running, StopAccepted, JoinCompleted, Crashed}`。
- `ProcessExitCode` 为仓内 OS 适配类型，不导出为公共 ErrorCode。

### C.2 稳定仓内端口
| 接口 | 落点 | 契约 |
| --- | --- | --- |
| `ProcessApplication::start(config, plan, factories)` | `application.rs` | 按固定顺序构造组件；成功只返回 `RunningProcess`。 |
| `RunningProcess::request_stop(ProcessStopReason)` | `application.rs` | 幂等；只向 world-slot 投递 `QuiesceForShutdown`，不伪造外部维护命令。 |
| `RunningProcess::join()` | `application.rs` | 等待全部受监督执行单元终态，返回 `ProcessExitReport`。 |
| `ComponentFactories` | `components.rs` | 字段为具体命名工厂接口；不允许 Vec<dyn Service> 或闭包表。 |

### C.3 Rust 签名草案

以下签名是**仓内实现接口**，不是新增公共 ABI；实现时只能引用上游 generated 类型，不能复制公共字段。

```rust

pub fn run_from_os() -> ProcessExitCode;

impl ProcessApplication {
    pub fn start(
        config: ProcessConfigSnapshot,
        plan: ValidatedHostCompositionPlan,
        factories: ComponentFactories,
    ) -> Result<RunningProcess, ProcessStartError>;
}

impl RunningProcess {
    pub fn readiness(&self) -> ProcessReadiness;
    pub fn request_stop(&self, reason: ProcessStopReason) -> Result<WorldSlotQuiesceRequestAck, ProcessError>;
    pub fn join(self) -> Result<ProcessExitReport, ProcessJoinError>;
}

// 字段必须是具体、命名 factory；禁止 Vec<dyn Service>、闭包表或 Service Locator。
pub struct ComponentFactories {
    pub host_runtime: HostRuntimeFactory,
    pub observability: ObservabilityFactory,
    pub persistence: PersistenceFactory,
    pub coreclr: CoreClrFactory,
    pub transport: TransportFactory,
    pub control_plane: ControlPlaneFactory,
}
```

## D. 状态、资源与生命周期所有权

- `ProcessLifecycle`：`Constructing -> Starting -> Running -> StopRequested -> Joining -> Exited`；任一非终态可进入 `Crashed`。
- 一次启动的不可变 `ProcessConfigSnapshot`、`HostCompositionPlan`、组件句柄集合与启动/关闭证据。
- 进程级 Watchdog 状态、panic 摘要、OS 信号去重状态、最终 `ProcessExitCode`。
- 仅 Composition Root 可见的具体 `Components` 结构；模块内部状态永不复制到这里。

### D.1 模块红线
- OS signal 不能直接调用 session/pacing/persistence，也不能绕到 maintenance-agent；必须沿现有命令边进入 world-slot。
- 进程最终终态只有退出/`ReadyToExit` 证据消费，不存在 `TargetActivated`。

## E. 线程、队列、背压与关闭

### E.1 线程/执行上下文
- OS main thread 只负责 bootstrap、组装、等待终态与返回退出码。
- signal listener、watchdog evaluator 与 crash evidence trigger 都通过 `host-runtime` 创建和监督。
- 不得调用 `std::thread::spawn`、`tokio::spawn`、`sleep` 或轮询；必须经 `HostRuntimeHandle`。
- 关闭使用结构化取消与显式 join；没有 detached task。

### E.2 队列合同
| 队列 | 元素 | 所有者 | 生产者 | 消费者 | 顺序 | 容量配置 | 满载动作 | 关闭语义 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `ProcessControlInbox` | `ProcessControlCommand` | process | signal adapter / watchdog / world-slot event bridge | process runner | FIFO（重复 signal 合并） | `process.control.capacity` | 满载时写 emergency evidence 并转进程故障 | close 后拒绝新命令；drain 已入队命令 |
| `WatchdogHeartbeatInbox` | `HeartbeatSample` | host-runtime | 受监督线程 | process watchdog evaluator | per-source latest + epoch | `watchdog.heartbeat.capacity` | 丢弃同 source 旧样本；无法入队则判监督面失真 | 取消后停止接收并生成终态摘要 |

这些行必须写入仓库 Queue Contract Registry；数值来自配置快照，SRV-D 默认值不得提升为公共常量。

## F. 失败分类、恢复与显式 Ack

| 分类 | 实现要求 |
| --- | --- |
| 可拒绝 | 配置/schema/profile 不匹配；返回 `StartupRejected`，不启动 listener，不产生半活进程。 |
| 可重试 | 可选 sink 暂不可用；按 profile 的降级策略重试，但 Audit/恢复前置失败不得被降级。 |
| 进程终止 | 核心组件启动失败、监督线程 panic、join 超时或进程 Watchdog 失真；请求 Failure Bundle 后以非零退出。 |
| 边界规则 | Runtime `FaultClass` 由 world-slot 裁决；process 只消费已经裁决的 Process 级升级事件。 |

## G. 编译依赖、成熟方案与 Adapter 隔离

### G.1 编译依赖
**允许：**
- 全部 14 个可实现模块 crate（仅 Composition Root 允许）
- 三个 generated contract crates
- `clap`（CLI）、`config`/`serde`（配置）、`thiserror`

**禁止：**
- `protocol-dispatch`
- LumioGame/LumioClient 源码
- 第三方网络/日志供应商类型出现在 `ProcessApplication` 公共签名
- 任何全局 mutable singleton

### G.2 成熟方案选择
| 方案 | 用途 | 成熟性、许可证与隔离 |
| --- | --- | --- |
| `clap 4.6` | CLI 解析 | 活跃、MIT/Apache-2.0；仅 process adapter，解析结果转为仓内配置类型。 |
| `config 0.15` + `serde` + `jsonschema` | 分层配置装载与 Schema 校验 | 广泛使用、宽松许可证；供应商错误在 `config.rs` 归一化。 |
| `tokio::signal` | 跨平台进程信号 | 沿用 host-runtime 的 Tokio，不新增 signal 线程；仅 `signals.rs` 可见。 |
| `std::panic::set_hook` | panic hook | 官方标准 API；hook 只写预分配摘要并触发 crash-safe 命令。 |

### G.3 明确拒绝的自研项
- 不自研 CLI parser、配置合并器、signal reactor、panic 运行时或进程线程池。
- 不写通用生命周期框架；固定组件集合由 `components.rs` 明确编码，防止回调/Service Locator 漂移。

## H. 测试面与 Fixture

- 单元：生命周期合法转移、signal 去重、exit 映射、配置冻结。
- 集成：逐组件启动失败注入，验证逆序释放且 listener 永不提前开放。
- 故障：监督任务 panic、Watchdog 假死、Failure Bundle 超时、二次 SIGTERM。
- 属性：任意启动失败点后所有已构造组件恰好关闭一次。
- Reference Host：由 `lumio-host-testkit` 提供假的具体 factories，不由生产 crate 依赖 testkit。

## I. 决策门与配置默认

- D-001 仅作为 V1 组装默认，不在类型系统永久锁死多 Release 演进。
- D-010 未冻结时，RemoteDS profile 必须因缺生产 control channel 明确拒绝组装；LocalEmbedded 测试 profile 可注入测试通道。
- 进程 Watchdog 阈值必须有独立配置键，禁止复用 SRV-D-003 Slot Watchdog。

## J. 本模块任务卡

| 任务 | Wave | 单一目标 | 依赖 |
| --- | --- | --- | --- |
| [`implement-process-config-lifecycle-and-explicit-components`](../../../.spec/tasks/implement-process-config-lifecycle-and-explicit-components.md) | Wave 4 | 建立配置合并/schema校验、ProcessLifecycle和具体Components/Factories结构，禁止通用hook/service locator。 | `implement-host-profile-resolution-and-capability-matching`, `implement-observability-diagnostic-metrics-trace-pipeline`, `implement-coreclr-generated-abi-contract-facade`, `implement-release-catalog-manifest-verification` |
| [`implement-process-signal-watchdog-and-crash-evidence`](../../../.spec/tasks/implement-process-signal-watchdog-and-crash-evidence.md) | Wave 6 | 通过host-runtime监督signal/watchdog，安装最小panic hook并请求Failure Bundle，不直接调用领域模块。 | `implement-process-config-lifecycle-and-explicit-components`, `implement-host-runtime-supervision-cancellation-and-join`, `implement-observability-failure-bundle-and-emergency-path` |
| [`assemble-process-startup-readiness-maintenance-and-shutdown`](../../../.spec/tasks/assemble-process-startup-readiness-maintenance-and-shutdown.md) | Wave 12 | 在wiring中连接所有具体typed ports，落实恢复前置、listener/admission开放门、maintenance ReadyToExit和逆序join。 | `implement-maintenance-orchestration-and-dual-durable-ack`, `implement-process-signal-watchdog-and-crash-evidence`, `implement-world-slot-resource-and-watchdog-soak` |

## K. 完成定义

本模块的任务卡全部通过；模块单测/fixture/故障测试通过；Queue Registry、Cargo DAG、许可证与源码红线检查通过；没有供应商类型泄漏、无界队列、任意回调或未见证 FaultClass 推断。
