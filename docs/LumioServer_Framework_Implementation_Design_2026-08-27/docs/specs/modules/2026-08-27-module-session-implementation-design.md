# LumioServer `session` 模块实现级设架

> 架构基线：`LGE-V1.2-2026-08-27`  
> 实现优先级：**P0**  
> crate：`lumio-session`  
> 本文只定义仓内实现结构；公共 Schema / ABI / ID / 状态机仍由 `LumioGameEngineArchitecture` 维护。

## A. 一句话职责与非职责

**职责：** 拥有每条服务端连接的 `ServerConnectionSession`、认证/Release/Slot 绑定事务、重连窗口和 opaque Runtime replication handle；执行接纳而不拥有 Host Admission Gate。

**明确不负责：**
- 不写 ConnectionRegistry，不拥有 transport carrier、TLS、Ingress/Egress storage。
- 不拥有 WorldSlotHost 生命周期、Admission Gate、Runtime ReplicationContext 语义或 Client replica 状态。
- 不把 `ServerConnectionSession` 命名/映射为 `ClientReplicaSession`。
- 不定义 auth credential wire、复制 messageType 或 Gameplay dispatch。

## B. crate、目录与文件清单

建议 package 名：`lumio-session`。文件按所有权/职责切分，不创建 `common.rs`、`globals.rs`、`event_bus.rs` 或大锅文件。

| 文件 | 唯一职责 |
| --- | --- |
| `modules/session/Cargo.toml` | 依赖 transport/auth/release-agent/world-slot + host foundations。 |
| `modules/session/src/lib.rs` | 导出 Server 端 session 行为 API。 |
| `modules/session/src/id.rs` | `ServerSessionId`/epoch 的 generated ID wrapper。 |
| `modules/session/src/state.rs` | `ServerConnectionSessionState` 合法转移。 |
| `modules/session/src/session.rs` | `ServerConnectionSession` 聚合对象。 |
| `modules/session/src/registry.rs` | 单写 session registry 与索引。 |
| `modules/session/src/admission.rs` | 认证→release→slot→transport bind 的 saga。 |
| `modules/session/src/reconnect.rs` | window/token/timer generation 与资源预算。 |
| `modules/session/src/binding.rs` | connection/session/slot/opaque replication handle 绑定。 |
| `modules/session/src/commands.rs` | 连接候选、drain、kick、fault、timer typed commands。 |
| `modules/session/src/events.rs` | admitted/rejected/disconnected/reconnected/drained/faulted。 |
| `modules/session/src/service.rs` | serial runner 与 dependency event reducer。 |
| `modules/session/src/error.rs` | admission/reconnect/stale epoch/port errors。 |
| `modules/session/tests/admission_saga_test.rs` | 每一步失败与补偿。 |
| `modules/session/tests/reconnect_race_test.rs` | timer/close/new connection 竞态。 |
| `modules/session/tests/server_name_guard_test.rs` | 禁止 ClientReplicaSession token。 |

## C. 公开/内部类型、Trait、命令、事件与端口

### C.1 类型、命令与事件
- `ServerConnectionSession`、`ServerConnectionSessionState`、`SessionEpoch`。
- `AdmissionAttemptId`、`AdmissionReservation`、`SlotAssociation`、`ReplicationContextHandle`（opaque）。
- `SessionCommand::{ConnectionCandidate, DependencyResult, BeginDrain, Kick, TimerFired, SlotFaulted}`。
- `SessionEvent::{Admitted, Rejected, Disconnected, Reconnected, Drained, Kicked, Faulted}`。

### C.2 稳定仓内端口
| 接口 | 落点 | 契约 |
| --- | --- | --- |
| `SessionCommandPort::try_send(SessionCommand)` | `commands.rs` | 所有跨模块输入唯一入口；命令必须含 session/connection/attempt epoch。 |
| `SessionEventPort::try_recv()` | `events.rs` | maintenance/world-slot/process 消费显式事件。 |
| `SessionQueryPort::snapshot(session_id)` | `service.rs` | 返回不可变 view；不暴露 mutable registry。 |
| `AdmissionReducer::advance(event)` | `admission.rs` | 纯状态机；只生成下一条 typed effect，不直接调用依赖。 |

### C.3 Rust 签名草案

以下签名是**仓内实现接口**，不是新增公共 ABI；实现时只能引用上游 generated 类型，不能复制公共字段。

```rust
impl SessionCommandPort {
    pub fn try_send(&self, command: SessionCommand) -> Result<(), SessionPortError>;
}

impl SessionService {
    pub fn reduce(&mut self, input: SessionInput) -> Result<SessionEffects, SessionError>;
    pub fn snapshot(&self, session_id: ServerSessionId) -> Option<ServerConnectionSessionView>;
}

impl AdmissionReducer {
    pub fn advance(
        state: AdmissionState,
        input: AdmissionInput,
    ) -> Result<(AdmissionState, AdmissionEffects), AdmissionError>;
}

// 该类型名称固定；禁止别名为 ClientReplicaSession。
pub struct ServerConnectionSession {
    pub session_id: ServerSessionId,
    pub session_epoch: SessionEpoch,
    pub state: ServerConnectionSessionState,
    pub binding: Option<SessionBinding>,
    pub replication_context: Option<ReplicationContextHandle>,
}
```

## D. 状态、资源与生命周期所有权

- `SessionRegistry` 与每个 `ServerConnectionSession` 私有状态、session epoch、connection binding。
- 接纳事务的 pending steps/compensation、exact release pin、slot association。
- 重连 deadline/token、last connection epoch、opaque `ReplicationContextHandle`。
- Session 级故障隔离与 drain/kick 进度；Host gate 只读消费自 world-slot。

### D.1 模块红线
- 所有旧 connection/auth completion 必须以 connection/session epoch 拒绝。
- session 不拥有 Admission 开关；只能请求 world-slot reservation。

## E. 线程、队列、背压与关闭

### E.1 线程/执行上下文
- session serial runner 运行在 host-runtime 有界 executor；所有 session 状态单写。
- transport/auth/release/world-slot completion 都先进入 `SessionControlInbox`。
- 重连过期由 host-runtime TimerFired 投递，不自建 sleep/thread。
- Simulation Owner Thread 不修改 session registry；只通过 world-slot/session events 交换 association 结果。

### E.2 队列合同
| 队列 | 元素 | 所有者 | 生产者 | 消费者 | 顺序 | 容量配置 | 满载动作 | 关闭语义 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `SessionControlInbox` | `SessionCommand | dependency event` | session | transport/auth/release/world-slot/maintenance/timer | session runner | FIFO per session/connection epoch | `session.control.capacity` | 握手前满载关闭连接；活动 session 满载隔离该 session | shutdown 后处理 close/ack，拒绝新 admission |
| `SessionEventOutbox` | `SessionEvent` | 下游 consumer | session runner | world-slot/maintenance/process | FIFO per session epoch | `session.event.capacity` | 关键终态保留槽；无法交付则隔离 session 并发 diagnostic | 终态 ack drain 后关闭 |

这些行必须写入仓库 Queue Contract Registry；数值来自配置快照，SRV-D 默认值不得提升为公共常量。

## F. 失败分类、恢复与显式 Ack

| 分类 | 实现要求 |
| --- | --- |
| 可拒绝 | auth/release/slot gate/queue 不满足；发送架构允许的拒绝/错误并释放 reservation。 |
| 可重试 | 短暂 AuthBusy、world admission busy；attempt id 不变且重试有界。 |
| Session 级 | stale connection epoch、重连冲突、该 session ingress/egress 持续饱和；只 fault/close 该 session。 |
| Slot/Process | 仅消费 Runtime witness 或已裁决 world-slot event；session 不从异常可捕获性推断 FaultClass。 |

## G. 编译依赖、成熟方案与 Adapter 隔离

### G.1 编译依赖
**允许：**
- transport
- auth
- release-agent
- world-slot
- host-runtime
- observability
- host-profiles
- generated IDs

**禁止：**
- coreclr-host/pacing/persistence-host 直接依赖
- Client implementation/type
- protocol-dispatch
- ConnectionRegistry mutable reference

### G.2 成熟方案选择
| 方案 | 用途 | 成熟性、许可证与隔离 |
| --- | --- | --- |
| `slotmap`（可选内部） | generation-aware registry | 成熟、MIT/Apache-2.0；若 generated ID 足够则不引入，供应商 key 不导出。 |
| `smallvec`（可选） | 固定少量 pending effects | 成熟、宽松；仅 admission reducer 内部。 |
| host-runtime Timer/ports | 重连与 serial inbox | 复用统一成熟原语，不自建 scheduler。 |

### G.3 明确拒绝的自研项
- 不自研 actor runtime、event sourcing framework、全局 session bus。
- 只实现显式 admission saga，因为跨 auth/release/world/transport 的 compensation 与 epoch 是本域硬约束，通用工作流引擎会泄漏回调/无界状态。

## H. 测试面与 Fixture

- 单元：ServerConnectionSession 状态机与非法转移。
- Saga：auth/release/reservation/bind 每个失败点恰好补偿一次。
- Property：任意事件序列下一个 session 至多绑定一个 connection/slot epoch。
- 竞态：disconnect、reconnect、timer fire、maintenance kick、slot fault。
- LocalEmbedded：仍走 handshake envelope→auth→permission→session inbox→slot reservation。

## I. 决策门与配置默认

- SRV-D-004 重连窗口/预算仅配置默认；Timer owner 固定为 host-runtime，session 消费 generation。
- D-001 exact release 是当前 policy，不把 `ServerConnectionSession` 结构永久限制为单 release。
- D-011 未冻结时只把 Handshake body 作为 opaque credential 输入。

## J. 本模块任务卡

| 任务 | Wave | 单一目标 | 依赖 |
| --- | --- | --- | --- |
| [`implement-session-registry-state-and-admission-saga`](../../../.spec/tasks/implement-session-registry-state-and-admission-saga.md) | Wave 7 | 建立单写SessionRegistry及transport candidate→auth→exact release→slot reservation→transport bind的显式effect/compensation链。 | `implement-auth-replay-grant-revocation-and-epoch`, `implement-release-local-member-state-health-and-reporting`, `implement-world-slot-aggregate-epoch-admission-and-quota`, `implement-transport-local-embedded-fidelity-adapter` |
| [`implement-session-reconnect-window-and-epoch-races`](../../../.spec/tasks/implement-session-reconnect-window-and-epoch-races.md) | Wave 8 | 为断开Session保留有界metadata/opaque handle，使用host-runtime timer并处理disconnect/reconnect/expiry/kick竞态。 | `implement-session-registry-state-and-admission-saga`, `implement-host-runtime-clock-and-timer-delivery` |
| [`implement-session-drain-kick-and-fault-isolation`](../../../.spec/tasks/implement-session-drain-kick-and-fault-isolation.md) | Wave 10 | 消费maintenance/world-slot命令，停止新接纳、drain/close连接并保证单Session故障不污染其他Session。 | `implement-session-reconnect-window-and-epoch-races`, `implement-world-slot-resource-and-watchdog-soak` |

## K. 完成定义

本模块的任务卡全部通过；模块单测/fixture/故障测试通过；Queue Registry、Cargo DAG、许可证与源码红线检查通过；没有供应商类型泄漏、无界队列、任意回调或未见证 FaultClass 推断。
