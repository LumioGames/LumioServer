# LumioServer

> Rust Dedicated Server Host、网络基础设施、Release Pool、WorldSlot 和服务器运维生命周期。

<!-- lumio-community:start -->
<div align="center">
<table>
<tr>
<td align="center" width="50%" valign="top">
<a href="https://qm.qq.com/q/PGkXh4tCyQ"><img src="https://raw.githubusercontent.com/LumioGames/.github/main/profile/assets/qr-qq.svg" width="170" alt="QQ 交流群 972220164"></a><br>
<a href="https://qm.qq.com/q/PGkXh4tCyQ"><img src="https://img.shields.io/badge/QQ%20%E4%BA%A4%E6%B5%81%E7%BE%A4-972220164-6171F0?style=for-the-badge&logo=tencentqq&logoColor=white" alt="QQ 交流群 972220164"></a><br>
<sub>什么都能聊</sub>
</td>
<td align="center" width="50%" valign="top">
<a href="https://applink.feishu.cn/client/chat/chatter/add_by_link?link_token=fffn1ae7-fd83-4315-96ac-6fa3aba3968e"><img src="https://raw.githubusercontent.com/LumioGames/.github/main/profile/assets/qr-engine.svg" width="170" alt="LumioEngine 开发者社区"></a><br>
<a href="https://applink.feishu.cn/client/chat/chatter/add_by_link?link_token=fffn1ae7-fd83-4315-96ac-6fa3aba3968e"><img src="https://img.shields.io/badge/%E9%A3%9E%E4%B9%A6%E7%BE%A4-LumioEngine%20%E5%BC%80%E5%8F%91%E8%80%85%E7%A4%BE%E5%8C%BA-5DE2C6?style=for-the-badge&logoColor=1E2A3A" alt="LumioEngine 开发者社区"></a><br>
<sub>飞书话题群 · Rust / C# 引擎层</sub>
</td>
</tr>
</table>
<sub>先进群再看代码。其它群和整体介绍见 <a href="https://github.com/LumioGames">LumioGames 主页</a>。</sub>
</div>
<!-- lumio-community:end -->

## 架构基线

- Baseline：`LGE-V1.4-2026-08-27`
- 唯一架构源：`LumioGameEngineArchitecture`
- 本地镜像：[`docs/architecture/LumioGameEngine_Architecture_v1.2.md`](docs/architecture/LumioGameEngine_Architecture_v1.2.md)

`LumioServer` 拥有服务器进程、连接、Release 身份代理、WorldSlot 聚合根、Host Pacing、CoreCLR Hosting、滚动更新与强制维护的本进程侧执行。集群期望状态（Pool 存在性、Release 指派、实例替换时机）归外部控制面（架构源 ADR-012）。它加载稳定 Runtime 与 Server Gameplay，但不拥有 ECS/Voxel 内部状态，也不定义 Gameplay 语义。

## Architecture Gate

ReleaseCatalog/Manifest、Envelope、Maintenance、Logging Event、Host Capability 和失败恢复契约以 `LumioGameEngineArchitecture` 为唯一来源。网络、路由、滚动更新或维护命令变更必须补齐正向/失败 Fixture，并在架构源执行 `python3 tools/lumio_contract.py validate`；目标 Pool 之外的产品/Release 不得被默认影响。

## 拥有的状态与生命周期

- 进程、监听 Endpoint、认证、Connection、Session Admission、重连窗口、限流和背压。
- Catalog 只读副本、本 Pool 成员状态、健康检查、Drain、Rollback 与维护进度（集群期望状态归外部控制面）。
- `WorldSlotHost` 聚合根（Admission Gate、生命周期 epoch、Quiesce 序列、故障分级裁决）、资源配额、Watchdog、Persistence Host 和 Crash Recovery 编排。
- CoreCLR、稳定 Runtime、Server Gameplay Assembly 的启动、激活、重载和关闭流程。
- Host Wall Clock（单调时钟归 `host-runtime`）、Tick pacing、Ingress/Egress 队列和运维状态。

Runtime 拥有 Logical Tick、GameWorld 和 Coordinator；VoxelEngine 拥有 VoxelWorld；Server 保存句柄、Context、Snapshot 元数据和编排状态，不直接访问内部 Storage。

## 子模块

模块架构总入口见 [modules/README.md](modules/README.md)（模块地图、依赖方向、线程/队列拓扑、核心流程与决策门）；各模块边界契约在各自 README。

| 子模块 | 责任 | 首批状态 |
| --- | --- | --- |
| [`process`](modules/process/README.md) | 进程入口与组装根、信号、退出码、配置快照、Crash/Watchdog、端口接线 | P0 |
| [`host-runtime`](modules/host-runtime/README.md) | 单调时钟、Timer 服务、取消树、任务监督、有界执行原语 | P0 |
| [`transport`](modules/transport/README.md) | Reactor、Envelope、可靠性、分片、限流背压、连接注册表唯一写入者 | P0 |
| [`auth`](modules/auth/README.md) | 认证、票据、防重放和连接级授权对象 | P0 |
| [`session`](modules/session/README.md) | Admission 管道、ServerConnectionSession、重连窗口、Release 固定 | P0 |
| [`release-agent`](modules/release-agent/README.md) | Catalog 消费、Manifest 校验、本 Pool 成员状态、健康检查、ExactRelease 匹配 | P1 |
| [`world-slot`](modules/world-slot/README.md) | Host 聚合根：状态机、epoch、Admission Gate、Quiesce 序列、故障裁决、Quota | P0 |
| [`pacing`](modules/pacing/README.md) | Tick 触发判定、Deadline、批次切割（启停受聚合根指挥） | P0 |
| [`coreclr-host`](modules/coreclr-host/README.md) | 稳定 Runtime、ALC、Gameplay 启停、异常转换与 FaultClass 见证转交 | P0 |
| [`persistence-host`](modules/persistence-host/README.md) | Snapshot/WAL/Command Log、commit ack、Checkpoint 和恢复 | P1 |
| [`maintenance-agent`](modules/maintenance-agent/README.md) | 维护命令进度机、滚动更新推进、MaintenanceKick、维护证据 | P1 |
| [`control-plane-adapter`](modules/control-plane-adapter/README.md) | 控制面边界：签名命令验证、fencing、幂等、退出证据上报 | P1 |
| [`observability`](modules/observability/README.md) | Async Log Sink、Audit durable ack、Metrics、Trace、Failure Bundle | P1 |
| [`host-profiles`](modules/host-profiles/README.md) | Capability/Preset、LocalEmbedded 保真、DS/Split/Bot 测试 Host 组装 | P1 |
| [`protocol-dispatch`](modules/protocol-dispatch/README.md) | 生成式消息分发边界（公共契约 D-009 冻结前封锁） | 封锁 |

独立进程（不属于上表 15 个 Host crate）：[`account-server/`](account-server/README.md) 实现 `lumio.account-port.v1` login-or-register、AccountEntity 与准入凭证签发。

## 职责

- 启动配置、Endpoint、WorldSlot、健康检查、资源预算、Watchdog、日志和 Metrics。
- 收包、Envelope/Release/权限校验、可靠/不可靠通道、Ack、重传、分片、认证、防重放和背压。
- 将网络/IO/Native Completion 通过有界 Queue/Batch 交给 Runtime Tick，网络线程不得调用 Gameplay。
- 统一加载一个 `LumioEngineSDK` Native 包、托管 Runtime 和 Server Gameplay ALC。
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
Timer Thread (host-runtime)
Network Reactor(s)
  -> bounded per-session Ingress (SPSC)
  -> Simulation Owner Thread per active WorldSlot
  -> bounded Native Job Pool / Completion Queue
  -> IO/Persistence workers
  -> bounded Egress Queue
  -> Network Send
```

每个队列都有容量、优先级、满载动作和 Metrics（登记于 [modules/README.md](modules/README.md) §4.2 Queue Contract Matrix）；可靠积压超阈值时降级或断开，不能无限增长。Native Completion 只在 Tick Barrier 应用。全部线程经 `host-runtime` 受监督创建；定时语义一律经 Timer 命令投递，不自建轮询线程。V1 建议一个 active WorldSlot/进程，保留多 Slot 接口但明确共享故障域；OOM、CoreCLR 崩溃和 Native UB 按进程级故障处理。

## Network 与 Session 契约

Envelope 至少包含 `protocolVersion、length、sequence、sessionId、productId、gameReleaseId、messageType、reliability、integrity、traceId`（拼写以架构源 `schemas/replication-envelope.schema.json` 为准）。Transport ACK 和 Replication Baseline ACK 分开；未知 Baseline、Gap、旧 Revision、Schema/Release 不匹配进入稳定错误和 Full Resync/拒绝路径。

Session 一旦建立就固定 `productId + gameReleaseId`。Server 侧每连接记录（`ServerConnectionSession`）是 Host 私有状态，不得命名或建模为 Client 拥有的 `ClientReplicaSession`；Client ReplicaWorld 不属于 Server WorldSlot 的物理对象（架构源 ADR-001）。

## Release Catalog、滚动更新与维护

`ReleaseCatalog` 是签名版本清单，记录 `ProductId + GameReleaseId`、Artifact、Capability、Endpoint、Pool 状态和兼容判定。一个进程/Runtime 只加载一个 Release，但同一集群/机器可运行多个 Release Pool，因此 A 1.1 与 BOE 2.1 可以并行服务。

滚动更新状态：

```text
Published -> Verified -> Warmup -> Serving
Old Serving -> Draining -> Empty -> Retired
任一阶段 -> Rollback / Faulted
```

新 Pool 健康检查通过后接收新 Session；旧 Pool 停止新接入并服务已有 Session，直到自然排空、显式迁移或期限到达。V1 不要求在线 Session 无感跨 Release 迁移。

维护命令携带 `productId + gameReleaseId + releasePoolId` 作用域，默认不影响其他产品/Release；命令经 `control-plane-adapter` 完成签名/fencing/幂等验证（过期 fencing token 以 `FencingTokenStale` 拒绝，`maintenanceId` 是幂等键）。deadline 属 Wall/单调时钟域：命令携带 `graceDeadlineSeconds`（时长），收到时一次性换算为单调 deadline，Tick 暂停或墙钟跳变不影响收敛。`Graceful`（宽限 ≥ 1 秒）经聚合根关闸、广播原因/剩余宽限、排空事务并完成 Snapshot/WAL 落盘——persistence commit ack 与 Audit durable ack 是两个独立完成信号——deadline 后以 `MaintenanceKick` 踢出剩余连接；`Forced`（宽限 = 0）立即停止新输入和 Tick 提交，尽最大努力写入 WAL/Failure Bundle 后广播 `MaintenanceKick` 并踢出目标 Pool 的全部用户。恢复时只重放带有 WAL 提交标记的命令，未提交命令视为未生效。本进程确保无连接残留后报告 `ReadyToExit` 并退出；**目标实例激活由外部控制面在本进程之外完成**。断开、失败和恢复动作写入 Audit/Failure Bundle。

## CoreCLR、Hot Reload 与故障隔离

一个进程只启动一个稳定 CoreCLR；Server Gameplay 使用 Collectible ALC 和 Runtime `GameplayModuleScope`。Host 负责 Quiesce、Cancel、Drain、Dispose、Root 验证和卸载。故障分级依据 Runtime 见证的 `FaultClass`（架构源 ADR-006）：`SessionLocalProven` 才允许隔离单 Session，`SlotStateUnproven`（含缺见证默认）强制 Slot 从最近有效 Snapshot 恢复，`ProcessFault`（Stack Overflow、OOM、CoreCLR 崩溃、Native UB）是进程级故障；Host 不从"异常可捕获"推断故障域。

## 持久化、日志与配置

- Persistence Host 以版本化 Snapshot + WAL/Command Log 为基础；本地文件/目录权威，备份对象存储/数据库通过 Adapter。
- 权威确认前按部署策略保证可恢复；Checkpoint 使用校验、压缩、原子替换和保留策略。
- Server 使用成熟 Rust 日志生态的异步多线程 Sink；Error/Fatal 有同步应急落盘（`EmergencySync` 仅限 Error/Fatal）。
- Audit 队列归 `observability`、WAL/TxnJournal/CommandLog 归 `persistence-host`，所有者与 ack 通道分立；durable 写返回显式 ack。每个事件声明 `correlation.scope`（`Process`/`Release`/`Session`/`World`/`Txn`），基础字段 `productId、gameReleaseId、traceId、producerId、eventSeq` 恒必填，层级 ID 不得伪造（架构源 ADR-011）。
- 配置在启动时编译并生成不可变快照；生产只通过签名版本显式切换，不能在半 Tick 中修改。

## Source / Compile-Time Dependencies

- Rust toolchain、网络/IO/日志基础 crates 和平台 SDK。
- `LumioEngineSDK` 统一 Native 包、ABI Binding 与共享 Loader；不直接暴露 NativeCore/VoxelEngine 源码。
- `LumioGameRuntime` 稳定 Managed Host/ABI；不编译依赖 Client 或 Game 实现源码。
- Release/Gameplay Payload 只通过版本化契约消费。

## Generated Contract Dependencies

消费 Root ABI、Capability、Error、Host、Voxel Port 和 Game Gameplay Contract 生成物。RPC Envelope/MessageId 的 dispatch 契约尚未冻结（公共决策门 D-009）——冻结前 `protocol-dispatch` 模块封锁，V1 wire 面只有复制 Envelope 的 MessageTypes。Server 只验证长度、版本、权限和 Hash，不重新定义 Component/Mapping Schema。

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
- Account Server login-or-register、Bot 命名空间四触点、账号库跨重启 AccountId 稳定、准入凭证解形/验签。

## Version / Manifest

`ServerHostManifest` 至少包含 Product/GameRelease、Server Host、Runtime、CoreEngine、Network/Replication Protocol、Platform、Artifact Hash、Capability、Config/Content、Migration、Signature 和 SBOM。握手和启动精确校验；不匹配时返回稳定错误。

## 开源优先与供应链

优先采用成熟开源的 Reactor、TLS、日志、配置、序列化、指标和进程治理框架。依赖通过 Adapter 隔离，锁定版本/Commit，检查许可证、漏洞、SBOM、AOT、确定性和性能；默认优先宽松许可证。

## 开发规范

- Host 只负责时钟、进程、连接和编排；权威状态变更必须进入 Runtime Tick Barrier。
- 网络回调只入有界队列，错误映射为可重试、可拒绝、可致命三类。
- Host 聚合迁移只能由 `world-slot` 聚合根发起并携带 epoch；跨模块协作走类型化命令 + 显式 ack，禁止任意回调注册与共享可变状态。
- 升级不覆盖旧 Release/Snapshot；所有操作可审计、可恢复、可回放。
- 资源配额、队列和维护超时必须有 Metrics 与故障测试。

## 当前阶段与开发节奏

1. **Architecture Gate**：冻结 Host/Runtime 所有权、线程/队列、Envelope、Manifest、维护状态机。
2. **Foundation**：实现进程、Reactor、CoreCLR Smoke、WorldSlot 单实例和有界队列。
3. **Vertical Slice**：接入 Runtime/Voxel/Game，跑通 LocalEmbedded、Snapshot/WAL、Release 拒绝和 Replay。
4. **Production Hardening**：Release Pool 滚动更新、强制维护、Crash Recovery、RemoteDS、Soak 和 100 人基线。
5. **P2**：多 Slot 共享、自动扩缩、Server HybridCLR、跨服和 Sharding；不改变 V1 Session 版本固定规则。
