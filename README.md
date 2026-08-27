# LumioServer

> Rust Dedicated Server Host、网络基础设施、Release Pool、WorldSlot 和服务器运维生命周期。

## 架构基线

- Baseline：`LGE-V1.0-2026-08-27`
- 唯一架构源：`LumioGameEngineArchitecture`
- 本地镜像：[`docs/architecture/LumioGameEngine_Architecture_v1.0.md`](docs/architecture/LumioGameEngine_Architecture_v1.0.md)

`LumioServer` 拥有服务器进程、连接、Release 路由、WorldSlot、Host Pacing、CoreCLR Hosting、滚动更新和强制维护。它加载稳定 Runtime 与 Server Gameplay，但不拥有 ECS/Voxel 内部状态，也不定义 Gameplay 语义。

## Architecture Gate

ReleaseCatalog/Manifest、Envelope、Maintenance、Logging Event、Host Capability 和失败恢复契约以 `LumioGameEngineArchitecture` 为唯一来源。网络、路由、滚动更新或维护命令变更必须补齐正向/失败 Fixture，并在架构源执行 `python3 tools/lumio_contract.py validate`；目标 Pool 之外的产品/Release 不得被默认影响。

## 拥有的状态与生命周期

- 进程、监听 Endpoint、认证、Connection、Session Admission、重连窗口、限流和背压。
- `ReleaseCatalog`、Release Pool、健康检查、路由、Drain、Rollback 和维护状态。
- `WorldSlotHost`、资源配额、Watchdog、Persistence Host 和 Crash Recovery 编排。
- CoreCLR、稳定 Runtime、Server Gameplay Assembly 的启动、激活、重载和关闭流程。
- Host Wall Clock、Tick pacing、Ingress/Egress 队列和运维状态。

Runtime 拥有 Logical Tick、GameWorld 和 Coordinator；VoxelEngine 拥有 VoxelWorld；Server 保存句柄、Context、Snapshot 元数据和编排状态，不直接访问内部 Storage。

## 子模块

模块架构总入口见 [modules/README.md](modules/README.md)（模块地图、依赖方向、线程/队列拓扑、核心流程与决策门）；各模块边界契约在各自 README。

| 子模块 | 责任 | 首批状态 |
| --- | --- | --- |
| [`process`](modules/process/README.md) | 进程入口、信号、退出码、配置快照、Crash/Watchdog | P0 |
| [`network`](modules/network/README.md) | Reactor、Envelope、可靠性、分片、限流和背压 | P0 |
| [`auth`](modules/auth/README.md) | 认证、票据、防重放和连接级权限 | P0 |
| [`session`](modules/session/README.md) | Admission、Connection、重连和 Session 路由 | P0 |
| [`release-router`](modules/release-router/README.md) | Catalog、Pool、健康检查、版本固定和路由 | P1 |
| [`world-slot`](modules/world-slot/README.md) | Slot 生命周期、Quota、隔离和诊断 | P0 |
| [`pacing`](modules/pacing/README.md) | Wall Clock、Tick 驱动、暂停和 Deadline | P0 |
| [`coreclr-host`](modules/coreclr-host/README.md) | 稳定 Runtime、ALC、Gameplay 启停和异常转换 | P0 |
| [`persistence-host`](modules/persistence-host/README.md) | Snapshot/WAL/Command Log、Checkpoint 和恢复 | P1 |
| [`maintenance`](modules/maintenance/README.md) | 滚动更新、Drain、强制维护、踢人和回滚 | P1 |
| [`observability`](modules/observability/README.md) | Async Log Sink、Audit、Metrics、Trace、Failure Bundle | P1 |
| [`host-profiles`](modules/host-profiles/README.md) | Capability/Preset、LocalEmbedded 保真、DS/Split/Bot 测试 Host 组装 | P1 |

## 职责

- 启动配置、Endpoint、WorldSlot、健康检查、资源预算、Watchdog、日志和 Metrics。
- 收包、Envelope/Release/权限校验、可靠/不可靠通道、Ack、重传、分片、认证、防重放和背压。
- 将网络/IO/Native Completion 通过有界 Queue/Batch 交给 Runtime Tick，网络线程不得调用 Gameplay。
- 统一加载一个 `LumioCoreEngine` 平台包、托管 Runtime 和 Server Gameplay ALC。
- 驱动 Host Wall Clock；在 Runtime 规定的 Phase 入口调用逻辑 Tick。
- 编排 Release Catalog、版本池、Session 排空、强制维护、Snapshot/WAL 落盘和恢复。
- 提供 Dedicated、LocalEmbedded Server Role、LocalSplitProcess、Headless Bot Endpoint。

## 明确不负责什么

- 不决定技能、物品、战斗、建筑、经济、任务或其他 Gameplay 语义。
- 不创建、销毁或直接访问 ECS Storage；只调用 Runtime API 和生成契约。
- 不实现 Voxel Chunk/Mutation 内部逻辑，不加载第二套 Native 包。
- 不拥有 Logical Tick Phase、Replication Mapping、Client Prediction 机制或 Game Content。
- 不在网络线程直接调用 Hot Gameplay，不把第三方网络类型写入稳定契约。

## 线程、队列与资源治理

```text
Network Reactor(s)
  -> bounded per-session Ingress
  -> Simulation Owner Thread per active WorldSlot
  -> bounded Native Job Pool / Completion Queue
  -> IO/Persistence workers
  -> bounded Egress Queue
  -> Network Send
```

每个队列都有容量、优先级、满载动作和 Metrics；可靠积压超阈值时降级或断开，不能无限增长。Native Completion 只在 Tick Barrier 应用。V1 建议一个 active WorldSlot/进程，保留多 Slot 接口但明确共享故障域；OOM、CoreCLR 崩溃和 Native UB 按进程级故障处理。

## Network 与 Session 契约

Envelope 至少包含 `ProtocolVersion、Length、Sequence、SessionId、ProductId、GameReleaseId、MessageType、Reliability、Integrity、TraceId`。Transport ACK 和 Replication Baseline ACK 分开；未知 Baseline、Gap、旧 Revision、Schema/Release 不匹配进入稳定错误和 Full Resync/拒绝路径。

Session 一旦建立就固定 `ProductId + GameReleaseId`。Server 只保存远端 Client 的 Connection/Replication Context；Client ReplicaWorld 不属于 Server WorldSlot 的物理对象。

## Release Catalog、滚动更新与维护

`ReleaseCatalog` 是签名版本清单，记录 `ProductId + GameReleaseId`、Artifact、Capability、Endpoint、Pool 状态和兼容判定。一个进程/Runtime 只加载一个 Release，但同一集群/机器可运行多个 Release Pool，因此 A 1.1 与 BOE 2.1 可以并行服务。

滚动更新状态：

```text
Published -> Verified -> Warmup -> Serving
Old Serving -> Draining -> Empty -> Retired
任一阶段 -> Rollback / Faulted
```

新 Pool 健康检查通过后接收新 Session；旧 Pool 停止新接入并服务已有 Session，直到自然排空、显式迁移或期限到达。V1 不要求在线 Session 无感跨 Release 迁移。

维护命令携带 `ProductId + GameReleaseId + ReleasePoolId` 作用域，默认不影响其他产品/Release；命令分为两种：`Graceful` 停止新接入、广播原因/截止时间、排空事务并完成 Snapshot/WAL/Audit 落盘，超时后以 `MaintenanceKick` 踢出目标 Pool 的全部剩余连接；`Forced` 立即停止新输入和 Tick 提交，尽最大努力写入 WAL/Failure Bundle 后广播 `MaintenanceKick` 并踢出目标 Pool 的全部用户。恢复时只重放带有 WAL 提交标记的命令，未提交命令视为未生效。两种模式都关闭目标旧实例、启动目标 Release，并将断开、失败和恢复动作写入 Audit/Failure Bundle。

## CoreCLR、Hot Reload 与故障隔离

一个进程只启动一个稳定 CoreCLR；Server Gameplay 使用 Collectible ALC 和 Runtime `GameplayModuleScope`。Host 负责 Quiesce、Cancel、Drain、Dispose、Root 验证和卸载。可捕获 Gameplay Exception 可隔离为 Session Fault；Stack Overflow、OOM、CoreCLR 崩溃和 Native UB 是进程级故障，必须从最近有效 Snapshot 恢复或重启。

## 持久化、日志与配置

- Persistence Host 以版本化 Snapshot + WAL/Command Log 为基础；本地文件/目录权威，备份对象存储/数据库通过 Adapter。
- 权威确认前按部署策略保证可恢复；Checkpoint 使用校验、压缩、原子替换和保留策略。
- Server 使用成熟 Rust 日志生态的异步多线程 Sink；Error/Fatal 有同步应急落盘。
- Diagnostic、Audit、Txn Journal、Command Log、Metrics、Trace 分开保存，共享 Product/Release/Pool/Maintenance/Session/World/Tick/Txn/Trace 关联。
- 配置在启动时编译并生成不可变快照；生产只通过签名版本显式切换，不能在半 Tick 中修改。

## Source / Compile-Time Dependencies

- Rust toolchain、网络/IO/日志基础 crates 和平台 SDK。
- `LumioCoreEngine` 统一 Native 包与生成 Header；不直接依赖 NativeCore/VoxelEngine 源码。
- `LumioGameRuntime` 稳定 Managed Host/ABI；不编译依赖 Client 或 Game 实现源码。
- Release/Gameplay Payload 只通过版本化契约消费。

## Generated Contract Dependencies

消费 Root ABI、Capability、Error、RPC Envelope、MessageId、Host、Voxel Port 和 Game Gameplay Contract 生成物。Server 只验证长度、版本、权限和 Hash，不重新定义 Component/Mapping Schema。

## Runtime Loading Relationships

```text
lumio-server / LocalEmbedded ServerRoleHost
  -> ReleaseCatalog + CoreEngine Loader (one package)
  -> stable Runtime + CoreCLR
  -> ServerGameplay.dll + Config/Content
  -> Server GameWorld/VoxelWorld handles
```

## Release Composition Relationships

`LumioGame` 组装 Server Host、CoreEngine、Runtime、Server Gameplay、生成契约、Config/Content、Migration、Manifest 和签名。Server 负责启动校验、Release 路由、升级、维护和失败恢复，不负责玩法兼容语义。

## Room Modes / Host Profiles

支持 `PublicDedicatedServer`、`PlayerHostedDedicatedServer`、`LocalhostDedicatedServer` 和 `LocalEmbedded` Server Role；测试 Host 还包括 `PureHeadless`、`NativeHeadless`、`LocalSplitProcess`、`RemoteDS`。Listen Server 不是 V1 目标；Player-hosted 始终是独立 DS 进程。

## Headless Test Surface

- DS 启停、Admission、握手、重连、Session/WorldSlot、Tick pacing、Quota、Watchdog 和维护。
- Wire Envelope、可靠性、分片、Ack、限流、背压、认证、防重放和网络故障注入。
- LocalEmbedded 的同 Codec/同权限/有界队列保真度，以及 LocalSplitProcess 端口/进程隔离。
- Release Catalog、Hash/Signature/Capability 拒绝、滚动更新、Drain、强制踢人和 Rollback。
- Snapshot/WAL 恢复、磁盘满、OOM、CoreCLR/ALC/Native 故障、日志背压和 Failure Bundle。
- 1/10/25/50/100/150/200 玩家 Workload，记录 Tick p50/p95/p99、CPU、RSS、GC、队列和网络。

## Version / Manifest

`ServerHostManifest` 至少包含 Product/GameRelease、Server Host、Runtime、CoreEngine、Network/Replication Protocol、Platform、Artifact Hash、Capability、Config/Content、Migration、Signature 和 SBOM。握手和启动精确校验；不匹配时返回稳定错误。

## 开源优先与供应链

优先采用成熟开源的 Reactor、TLS、日志、配置、序列化、指标和进程治理框架。依赖通过 Adapter 隔离，锁定版本/Commit，检查许可证、漏洞、SBOM、AOT、确定性和性能；默认优先宽松许可证。

## 开发规范

- Host 只负责时钟、进程、连接和编排；权威状态变更必须进入 Runtime Tick Barrier。
- 网络回调只入有界队列，错误映射为可重试、可拒绝、可致命三类。
- 升级不覆盖旧 Release/Snapshot；所有操作可审计、可恢复、可回放。
- 资源配额、队列和维护超时必须有 Metrics 与故障测试。

## 当前阶段与开发节奏

1. **Architecture Gate**：冻结 Host/Runtime 所有权、线程/队列、Envelope、Manifest、维护状态机。
2. **Foundation**：实现进程、Reactor、CoreCLR Smoke、WorldSlot 单实例和有界队列。
3. **Vertical Slice**：接入 Runtime/Voxel/Game，跑通 LocalEmbedded、Snapshot/WAL、Release 拒绝和 Replay。
4. **Production Hardening**：Release Pool 滚动更新、强制维护、Crash Recovery、RemoteDS、Soak 和 100 人基线。
5. **P2**：多 Slot 共享、自动扩缩、Server HybridCLR、跨服和 Sharding；不改变 V1 Session 版本固定规则。
