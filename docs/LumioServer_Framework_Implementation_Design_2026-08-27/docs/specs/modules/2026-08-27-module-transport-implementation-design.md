# LumioServer `transport` 模块实现级设架

> 架构基线：`LGE-V1.2-2026-08-27`  
> 实现优先级：**P0**  
> crate：`lumio-transport`  
> 本文只定义仓内实现结构；公共 Schema / ABI / ID / 状态机仍由 `LumioGameEngineArchitecture` 维护。

## A. 一句话职责与非职责

**职责：** 拥有连接承载、ConnectionRegistry、公共 Envelope/Codec 校验链、可靠性/分片、限流以及每连接有界 Ingress/Egress；只把验证后的字节与连接事件交给上层。

**明确不负责：**
- 不认证凭据、不裁决 permission、不创建 `ServerConnectionSession`、不调用 Gameplay/Runtime。
- 不拥有 RPC/Message handler registry；`protocol-dispatch` 解锁前只接受架构源已有复制 Envelope MessageTypes。
- LocalEmbedded 只替换 byte carrier，不跳过 Schema、Codec、Envelope、大小、权限引用、队列或 Tick 交付。
- 不把 Quinn/rustls/socket 类型暴露到稳定 API。

## B. crate、目录与文件清单

建议 package 名：`lumio-transport`。文件按所有权/职责切分，不创建 `common.rs`、`globals.rs`、`event_bus.rs` 或大锅文件。

| 文件 | 唯一职责 |
| --- | --- |
| `modules/transport/Cargo.toml` | vendor-neutral core；Remote adapter 依赖作为私有可选实现，不改变公开类型。 |
| `modules/transport/src/lib.rs` | 导出连接/队列/命令/事件的 supplier-neutral API。 |
| `modules/transport/src/endpoint.rs` | endpoint lifecycle 与 carrier factory。 |
| `modules/transport/src/connection.rs` | 连接状态、epoch、预算和 close reason。 |
| `modules/transport/src/registry.rs` | 唯一可变 ConnectionRegistry owner。 |
| `modules/transport/src/envelope.rs` | generated Envelope 借用、大小/版本/messageType gate。 |
| `modules/transport/src/codec.rs` | `EnvelopeCodec` adapter；生产 codec 必须由 D-004 选择。 |
| `modules/transport/src/permission.rs` | transport-owned opaque `PermissionGrantRef`，不依赖 auth crate。 |
| `modules/transport/src/rate_limit.rs` | `governor` adapter 与 per-connection/key budgets。 |
| `modules/transport/src/reliability.rs` | 复制 Envelope 的 ack/order 元数据；不定义新 messageType。 |
| `modules/transport/src/fragment.rs` | bounded reassembly 与超限拒绝。 |
| `modules/transport/src/ingress.rs` | per-connection ingress queue。 |
| `modules/transport/src/egress.rs` | per-connection egress queue与 bounded drain。 |
| `modules/transport/src/commands.rs` | `ConnectionCommand` 与显式 ack。 |
| `modules/transport/src/events.rs` | `ConnectionEvent`、`IngressAvailable`、backpressure/fault evidence。 |
| `modules/transport/src/ports.rs` | command/event/ingress/egress 端口。 |
| `modules/transport/src/runner.rs` | registry/carrier 控制 runner。 |
| `modules/transport/src/adapters/local_embedded.rs` | 内存 byte carrier；完整校验链。 |
| `modules/transport/src/adapters/remote.rs` | Quinn/rustls adapter，仅在 D-004 选型满足时进入生产 composition。 |
| `modules/transport/src/adapters/fault_decorator.rs` | 确定性丢包/延迟/重复/重排；仍经有界队列。 |
| `modules/transport/src/error.rs` | carrier/codec/limit/queue/epoch 错误归一化。 |
| `modules/transport/tests/registry_owner_test.rs` | 只有 transport 命令可改 registry。 |
| `modules/transport/tests/local_embedded_fidelity_test.rs` | 与 remote byte path 的 contract parity。 |
| `modules/transport/tests/bounded_backpressure_test.rs` | 容量、优先级、close drain。 |
| `modules/transport/tests/envelope_fixture_test.rs` | 架构源正反 fixture。 |

## C. 公开/内部类型、Trait、命令、事件与端口

### C.1 类型、命令与事件
- `TransportService`、`TransportConnectionId`、`ConnectionEpoch`、`ConnectionView`。
- `ValidatedEnvelopeBytes`、`OutboundEnvelopeBytes`、`EnvelopeMetadata`。
- `PermissionGrantRef` 是 transport-owned 不可变引用值；session 从 auth 结果复制必要 ID，不传 auth 类型。
- `ConnectionCommand::{Bind, Unbind, Close, SetDrain, EnqueueControlEnvelope}`。
- `ConnectionEvent::{Accepted, HandshakeEnvelope, IngressReady, Backpressured, Closed, Faulted}`。

### C.2 稳定仓内端口
| 接口 | 落点 | 契约 |
| --- | --- | --- |
| `TransportControlPort::try_send(ConnectionCommand)` | `ports.rs` | 所有 registry 写操作唯一入口；命令携带 connection epoch + ack id。 |
| `TransportEventPort::try_recv()` | `ports.rs` | session/release/maintenance 消费 typed event，不发生反向调用。 |
| `IngressReader::drain(connection, max_items, max_bytes)` | `ingress.rs` | 仅 owner thread 调用；返回验证后的 envelope bytes。 |
| `EgressWriter::try_enqueue(connection, OutboundEnvelopeBytes)` | `egress.rs` | 非阻塞；满载显式返回。 |
| `EnvelopeCodec::{decode,encode}` | `codec.rs` | adapter 内部 trait；输入/输出仓内 bytes，供应商类型不泄漏。 |
| `ByteCarrier` | `endpoint.rs` | 仅 adapter SPI；local/remote 都必须把 bytes 交回同一 codec runner。 |

### C.3 Rust 签名草案

以下签名是**仓内实现接口**，不是新增公共 ABI；实现时只能引用上游 generated 类型，不能复制公共字段。

```rust
impl TransportControlPort {
    pub fn try_send(&self, command: ConnectionCommand) -> Result<(), TransportPortError>;
}

impl IngressReader {
    pub fn drain(
        &mut self,
        connection_id: TransportConnectionId,
        limits: DrainLimits,
    ) -> Result<IngressBatch, IngressReadError>;
}

impl EgressWriter {
    pub fn try_enqueue(
        &self,
        connection_id: TransportConnectionId,
        envelope: OutboundEnvelopeBytes,
    ) -> Result<EgressEnqueueAck, EgressError>;
}

pub(crate) trait EnvelopeCodec: Send {
    fn decode(&mut self, bytes: &[u8]) -> Result<DecodedEnvelope, CodecError>;
    fn encode(&mut self, envelope: &GeneratedReplicationEnvelope) -> Result<EncodedBytes, CodecError>;
}

pub(crate) trait ByteCarrier: Send {
    fn try_receive(&mut self, budget: ByteBudget) -> Result<CarrierReceive, CarrierError>;
    fn try_send(&mut self, bytes: &[u8]) -> Result<CarrierSend, CarrierError>;
    fn close(&mut self, reason: TransportCloseReason) -> Result<(), CarrierError>;
}
```

## D. 状态、资源与生命周期所有权

- `ConnectionRegistry`、`TransportConnectionId`、connection epoch、carrier state 与 send/receive budgets。
- 每连接 Ingress/Egress ring、fragment/reassembly 状态、reliability ack 状态和 rate limiter。
- Envelope decode/validate 结果与大小/协议版本拒绝证据；不拥有 payload 业务语义。
- 绑定到连接记录的不可变 `PermissionGrantRef`，但 grant 内容与裁决归 auth。

### D.1 模块红线
- transport 绝不依赖 auth；`PermissionGrantRef` 是 transport 自有 opaque value。
- 网络线程不得调用 Gameplay；只有 world-slot 在 Tick Barrier 消费 ingress。

## E. 线程、队列、背压与关闭

### E.1 线程/执行上下文
- Remote carrier 的 reactor/send workers 经 host-runtime 监督；连接固定 affinity 由配置和 D-004 结果决定。
- LocalEmbedded adapter 不创建 OS 网络线程，仍经相同 byte ingress/codec/queue runner。
- Simulation Owner Thread 只非阻塞 drain ingress / enqueue egress；绝不等待 socket/TLS。
- ConnectionRegistry 只有 transport runner 可写；session 只能发送 `ConnectionCommand`。

### E.2 队列合同
| 队列 | 元素 | 所有者 | 生产者 | 消费者 | 顺序 | 容量配置 | 满载动作 | 关闭语义 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `ConnectionIngressQueue` | `ValidatedEnvelopeBytes` | transport | carrier/codec runner | world-slot owner thread | per-connection FIFO | `transport.ingress.capacity.items/bytes` | 拒绝新 frame、计数；按可靠性策略断开，绝不覆盖 | 连接关闭后 drain/丢弃按命令固定 |
| `ConnectionEgressQueue` | `OutboundEnvelopeBytes` | transport | world-slot owner thread | send worker/carrier | per-connection FIFO + reliability class | `transport.egress.capacity.items/bytes` | 返回 `EgressBackpressured`；不得无限缓存 | close 可选择 bounded drain，超时后丢弃并记录 |
| `ConnectionCommandQueue` | `ConnectionCommand` | transport | session/release/maintenance | registry runner | FIFO per connection epoch | `transport.command.capacity` | 拒绝并返回 ack；关键关闭命令走保留槽 | 关闭后只接受幂等 close 查询 |

这些行必须写入仓库 Queue Contract Registry；数值来自配置快照，SRV-D 默认值不得提升为公共常量。

## F. 失败分类、恢复与显式 Ack

| 分类 | 实现要求 |
| --- | --- |
| 可拒绝 | 未知 protocolVersion/messageType、超大小、坏 fragment、stale connection epoch；关闭或返回协议错误 Envelope（仅架构已有类型）。 |
| 可重试 | egress/command port 满；调用方按矩阵重试或断开，transport 不无限等待。 |
| 连接级 | TLS/QUIC/socket、重放速率、可靠队列耗尽；只关闭连接并发事件。 |
| 进程级 | registry runner 或 reactor supervisor 崩溃；由 host-runtime 升级，不据此伪造 Runtime FaultClass。 |

## G. 编译依赖、成熟方案与 Adapter 隔离

### G.1 编译依赖
**允许：**
- generated architecture contracts
- host-runtime
- observability
- host-profiles
- `bytes`、`governor`；Remote adapter 私有 `quinn`/`rustls`

**禁止：**
- auth/session/world-slot/Gameplay/Runtime crate
- protocol-dispatch
- 第三方 carrier 类型进入 `lib.rs` API
- unbounded channel

### G.2 成熟方案选择
| 方案 | 用途 | 成熟性、许可证与隔离 |
| --- | --- | --- |
| `quinn 0.11` | RemoteDS QUIC carrier 候选 | 成熟、MIT/Apache-2.0；D-004 未确认时只作为隔离 adapter，不冻结 wire。 |
| `rustls 0.23` | TLS | 广泛部署、宽松许可证；证书/key 类型只在 remote adapter。 |
| `governor 0.10` | 限流 | 成熟 GCRA 实现、MIT；包装为连接预算，参数来自配置。 |
| `bytes` | 共享 byte buffer | Tokio 生态成熟；只作为私有 storage，公开值用仓内 newtype。 |
| `serde`/生成 codec | Envelope/fixture adapter | 只消费架构源生成类型，不手写第二套 Schema。 |

### G.3 明确拒绝的自研项
- 不自研 TLS、QUIC、reactor、通用限流器或无界重传缓存。
- 自研仅限有界 connection registry/queue glue、因为必须满足 per-connection ownership、SPSC、LocalEmbedded 保真与架构 Envelope。

## H. 测试面与 Fixture

- Golden：全部 replication-envelope 正反 fixtures 与字段 camelCase。
- Property：任意 bytes 不 panic；decode→encode 不改变规范化 Envelope；reassembly 总内存有界。
- 故障：丢包/重复/重排/TLS 失败/egress 饱和/close race。
- 一致性：同一 frame 经 LocalEmbedded 与 Remote adapter 到达相同 `ValidatedEnvelopeBytes`。
- 性能：固定 workload 下 per-connection queue depth、copy bytes、p50/p95/p99/max。

## I. 决策门与配置默认

- D-004 冻结前，不把 Quinn、QUIC、压缩或具体 codec 写入公共契约；Remote adapter 的生产启用依赖该门。
- SRV-D-001/002/006 数字仅配置默认和 benchmark 输入。
- D-009 未冻结：禁止 handler registry、RPC correlation、cancel/deadline wire。

## J. 本模块任务卡

| 任务 | Wave | 单一目标 | 依赖 |
| --- | --- | --- | --- |
| [`implement-transport-vendor-neutral-envelope-core`](../../../.spec/tasks/implement-transport-vendor-neutral-envelope-core.md) | Wave 3 | 定义supplier-neutral连接值、generated Envelope gate、codec/carrier SPI、permission reference和无业务dispatch边界。 | `consume-upstream-generated-contract-artifacts`, `implement-host-runtime-bounded-ports` |
| [`implement-transport-registry-bounded-ingress-egress`](../../../.spec/tasks/implement-transport-registry-bounded-ingress-egress.md) | Wave 5 | 建立transport单写registry、connection epoch、Ingress/Egress/Command queues、可靠/分片/限流状态。 | `implement-transport-vendor-neutral-envelope-core`, `implement-host-runtime-supervision-cancellation-and-join` |
| [`implement-transport-local-embedded-fidelity-adapter`](../../../.spec/tasks/implement-transport-local-embedded-fidelity-adapter.md) | Wave 6 | 以内存byte carrier替代OS网络层，但复用同一codec/envelope/permission/size/queue路径。 | `implement-transport-registry-bounded-ingress-egress`, `implement-host-profile-resolution-and-capability-matching` |
| [`implement-transport-remote-and-fault-adapters`](../../../.spec/tasks/implement-transport-remote-and-fault-adapters.md) | Wave 7 | 在D-004满足时以Quinn/rustls实现RemoteDS carrier，并提供bounded确定性故障decorator；两者均不改变稳定API。 | `implement-transport-local-embedded-fidelity-adapter`, `implement-host-runtime-supervision-cancellation-and-join`, `implement-host-profile-fault-decorator-declarations` |

## K. 完成定义

本模块的任务卡全部通过；模块单测/fixture/故障测试通过；Queue Registry、Cargo DAG、许可证与源码红线检查通过；没有供应商类型泄漏、无界队列、任意回调或未见证 FaultClass 推断。
