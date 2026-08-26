# LumioServer

> LumioGameEngine v0.2 架构中的 Rust Dedicated Server Host 与服务器基础设施。

## 定位

`LumioServer` 拥有服务器唯一 `main()`、网络与进程生命周期。它是通用服务器宿主，不包含某一款游戏的具体规则；装入 `LumioGame` 发布的 Gameplay、Schema、配置和内容包后，才成为这款游戏的 Dedicated Server。

## 职责

- 进程启动、配置加载、端口监听、World Slot、资源预算和优雅停服。
- Connection、Session、超时、重连、可靠/不可靠通道、限流、背压和 Buffer Pool。
- RPC 传输信封、MessageId、RequestId、路由、优先级和包大小校验。
- Rust Network Runtime、IO/Persistence Worker、Watchdog、Health、日志、Metrics 和 Crash 信息。
- CoreCLR Hosting、Stable C# Runtime 启动、Managed Tick 驱动和热更部署协调。
- 直接链接 `LumioNativeCore` 与 `LumioVoxelEngine`，避免同进程重复加载原生核心。

## 依赖关系

### 上游依赖

- [`LumioNativeCore`](https://github.com/LumioGames/LumioNativeCore)：原生计算与资源基础设施。
- [`LumioVoxelEngine`](https://github.com/LumioGames/LumioVoxelEngine)：服务器权威体素世界。
- [`LumioGameRuntime`](https://github.com/LumioGames/LumioGameRuntime)：Stable Managed Runtime、ECS 与 Managed API。

### 下游使用者

- [`LumioClient`](https://github.com/LumioGames/LumioClient)：仅消费本仓库发布的传输信封契约，不依赖服务器实现。
- [`LumioGame`](https://github.com/LumioGames/LumioGame)：锁定服务器版本，并组装游戏专用 Gameplay、配置和内容形成最终服务器发行包。

```text
LumioNativeCore + LumioVoxelEngine + LumioGameRuntime
                         └─> LumioServer
                             └─> LumioGame server package
```

## 契约所有权

本仓库是 Connection/Session、RPC 传输信封、路由、限流、背压和服务器宿主配置契约的唯一事实源。Gameplay Payload 对 Rust 网络层保持不透明。

## 禁止事项

- 禁止决定技能能否释放、物品能否使用、建筑是否允许或其他 Gameplay 语义。
- 禁止创建、销毁或直接访问 C# ECS Entity 和 Component Storage。
- 禁止保存指向 Hot Gameplay 的 Delegate、方法地址或托管对象。
- 禁止把网络线程、IO 线程或 Rust Job 线程直接进入 Hot Gameplay。
- 禁止在服务器进程中重复加载第二份 `LumioNativeCore` 或 `LumioVoxelEngine` 动态库。
- 禁止反向依赖 `LumioClient` 或 `LumioGame` 源码。

## 当前状态

`v0.1.0` 仅冻结仓库职责与依赖边界；尚未发布服务器代码、镜像或软件包。

