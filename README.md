# LumioServer

> Rust Dedicated Server Host、网络基础设施与服务器生命周期编排。

## 定位

`LumioServer` 拥有服务器唯一进程入口、Connection/Session、World Slot、网络传输和 CoreCLR Hosting。装入同一 `GameReleaseId` 的 Server Gameplay、配置和内容后，它才成为具体游戏的 Dedicated Server。

它也提供可被 `LocalEmbedded` 使用的 Server Role Host；Local 是同进程双角色，不是共享 ECS World，也不是 Listen Server。

总架构基线见 [`docs/architecture/LumioGameEngine_Architecture_v0.3.md`](docs/architecture/LumioGameEngine_Architecture_v0.3.md)。

Dedicated Server Host、网络、进程治理和 CoreCLR Hosting 统一使用 Rust；Server Gameplay 通过 CoreCLR 以 C# 热更程序集加载，不能把具体玩法编译进 Rust Host。

## 拥有的状态与生命周期

- 进程、监听 Endpoint、Connection、Session、认证状态、重连窗口、限流和背压状态。
- `WorldSlot -> SimulationSession` 生命周期、Tick Clock、资源预算和优雅停服状态。
- 每个 Session 的 Server `GameWorld`、权威 `VoxelWorld`、Replication Context、Snapshot Metadata 和 Migration 状态（数据由 Runtime/Voxel Port 拥有）。
- CoreCLR、Stable Runtime、Server Gameplay Assembly 的启动、激活、热更和升级编排状态。

## 职责

- 启动配置、Endpoint、端口监听、World Slot、资源预算、Watchdog、Health、日志、Metrics 和 Crash 信息。
- Connection/Session、握手、超时、重连、可靠/不可靠通道、Buffer Pool、限流和背压。
- RPC 传输信封、MessageId、RequestId、路由、优先级、包大小和流控校验；Gameplay Payload 保持不透明。
- 统一加载 `LumioCoreEngine` 平台 Native 包，创建 VoxelWorld Port，并托管 `LumioGameRuntime`/CoreCLR。
- 驱动权威 Server `GameWorld` Tick、Cross-World Prepare/Commit、Snapshot/Replication 和生产升级顺序。
- 提供 DS、LocalEmbedded Server Role、Split-Process Test Host 和 Bot Test Endpoint。

## 明确不负责什么

- 不决定技能、物品、战斗、建筑、经济或其他 Gameplay 语义。
- 不创建、销毁或直接访问 ECS Storage；只驱动 Runtime API 和生成契约。
- 不实现 Voxel Chunk/Mutation 内部逻辑，不直接链接多个 NativeCore/VoxelEngine 动态库。
- 不持有 Client Gameplay、Renderer 或 `LumioGame` 源码，不把网络线程直接调用 Hot Gameplay。
- 不替玩法作者判断 Server/Client 状态是否兼容；只校验 Manifest、Schema、ABI 和 Migration Hook。

## 对外产物与契约

- `lumio-server` DS 可执行文件、容器镜像、配置模板和平台包。
- Connection/Session、RPC Envelope、Endpoint、Health/Metrics、WorldSlot 和 Host API 契约。
- `ServerHostManifest`：Core Engine、Runtime、网络协议、CoreCLR、GameRelease、平台和 Artifact Hash。
- LocalEmbedded/Headless Host、Replay/Command Stream、资源预算和故障注入工具。

## Source / Compile-Time Dependencies

- Rust toolchain、网络/IO/日志基础 crates。
- `LumioCoreEngine` 统一 Native 平台产物和生成 Header；不直接以源码形式依赖 `LumioNativeCore` 或 `LumioVoxelEngine`。
- `LumioGameRuntime` 的稳定 Managed Host/ABI；不编译依赖 `LumioClient` 或 `LumioGame` 源码。

## Generated Contract Dependencies

消费 RPC Envelope、MessageId、Endpoint、Core Engine Capability、Voxel Port 和 Game Gameplay Contract 的生成物。Gameplay Payload 由 Game 定义并由网络层按长度、版本和权限做不透明转发。

## Runtime Loading Relationships

```text
lumio-server / LocalEmbedded ServerRoleHost
  -> LumioCoreEngine (one unified native package)
  -> LumioGameRuntime stable host + CoreCLR
  -> ServerGameplay.dll + Game Config/Content
  -> Server GameWorld + authoritative VoxelWorld
```

网络线程、IO 线程和 Rust Job 线程通过 Typed Queue/Batch 与托管 Tick 交互，不直接进入 Hot Gameplay。

## Release Composition Relationships

DS 发行包由 `LumioGame` 锁定并组装：Server Host、一个 CoreEngine 平台包、Runtime、Server Gameplay Assembly、生成契约、配置、内容、Manifest 和签名。Server 与 Client 必须使用同一 `GameReleaseId` 同步换包；Server 负责编排升级、失败恢复和启动拒绝。

## Room Modes / Host Profiles

| RoomMode | Host Profile | Endpoint/进程关系 |
| --- | --- | --- |
| `Online` | `PublicDedicatedServer` | 公共 DS Endpoint。 |
| `Online` | `PlayerHostedDedicatedServer` | 玩家启动的独立 DS 进程 Endpoint。 |
| `Online` | `LocalhostDedicatedServer` | 本机独立 DS 进程 Endpoint。 |
| `Singleplayer` | `LocalEmbedded` | 同一进程实例化 Server Role，通过 InMemoryTransport 对接 Client Role。 |

玩家选择的是 `RoomMode + HostProfile`；Endpoint 只区分 DS 发现/位置，不改变 Gameplay 代码。移动端第一阶段可加入远程 DS，但不启动 Player-hosted DS。

## Headless Test Surface

- DS 启停、WorldSlot/Session、握手、重连、超时、限流、背压、包大小和故障注入。
- `LocalEmbedded`、`LocalSplitProcess`、`RemoteDS`、Bot 连接和网络抖动测试。
- 服务器 Tick/Replication/资源预算、CPU/内存/网络 p95/p99 和约 100 名真实玩家基线。
- Core Engine/Runtime/Game Manifest 校验、Hot Reload、Migration、崩溃恢复和 Replay 重放。

## Version / Manifest

- Server Host、网络协议、CoreCLR Host 和 Game Release 分别记录版本；生产包使用不可变 Hash。
- 启动校验 Server/Client `GameReleaseId`、Gameplay Schema、Runtime API、Core Engine ABI/Capability 和 Voxel Migration。
- Endpoint 元数据不改变 Release 兼容矩阵；拒绝不匹配客户端并记录可诊断错误。

## 开发规范

- 服务器权威 Tick 是唯一提交 Gameplay 结果的入口；网络回调只入队命令。
- 所有资源预算、队列长度、Session 状态和失败路径必须有 Metrics 与 Headless 测试。
- 不在 Host 层复制 Gameplay 规则；用生成契约做长度、版本、权限和能力校验。
- 升级顺序固定为停接入、冻结 Tick、导出 Snapshot、执行 Game/Voxel Migration、校验 Manifest、启动新 Release。
- LocalEmbedded 必须走与 DS 相同的 Server Role API 和消息边界，以保证测试与生产路径一致。

## 当前阶段任务

- 建立 CoreEngine 单包加载、CoreCLR Hosting、Server Role 和 LocalEmbedded 最小闭环。
- 实现 Connection/Session/WorldSlot、InMemoryTransport、Replay 和故障注入 Headless 测试。
- 冻结 DS Host Manifest、Endpoint、升级编排和 100 玩家性能基线。
