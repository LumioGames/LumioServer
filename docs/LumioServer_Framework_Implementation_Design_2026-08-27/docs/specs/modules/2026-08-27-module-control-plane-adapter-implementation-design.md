# LumioServer `control-plane-adapter` 模块实现级设架

> 架构基线：`LGE-V1.2-2026-08-27`  
> 实现优先级：**P1**  
> crate：`lumio-control-plane-adapter`  
> 本文只定义仓内实现结构；公共 Schema / ABI / ID / 状态机仍由 `LumioGameEngineArchitecture` 维护。

## A. 一句话职责与非职责

**职责：** 把外部运维输入隔离为“通道 adapter → 认证/签名验证 → fencing/idempotency → typed control command”，并上报本地 readiness/drain/exit evidence；不拥有 desired state。

**明确不负责：**
- D-010 冻结前不选择生产通道、wire framing、签名算法或重放格式。
- 不拥有 Maintenance 状态机、Release Pool、实例替换或目标激活。
- 不接受未验证命令进入 maintenance-agent，不把 vendor SDK 类型泄漏。
- 不存储 secret/key，不记录 credential/signature 原文。

## B. crate、目录与文件清单

建议 package 名：`lumio-control-plane-adapter`。文件按所有权/职责切分，不创建 `common.rs`、`globals.rs`、`event_bus.rs` 或大锅文件。

| 文件 | 唯一职责 |
| --- | --- |
| `modules/control-plane-adapter/Cargo.toml` | foundations + generated commands；无生产 channel vendor。 |
| `modules/control-plane-adapter/src/lib.rs` | 导出 behavior core/SPI。 |
| `modules/control-plane-adapter/src/frame.rs` | opaque unverified frame，禁止 Debug secret content。 |
| `modules/control-plane-adapter/src/authenticator.rs` | `ControlCommandAuthenticator` SPI；D-010 后选择算法。 |
| `modules/control-plane-adapter/src/fencing.rs` | monotonic fencing token/sequence validation。 |
| `modules/control-plane-adapter/src/idempotency.rs` | bounded commandId result cache。 |
| `modules/control-plane-adapter/src/commands.rs` | VerifiedControlCommand 与 maintenance mapping。 |
| `modules/control-plane-adapter/src/reports.rs` | LocalStatusReport/ReadyToExitEvidence。 |
| `modules/control-plane-adapter/src/channel.rs` | `ControlChannel` SPI，无 vendor 类型。 |
| `modules/control-plane-adapter/src/service.rs` | serial verify/reduce/report runner。 |
| `modules/control-plane-adapter/src/adapters/injected.rs` | 测试 channel；直接送 opaque frames，仍走 authenticator。 |
| `modules/control-plane-adapter/src/adapters/production_gate.rs` | 只报告 `ProductionChannelUnavailableUntilD010`，不伪造实现。 |
| `modules/control-plane-adapter/src/error.rs` | auth/fence/duplicate/channel/backpressure。 |
| `modules/control-plane-adapter/tests/fencing_test.rs` | 旧 token/乱序/duplicate。 |
| `modules/control-plane-adapter/tests/verification_order_test.rs` | 未验证命令永不到 maintenance。 |
| `modules/control-plane-adapter/tests/report_delivery_test.rs` | ReadyToExit 不被 health coalesce。 |

## C. 公开/内部类型、Trait、命令、事件与端口

### C.1 类型、命令与事件
- `UnverifiedControlFrame`、`VerifiedControlCommand`、`FencingToken`、`ControlCommandId`。
- `ControlCommand::{Maintenance(generated MaintenanceCommand), QueryLocalStatus}`；不得添加 wire RPC。
- `LocalStatusReport`、`ReadyToExitEvidence`、`ControlChannelHealth`。
- `ControlCommandAuthenticator`、`ControlChannel` 为 adapter SPI。

### C.2 稳定仓内端口
| 接口 | 落点 | 契约 |
| --- | --- | --- |
| `ControlIngressPort::try_send(UnverifiedControlFrame)` | `frame.rs` | channel 唯一输入。 |
| `VerifiedControlPort::try_recv()` | `commands.rs` | maintenance-agent 消费，只见 verified command。 |
| `StatusReportPort::try_send(LocalStatusReport)` | `reports.rs` | process/release/maintenance 生产。 |
| `ControlCommandAuthenticator::verify(frame)` | `authenticator.rs` | D-010 后 adapter；返回 normalized identity/fence，绝不返回 key。 |
| `ControlChannel::{poll_frame,try_send_report}` | `channel.rs` | supplier-neutral nonblocking SPI。 |

### C.3 Rust 签名草案

以下签名是**仓内实现接口**，不是新增公共 ABI；实现时只能引用上游 generated 类型，不能复制公共字段。

```rust
pub trait ControlCommandAuthenticator: Send {
    fn verify(
        &mut self,
        frame: &Secret<UnverifiedControlFrame>,
    ) -> Result<AuthenticatedControlCommand, ControlAuthenticationError>;
}

pub trait ControlChannel: Send {
    fn poll_frame(&mut self) -> Result<ControlPoll, ControlChannelError>;
    fn try_send_report(&mut self, report: &LocalStatusReport) -> Result<ReportSend, ControlChannelError>;
}

impl ControlPlaneService {
    pub fn reduce(
        &mut self,
        input: ControlPlaneInput,
    ) -> Result<ControlPlaneEffects, ControlPlaneError>;
}
```

## D. 状态、资源与生命周期所有权

- 控制输入 bounded queue、command authenticity result、fencing token/sequence、idempotency index。
- 本进程 report sequence、delivery retry state 与 channel health。
- `VerifiedControlCommand` 与 `LocalStatusReport` supplier-neutral values。
- 生产 channel availability capability；D-010 未决时 RemoteDS composition 明确失败。

### D.1 模块红线
- 本模块不拥有 cluster desired state。
- 不存在由旧进程执行的目标实例激活。

## E. 线程、队列、背压与关闭

### E.1 线程/执行上下文
- behavior core 是 serial runner；channel poll/stream task 由 host-runtime监督。
- D-010 前仅提供 dev/test injected channel，不创建生产网络线程。
- 重试/backoff 通过 TimerCommand；不 sleep/busy poll。
- 验证完成后才入 maintenance queue；迟到/旧 fencing 拒绝。

### E.2 队列合同
| 队列 | 元素 | 所有者 | 生产者 | 消费者 | 顺序 | 容量配置 | 满载动作 | 关闭语义 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `ControlIngressQueue` | `UnverifiedControlFrame` | control-plane-adapter | channel adapter | verifier runner | FIFO by channel sequence | `control_plane.ingress.capacity` | 拒绝/断开 channel；不挤掉已验命令 | channel close 后完成已入队验证 |
| `VerifiedControlQueue` | `VerifiedControlCommand` | maintenance-agent | verifier runner | maintenance | command/fencing sequence | `control_plane.verified.capacity` | 返回 backpressure，保持 command id 以重试 | shutdown drain critical commands |
| `StatusReportQueue` | `LocalStatusReport` | control-plane-adapter | process/release/maintenance | channel sender | sequence FIFO, latest health may coalesce | `control_plane.report.capacity` | 健康可合并；ReadyToExit evidence 不可丢 | bounded final flush then evidence of failure |

这些行必须写入仓库 Queue Contract Registry；数值来自配置快照，SRV-D 默认值不得提升为公共常量。

## F. 失败分类、恢复与显式 Ack

| 分类 | 实现要求 |
| --- | --- |
| 可拒绝 | 坏签名/身份、旧 fencing、duplicate conflicting payload、scope 不可解析；审计拒绝。 |
| 可重试 | channel/reporter 暂不可用、verified queue full；同 command id 有界重试。 |
| 本地降级 | 健康 report 丢失可进入 disconnected 状态；不改变本地权威 lifecycle。 |
| 进程配置失败 | RemoteDS 要求生产 channel 而 D-010/adapter 未满足；process 在开放 listener 前拒绝启动。 |

## G. 编译依赖、成熟方案与 Adapter 隔离

### G.1 编译依赖
**允许：**
- host-runtime
- observability
- host-profiles
- generated MaintenanceCommand/IDs
- `secrecy`/`zeroize` if key handle wrapper

**禁止：**
- maintenance/release/process implementation reverse dependency
- Kubernetes/cloud SDK type in public API
- 自定义 signature/wire
- TargetActivated

### G.2 成熟方案选择
| 方案 | 用途 | 成熟性、许可证与隔离 |
| --- | --- | --- |
| 生产通道：未选 | D-010 尚未冻结 | 不以“成熟方案优先”为借口提前私造通道；门冻结后从 gRPC/HTTP/queue 等成熟方案中选并封 adapter。 |
| `secrecy`/`zeroize` | 验证器 key handle | 只用于 adapter secret handle；不存入 state/report。 |
| host-runtime backoff/timer | poll/retry | 不自研线程/睡眠。 |

### G.3 明确拒绝的自研项
- 不自研签名算法、控制协议、service discovery、orchestrator、持久消息总线。
- 当前只实现供应商无关的 verify/fence/idempotency 行为与 injected test channel，因为这是 D-010 允许冻结的行为层。

## H. 测试面与 Fixture

- 行为：invalid auth、old fence、duplicate same/different payload、queue full。
- 安全：frame/signature/key 不出日志/Debug/bundle。
- 顺序：只有 verify→fence→idempotency 全部成功才输出 typed command。
- 报告：progress 可合并，ReadyToExit evidence 必须显式 ack 或失败。
- Composition：RemoteDS + production channel unavailable 必须启动拒绝；LocalEmbedded test channel可运行。

## I. 决策门与配置默认

- D-010 是生产 channel/wire/签名选择硬阻塞；task 不得越过。
- 控制命令公共字段只来自架构源；adapter 不能添加本地 wire fields。

## J. 本模块任务卡

| 任务 | Wave | 单一目标 | 依赖 |
| --- | --- | --- | --- |
| [`implement-control-plane-behavior-core`](../../../.spec/tasks/implement-control-plane-behavior-core.md) | Wave 5 | 在不选择D-010通道/wire/算法的前提下，定义opaque frame、authenticator SPI、fencing/idempotency和verified typed output。 | `implement-host-runtime-bounded-ports`, `implement-observability-audit-durable-pipeline`, `consume-upstream-generated-contract-artifacts` |
| [`implement-control-plane-injected-channel-and-status-reporting`](../../../.spec/tasks/implement-control-plane-injected-channel-and-status-reporting.md) | Wave 6 | 提供测试专用injected channel、bounded status queue、report coalescing与ReadyToExit不可丢语义。 | `implement-control-plane-behavior-core`, `implement-host-profile-fault-decorator-declarations`, `implement-host-runtime-clock-and-timer-delivery` |

## K. 完成定义

本模块的任务卡全部通过；模块单测/fixture/故障测试通过；Queue Registry、Cargo DAG、许可证与源码红线检查通过；没有供应商类型泄漏、无界队列、任意回调或未见证 FaultClass 推断。
