---
name: repository-architecture
description: LumioServer 的 V1.4 架构基线、模块所有权、依赖图、队列与跨仓契约消费规则
metadata:
  type: doc
  status: 已交付
---

# LumioServer 架构边界与契约纪律

> 本文是 LumioServer 对公共架构的只读规则镜像。项目治理与验证入口见 [`AGENTS.md`](../../AGENTS.md)、[`knowledge/README.md`](../README.md) 和 [`rules/system.md`](../../rules/system.md)；仓内模块总览见 [`modules/README.md`](../../../modules/README.md)。

## 1. 唯一事实源与版本锁

- 当前唯一有效公共基线是 `LGE-V1.4-2026-08-27`。
- 权威架构仓库是 `LumioGameEngineArchitecture`，固定提交为 `d3252a8886b4bfd56fbb08490c3db0e6fc8c9550`。
- 架构正文路径为 `docs/architecture/LumioGameEngine_Architecture_v1.4.md`；本仓不把正文或 Schema 重新实现为本地契约。
- 固定正文 SHA-256 为 `F1D36ACF33A1F5E8326A9E58D609FCF7D9FA85177F9B5B60BB3F4742C1AFEBD0`。校验应在 `C:/Work/LumioGames/LumioGameEngineArchitecture` 的固定提交上进行，不能用漂移后的 HEAD 代替。
- 架构仓库的 ADR、Schema、ID Registry、Fixture、生成器和校验工具共同构成 Architecture Gate。LumioServer 只消费其发布的版本化结果；实现、README 或生成物不得反向定义公共语义。
- 旧的 `LGE-V1.2-2026-08-27` 内容只可作为历史背景，不能作为当前状态、字段、错误、时序或依赖的依据。

公共语义发生变化时，顺序固定为：架构源新增/更新 ADR → 更新 Schema、ID 与正/失败 Fixture → 运行 Contract validate → 生成新的 BaselineId/Hash → 同步各实现仓只读镜像。未完成这条链时，LumioServer 不得私改 Envelope、Release、Capability、FaultClass、状态机或错误码。

## 2. 七仓库边界与 LumioServer 所有权

架构源冻结的七仓职责如下：

| 仓库 | 必须拥有 | LumioServer 不得接管 |
| --- | --- | --- |
| `LumioNativeCore` | 领域无关 Rust Kernel、Handle、Error、Capability、内存、Job、空间、压缩和 ABI 基础 | Voxel、ECS、Gameplay、网络、Session、CoreCLR |
| `LumioVoxelEngine` | VoxelWorld、Chunk/Block、Voxel Revision、Streaming、Voxel Snapshot/Diff/Migration | Socket、Session、ECS Storage、Host 生命周期 |
| `LumioCoreEngine` | Native 聚合、Root ABI、单包 Loader、Manifest、Hash、签名、SBOM 和平台产物 | World 状态、ECS、GAS、Gameplay、迁移业务语义 |
| `LumioGameRuntime` | ECS、Logical Tick/Phase、Game/Replica World、Coordinator、Replication、GAS、SnapshotCut、Determinism | 进程、Socket、端口、Voxel 内部、具体玩法 |
| `LumioServer` | Rust Host、连接/网络机械层、Auth、Server Session、Release 本地代理、WorldSlot、Pacing、CoreCLR Hosting、维护与升级编排 | ECS/Voxel 权威数据、Logical Phase、Gameplay 规则、集群 desired state |
| `LumioClient` | Client Connection/Handshake、Replica、Prediction、Unity/HybridCLR Host、Headless Bot | Server 权威、公共 Schema、具体内容和 Native 内部 |
| `LumioGame` | Server/Client Component、Processor、Payload、Mapping、GAS 内容、配置/Scenario、Release Composition | Runtime/Host 生命周期、通用 ABI、网络治理、Voxel 内部 |

LumioServer 拥有进程、连接和 Endpoint、认证与接纳、每进程 Release 身份、本进程 Pool member/health、`WorldSlotHost` 聚合根、Host Wall Clock/Pacing、CoreCLR Hosting、本地 Snapshot/WAL 编排、维护代理、控制面 adapter、可观测性和 Host Profile。它只保存 Runtime/Voxel 的不透明句柄，不复制或修改 `GameWorld`、`VoxelWorld`、ECS、GAS、Chunk 或 Gameplay 状态。

## 3. 模块地图与 crate 所有权

仓内保留 15 个一等模块名，但 Foundation 只为其中 14 个可实现模块建立 crate；`protocol-dispatch` 是 D-009 封锁目录：没有 `Cargo.toml`、`src/`、trait、handler、测试替身或依赖边。

| 模块 | crate / 状态 | 唯一职责 |
| --- | --- | --- |
| `process` | `lumio-server-process`（另提供 `lumio-server` 薄 binary） | 入口、配置、信号、进程 Watchdog、显式组装与退出 |
| `host-runtime` | `lumio-host-runtime` | 单调时钟、Timer 投递、取消树、监督、有界端口 |
| `transport` | `lumio-transport` | ConnectionRegistry、Envelope 机械校验、Ingress/Egress、carrier adapter |
| `auth` | `lumio-auth` | 不透明凭据验证、防重放、principal/grant、认证审计事实 |
| `session` | `lumio-session` | `ServerConnectionSession`、接纳 saga、Release 固定、重连句柄 |
| `world-slot` | `lumio-world-slot` | `WorldSlotHost`、epoch、Admission Gate、Owner Thread、故障裁决 |
| `pacing` | `lumio-pacing` | 单调 deadline 到 `TickPermit`；不拥有 Logical Tick |
| `coreclr-host` | `lumio-coreclr-host` | 官方 CoreCLR hosting、generated ABI、Managed 入口 |
| `release-agent` | `lumio-release-agent` | 本进程 Release 校验、ExactRelease、member/health |
| `persistence-host` | `lumio-persistence-host` | Snapshot/WAL/Txn/Cmd durable path、checkpoint/recovery |
| `maintenance-agent` | `lumio-maintenance-agent` | 已验证维护命令的本进程退役编排、双 durable ack |
| `control-plane-adapter` | `lumio-control-plane-adapter` | 验证、fencing、幂等和本地证据上报 |
| `observability` | `lumio-observability` | Diagnostic、Audit durable、Metrics/Trace、Failure Bundle |
| `host-profiles` | `lumio-host-profiles` | Capability/Preset 到 immutable composition plan |
| `protocol-dispatch` | 无 crate（硬封锁） | D-009 解锁前零实现、零 API、零依赖 |

禁止创建 `common`、`globals`、`event_bus`、`everything` 或同义的上帝 crate/file。共享类型只有在满足架构设计中“至少两个模块编译依赖、且不属于现有 owner/生成契约”的准入条件后，才能另行提出 ADR；Foundation 不预建共享 crate。

## 4. 三张图：编译、命令、事件/ack

三张图不能混用。源码依赖决定 Cargo DAG；命令图决定谁能要求 owner 改变状态；事件/ack 图只报告事实或对应动作的完成，不反转控制权。

### 4.1 源码编译 DAG

实现 crate 只能依赖允许的下游 API、Schema 或 generated Artifact，图必须单向且无环：

```text
maintenance-agent -> world-slot, session, release-agent, transport,
                     persistence-host, control-plane-adapter, host-profiles,
                     host-runtime, observability
session            -> auth, release-agent, world-slot, transport,
                     host-profiles, host-runtime, observability
release-agent      -> transport, control-plane-adapter, host-profiles,
                     host-runtime, observability
world-slot         -> coreclr-host, pacing, persistence-host, transport,
                     host-profiles, host-runtime, observability
transport/auth/pacing/coreclr-host/persistence-host/control-plane-adapter
                    -> only their declared foundation/profile/observability ports
observability      -> host-runtime
```

`process` 是唯一 Composition Root，可在组装期知道全部具体模块并接线；它不是借此获得跨模块业务所有权。Generated contract crates 是架构源发布的只读输入，不是 LumioServer 公共契约 owner。`protocol-dispatch` 不得出现在 Cargo metadata 或任何依赖边中。

### 4.2 运行期命令边

- 外部控制面 → `control-plane-adapter`：未验证输入；验证、fencing、幂等后才产生 `VerifiedMaintenanceCommand`。
- `control-plane-adapter` → `maintenance-agent`：已验证维护命令。
- `maintenance-agent` → `world-slot` / `session` / `transport` / `release-agent`：带作用域的 Quiesce、Drain/Kick、Broadcast/Disconnect、本地 member transition。
- `process` → `world-slot`：`QuiesceForShutdown`、配置激活请求；OS signal 不绕到 `maintenance-agent`。
- `world-slot` → `pacing` / `coreclr-host` / `persistence-host`：Start/Pause/Resume/Stop、Managed owner-thread entry、Snapshot/WAL/Checkpoint effect。
- `session` → `auth` / `release-agent` / `world-slot` / `transport`：Authenticate、ExactRelease、Reserve/Bind/Release、连接命令。
- 各定时语义 owner → `host-runtime`：RegisterTimer/Cancel；Timer 不是业务回调注册。

每个 command 都带 request/correlation、作用域身份（如 Slot epoch 或 `maintenanceId`）和明确 ack；重复请求返回既有终态，旧作用域只返回 `StaleEpoch`/`FencingTokenStale` 等已登记错误且不改变状态。

### 4.3 运行期事件与 ack 边

- `transport` → `world-slot`：Owner Thread 拉取 `IngressBatch`；→ `session`：`HandshakeReady`/`ConnectionClosed`。
- `pacing` → `world-slot`：`TickPermit` 与 pacing health。
- `coreclr-host` → `world-slot`：Managed result、ErrorCode 和可选 Runtime `FaultClass` witness。
- `world-slot` → `session`：GateStateChanged/SlotFaulted/admission ack；→ `maintenance-agent`：带 epoch 的 Quiesce progress；→ `process`：ReadyToStop。
- `persistence-host` → `world-slot`：CommitAck/DiskPressure；→ `maintenance-agent`：`PersistenceCommitAck`。
- `observability` → `maintenance-agent`：`AuditDurableAck`/AuditBackpressure；→ `world-slot`：AuditBackpressure。
- `host-runtime` → 各 owner/process：`TimerFired`、`TaskPanicked` 或监督终态。
- `maintenance-agent`/`release-agent`/`process` → `control-plane-adapter`：Progress、Identity、生命周期/退出证据。

`PersistenceCommitAck` 与 `AuditDurableAck` 是两个独立 latch；任何一个都不蕴含另一个。维护完成 predicate 必须同时满足聚合/Session 终态和两个 durable ack。

## 5. Host、Runtime、Gameplay、Voxel 与并发边界

- `WorldSlotHost` 是 Host 侧唯一聚合根。它拥有 Admission Gate、Host 生命周期 epoch、pacing 启停、Quiesce/Drain/Snapshot/Stop 序列和 FaultClass 裁决；子模块只能执行它下发的 typed command。
- 每个 active `WorldSlot` 使用一个由 `host-runtime` 监督的 Simulation Owner Thread；它是唯一 Managed Tick 入口。所有权威状态只在 Runtime Tick Barrier 提交，Host 只驱动入口并消费结果。
- Reactor、IO、Native worker 和平台回调只能写入已登记的 bounded queue/batch，不能直接调用 Gameplay、C# 或修改 World。不得直接使用 `std::thread::spawn`、`tokio::spawn`、`sleep`、轮询或无界 channel；必须经 `host-runtime` 的监督和端口原语。
- Runtime 拥有 Logical Tick、13 相 phase、ECS/Coordinator 和 `GameWorld`；VoxelEngine 拥有 `VoxelWorld`、Chunk/Block/Revision；Gameplay 规则归 `LumioGame`。Host 不定义 phase、复制状态或 Voxel 内部。
- `FaultClass` 路径固定为 Runtime witness → `coreclr-host` 原样转交 → `world-slot` 裁决。可捕获异常、Rust panic、网络/磁盘错误都不能自行推断故障域；缺 witness 固定按 `SlotStateUnproven` 处理。
- `ServerConnectionSession` 是 Host 私有的每连接记录，禁止命名或建模为 `ClientReplicaSession`。连接注册表只能由 `transport` 单写，Admission Gate 只能由 `world-slot` 写。

### 5.1 Tick 与队列合同

Tick 顺序由 V1.4 的 13 相契约冻结：`IngressCapture → DecodeAndCanonicalize → ApplyInputs → ProcessorPlan → CrossWorldPrepare → NativeJobBarrier → CommitDecision → VoxelCommit → EcsCommandBufferCommit → GasAndEventFinalize → ReplicationProjection → SnapshotHashMetrics → EgressPublish`。唯一 Commit Point 是 `GasAndEventFinalize`；重复 Tick 的结果必须是 `IdempotentSame`。

每个队列必须在 Queue Registry 登记唯一 owner、producer、consumer、ordering、capacity、full action 和 close semantics。至少包括 `ProcessControlInbox`、`WatchdogHeartbeatInbox`、`ConnectionIngressQueue`、`ConnectionEgressQueue`、`ConnectionCommandQueue`、`WorldSlotAggregateInbox`、`TickPermitQueue`、`NativeCompletionQueue`、四类 persistence durable queues、`AuditDurableQueue` 和 Failure Bundle queues。队列容量来自配置/SRV-D 测量，不得写成未测量的公共常量；满载必须拒绝、降级、丢弃低优先级或升级故障，不能无界增长或覆盖权威数据。

### 5.2 LocalEmbedded 保真

`LocalEmbedded` 只允许替换 Socket/TLS/OS carrier 为 byte carrier，仍必须经过同一 Schema、Codec/Canonical Serializer、Envelope、Protocol/Permission gate、认证/防重放、消息大小限制、有界队列和 Tick Barrier。它不能以对象直连、共享 World/Storage、绕过权限或绕过队列来制造“通过”。Fault Decorator 的延迟、抖动、丢包、乱序、重复、断线、重连和 QueueFull 必须由 Host Profile 声明并带确定性 seed。

## 6. V1.4 生成契约与只读消费

本仓不得复制公共字段、状态表或 ABI。所有 generated artifact 必须带 BaselineId 以及 Compiler/Input/Output Hash，并以只读依赖消费：

- V1.4 通过 ADR-037 把恢复记录链、PackageIdentity 五元组、SignatureBlock、SessionReleaseTriple、TrustDomain、CompressionCodec、`StateTransitionEvent` 等公共原语下沉到 `common.schema.json`。
- ADR-038 新增 P0 `state-machine-descriptor`，冻结 12 个状态机的 descriptor、可达性、终态出边和 registry 交叉一致性；实现仓消费生成表，不手抄状态机。
- ADR-039 新增 `ContractRuntime` artifact：纯 Rust crate 与纯 C# assembly，零 Native 依赖、零领域语义；它只提供恢复 hash-chain、canonical encode/decode 和 bounded-buffer guards。
- V1.4 同时冻结 ChunkId `c:x:y:z` 规范键、Snapshot SHA-256 checksum、按算法分支的 replication integrity、ID Registry Gate 词汇、`abortReason` 交集和完整五元 PackageIdentity。旧拼写或弱校验不能留在当前实现规则中。
- `ProtocolPermissionValidator`、`CanonicalSerializer`、`ContractTypes`、`StateTransitionTable` 等只从架构源生成并消费；LumioServer 不成为 Schema/ABI/ID/ErrorCode 的第二来源。

## 7. 决策门与生产闸门

以下公共门未满足前，只能实现行为 core 或 adapter SPI，不能把临时选择写成生产契约：

- **D-004 Transport/Codec/Compression**：供应商未冻结。可实现 vendor-neutral Envelope core 和 LocalEmbedded；RemoteDS 生产 carrier/TLS 必须置于隔离 Adapter，并在门通过后启用。
- **D-009 RPC/Message dispatch**：公共 MessageId/RPC wire 未冻结。`protocol-dispatch` 保持零实现；transport 只处理架构源当前复制 MessageTypes，不得增加 handler registry、RPC envelope 或私有路由值。
- **D-010 Control plane**：可实现验证、fencing、幂等和 injected test channel；生产传输/签名 framing 与 desired-state 通道仍受 Baseline 约束。不得把 file/CLI 临时方案冒充冻结 wire。
- **D-011 Auth**：只冻结握手必经的防重放行为；凭据 wire、签名算法和 verifier 供应商未冻结。必须保留不透明 verifier port，不得私造公共 credential 格式。
- **D-001/D-002/D-003/D-005/D-007/D-008** 的临时默认分别是单 Release、service-level drain、Graceful、可恢复 Snapshot/WAL、ExactRelease、文件+控制台 sink；这些是配置/测量候选，不是永久类型约束。

Server 内部 SRV-D-001..017（队列、Watchdog、重连、防重放、deadline、ack 等）在测量和评审前一律标记 provisional；不得进入公共 Schema、ABI、generated artifact、`pub const` 或 SLA。

## 8. Release、维护、恢复与可观测性

- 一个进程默认一个 `gameReleaseId`、一个 CoreEngine package、一个 CoreCLR 和一个 active Slot；Release Pool 的存在性、成员替换和目标激活归外部控制面，Server 只报告本地 member/health。
- ExactRelease 必须匹配 `productId + gameReleaseId`；不提供 N/N-1 或 Active Session 内跨 Release Gameplay Scope 替换。`TargetActivated` 不是本进程终态。
- 维护路径为 `UnverifiedControlFrame → authenticate/fence/idempotency → VerifiedMaintenanceCommand → maintenance-agent → world-slot QuiesceForMaintenance → session drain/kick + transport broadcast + local release transition → persistence commit ack + Audit durable ack → ReadyToExit → control-plane report`。两个 ack 任意顺序到达都必须独立记录，缺任一不可 ReadyToExit。
- OS 关闭路径为 `OS signal → ProcessControlInbox → process → world-slot QuiesceForShutdown → ReadyToStop → structured cancel/join → observability final flush → control-plane exit evidence`；它不伪造外部 MaintenanceCommand。
- Snapshot/WAL/TxnJournal/CommandLog 是恢复输入；`TxnJournal`/`CommandLog` 不能用 `LoggingEvent` 诊断镜像替代。写入使用校验、fsync/原子替换和保留策略，`PersistenceCommitAck` 不得早于权威落盘。
- Diagnostic 可以按策略采样丢弃并计数；Audit durable、WAL/Txn/Cmd 走独立有界持久队列，不得静默丢失。进程级故障、CoreCLR/Native 故障、队列监督失真必须请求 `FailureBundle`；超预算可封装 partial bundle 并记录缺失提供方，不能把故障伪装成 Session-local。

## 9. 变更与审查红线

- 跨线程/跨进程/跨语言/跨 World 的 effect 必须是 typed command/event + bounded port + explicit ack/correlation；禁止 closure callback registry、全局 EventBus、Service Locator 和共享可变 registry。
- 任何新队列先登记合同；任何新线程、Timer 或 async task 先接入 `host-runtime` supervision；任何公共语义改动先回架构源走 ADR/Schema/Fixture/Baseline 流程。
- 生成物不得手改；密钥、credential、signature 原文不得入库、日志、fixture 或任务卡。
- 代码、文档和配置完成后，至少运行 `node .spec/tools/spec-lint.mjs` 及适用的 Cargo/Contract Gate，并在交付证据中记录实际退出码、输出摘要、源提交和哈希。没有新鲜证据不得声称完成。

本镜像只描述当前 V1.4 可执行边界；模块实现细节和后续任务顺序以 `docs/LumioServer_Framework_Implementation_Design_2026-08-27` 及需求室卡片为准，不能用本文件扩张卡片文件集。
