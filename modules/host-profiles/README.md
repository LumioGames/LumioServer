# host-profiles 模块

> Host Capability/Preset 声明与匹配、LocalEmbedded 保真约束、Fault Decorator 配置入口与测试 Host 组装矩阵。

## 模块定位与目标

`host-profiles` 把"这个进程以什么形态运行、提供哪些能力"变成一份启动期解析、运行期只读的声明。它是全员只读依赖：任何模块可以查询能力，任何 Scenario 的 `requiredCapabilities` 在激活前与本进程的 `capabilities` 匹配，不匹配以稳定原因失败——Gameplay 永远不需要 `IsOffline`/`IsLocal` 之类布尔分支（架构源 ADR-014）。

## 负责什么

- 解析并持有本进程的 Host Capability 声明（公共 Schema：架构源 `schemas/host-capability.schema.json`——`preset`、`roomMode`、`roles`、`capabilities`、`platformProfile`、`faultProfile`、`requiredCapabilities`）。
- Preset 组装矩阵：`RemoteDS`、`LocalSplitProcess`、`LocalEmbedded`、`PureHeadless`、`NativeHeadless` 各自对应的模块装配差异（例如 `LocalEmbedded` 用 InMemory 传输 Adapter，`RemoteDS` 用完整网络栈），供 [process](../process/README.md) 组装时消费。
- Required/Provided Capability 匹配：Scenario 声明 `requiredCapabilities`，Host 声明 `capabilities`；只允许匹配组合激活，失败发生在 Session 激活之前并携带稳定原因。
- Fault Decorator 配置入口：按 Host Profile 声明延迟、抖动、丢包、乱序、重复、断线、重连、QueueFull 注入，带确定性 Seed，并把 fault profile 记录进 Replay/Failure Bundle 元数据（架构源 ADR-009）。
- LocalEmbedded 保真约束的**声明与守护**：LocalEmbedded 可绕过 Socket/TLS/OS 网络栈，但必须复用同一 Envelope、Codec、大小限制、权限路径、有界队列与 Tick 交付；本模块负责把"哪些可绕过、哪些不可绕过"表达为 Capability 组合，使旁路在类型上不可表达。
- Headless 测试 Host 组装差异：DS 启停、Bot Endpoint、LocalSplitProcess 端口/进程隔离等测试面所需的 Profile 组合（测试面清单见根 [README.md](../../README.md) 的 Headless Test Surface 章节）。

## 明确不负责什么

- 不实现任何传输（归 [network](../network/README.md)）；本模块只声明该 Profile 使用哪类传输 Adapter。
- 不定义 Capability Schema（归架构源）；不新增 Capability 语义——新增能力位必须先在架构源走 ADR/Schema 流程。
- 不做 Gameplay 分支：Gameplay 只读取 Role、Capability 和 Port。
- 不拥有 Client 侧 Preset（`MobileLocal` 的 Client 实现归 `LumioClient`）；本模块只关心其中 Server Role 的含义。

## 拥有的状态与资源

- 本进程生效的 HostCapability 声明（启动期解析、运行期不可变）。
- Preset 到模块装配差异的映射表（静态设计数据）。
- Fault Decorator 配置（含确定性 Seed），运行期只读。

## 输入、输出与稳定接口

- **输入**：部署配置中的 Preset 选择与 Capability 覆盖、Scenario 的 `requiredCapabilities`（经 Release/握手输入）。
- **输出**：只读 Capability 查询结果、Preset 装配决议、Capability 匹配裁决（通过/带稳定原因失败）、fault profile 元数据。
- **稳定接口**：`provided_capabilities()` 只读查询；`match_required(required) -> Ok | StableReason` 匹配裁决；`transport_profile()`、`fault_profile()` 装配查询。

## 上游与下游依赖

- **上游**：[process](../process/README.md)（启动装配）、[network](../network/README.md)（传输/故障注入 Profile）、[auth](../auth/README.md)（权限相关能力位）、[coreclr-host](../coreclr-host/README.md)（Native/Runtime 能力位）、[session](../session/README.md)（激活前匹配，经 release-router 的 Capability 校验链）。
- **下游**：仅 [observability](../observability/README.md)（记录匹配失败事件）。本模块不得回调任何上层模块。

## 生命周期与状态机

- 启动期一次性解析并冻结；无运行期状态机。
- 变更 Profile 的唯一途径是携带新配置快照重启进程；不存在运行中切换 Preset。

## 线程、队列与并发所有权

- 无自有线程、无队列；全部数据不可变，任意线程只读访问。

## 正常数据流与失败路径

- **正常**：部署配置 → Schema 校验 → Capability 声明冻结 → 各模块按 Profile 装配 → Scenario 匹配通过 → Session 激活。
- **失败路径**：
  - Capability 声明缺必填字段（如缺 `roles`）：启动失败，对应架构源反例 Fixture `fixtures/invalid/host-capability-missing-role.json`。
  - `requiredCapabilities` 不满足：Session 激活前以稳定原因拒绝；只有 Scenario 显式允许时才走降级组合。
  - 未声明的 Capability 被查询：返回稳定"未声明"结果，不隐式推断——任何代码不得从环境变量推断能力（架构源 `docs/architecture/DECISIONS_PENDING.md` 确认记录条款）。

## 错误分类、恢复与降级

- **可重试**：无（解析是一次性启动动作）。
- **可拒绝**：声明缺字段、Preset 未知、Required/Provided 不匹配——拒绝启动或拒绝激活。
- **可致命**：无独立致命路径；解析失败即启动失败，由 [process](../process/README.md) 处置。
- **降级**：仅当 Scenario 显式允许 fallback 组合时按声明降级，降级事实写入 Audit。

## 配置、Capability 与安全约束

- 本模块即 Capability 的宿主侧权威消费点；能力协商是 ReleaseManifest/握手的一部分（架构源 ADR-014）。
- fault profile 只在测试类 `roomMode` 下允许非 `NoFault` 值；生产 Profile 强制 `NoFault`——这是 dev-only 开关不得进生产的具体化。

## 日志、Metrics、Trace 与 Audit

- 匹配失败、降级采用、fault profile 激活写 Audit。
- Metrics：匹配失败计数（按稳定原因分类）。
- fault profile 与 Seed 进入 Replay/Failure Bundle 元数据，保证故障注入可重放。

## 测试面、故障矩阵与性能指标

- **测试面**：每个 Preset 的装配差异、Required/Provided 匹配矩阵（含 Pure/Native Headless 组合）、LocalEmbedded 与 LocalSplitProcess 用相同命令流对比 State Hash/ACK/Failure Bundle（架构源 ADR-009 验证要求）。
- **故障矩阵**：缺 `roles` 声明、未知 Preset、不匹配组合、生产 Profile 携带 fault 注入的拒绝。
- **性能指标**：无热路径；解析发生在启动期，计入 [process](../process/README.md) 的启动耗时分解。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-009-local-transport.md`（LocalEmbedded 保真与 Fault Decorator）、`docs/adr/ADR-014-platform-capability.md`（Capability 正交声明与 Preset）。
- 架构源 `schemas/host-capability.schema.json`：正例 `fixtures/valid/host-capability.json`，反例 `fixtures/invalid/host-capability-missing-role.json`。

## 尚未批准的决策门

- **D-006**（MobileLocal 内存/启动预算与 HybridCLR 政策）：临时默认值为先做测量 spike，Server HybridCLR 与深度移动优化不是 V1 前置；预算确认前 `LocalEmbedded` 在移动目标设备上的可行性结论标注 provisional。登记见 [modules/README.md](../README.md) §11.1。
