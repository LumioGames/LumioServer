# process 模块

> 进程入口与组装根：启动/关闭编排、信号处理、配置快照、进程级 Watchdog 与崩溃处置。

## 模块定位与目标

`process` 是 LumioServer 进程的入口与唯一组装根。它把 [modules/README.md](../README.md) §6.1 的启动顺序和 §6.6 的关闭顺序变成可执行编排，保证任何初始化失败都进入明确 `Faulted` 而不留半初始化对象，任何退出都有分类退出码与可审计证据。

## 负责什么

- 进程入口（未来的 `main`）：命令行/环境解析、部署配置装载、按固定顺序初始化与析构全部模块；组装期完成全部类型化端口接线（如 auth 的 `ReplayStorm` 到 transport、host-runtime 的监督事件到本模块）。
- 配置装载与编译：把人类可读配置经 Schema 校验、按 `Engine -> Platform -> Server -> Product -> Environment -> User/Session` 固定层级合并，编译为**不可变配置快照**并分发给各模块；生产环境只接受带 Hash/签名的版本切换，切换请求经 [world-slot](../world-slot/README.md) 聚合命令送达，由其 Simulation Owner Thread 在 Tick Barrier 上原子应用（Tick 边界事实由 [pacing](../pacing/README.md) 的判定结果供给，无回调注册）。
- 信号处理：SIGTERM/SIGINT 触发优雅关闭流程（对聚合根下发 `QuiesceForShutdown`）；不可捕获信号（SIGKILL）依赖崩溃恢复路径兜底。
- 退出码约定：按"正常退出 / 优雅维护退出 / 配置或校验失败 / 进程级故障"分类，供进程管理器与运维脚本判定重启策略；退出证据经 [control-plane-adapter](../control-plane-adapter/README.md) 报告（`ReadyToExit` 是控制面激活目标实例的前置证据，架构源 ADR-012）。
- 进程级 Watchdog：汇聚各模块心跳与 [host-runtime](../host-runtime/README.md) 的 `TaskPanicked` 监督事件（Slot 级 Watchdog 归 [world-slot](../world-slot/README.md)；阈值属 SRV-D-016），判定进程整体失活并触发自愈或退出。
- 崩溃处置：安装 panic hook；进程级故障（OOM、Stack Overflow、CoreCLR 崩溃、Native UB，`FaultClass.ProcessFault`）发生时写 crash marker、触发 Failure Bundle 装配（经 [observability](../observability/README.md)），下次启动时进入恢复流程（见 [modules/README.md](../README.md) §6.5）。

## 明确不负责什么

- 不做任何网络 IO、Envelope 解析或连接管理（归 [transport](../transport/README.md)）。
- 不定义配置格式契约与 Table Reader（归 `LumioGameRuntime`，架构源 ADR-010）；本模块只做装载、校验、编排与分发。
- 不拥有 Tick 触发（归 [pacing](../pacing/README.md)）、CoreCLR 生命周期（归 [coreclr-host](../coreclr-host/README.md)）、Slot 状态机（归 [world-slot](../world-slot/README.md)）。
- 不决定维护命令语义与滚动更新状态（归 [maintenance-agent](../maintenance-agent/README.md)、[release-agent](../release-agent/README.md)）；进程只是被编排的执行单元。
- 不提供通用 lifecycle callback、Service Locator 或 `Vec<dyn Service>` 插件面；组装与逆序关闭只操作编译期明确的 `Components` 字段和类型化端口。

## 拥有的状态与资源

- 进程生命周期状态（本仓细化设计，非公共契约）：`Starting -> Ready -> Serving -> Draining -> Stopping -> Exited`，任一活动状态可进入 `Faulted`。
- 不可变配置快照（当前生效版本 + 其 Hash/签名元数据）。
- 显式 `Components` 组装：每个具体模块的句柄、类型化端口、固定初始化/析构顺序与健康心跳句柄；不存在运行期可扩张的模块注册表。
- crash marker 文件句柄与退出码。

## 输入、输出与稳定接口

- **输入**：命令行参数、环境变量、部署配置文件、信号、上次运行遗留的 crash marker。
- **输出**：不可变配置快照（分发给全部模块）、分类退出码、启动/关闭 Audit 事件、Failure Bundle 触发请求。
- **稳定接口**（未来实现的边界承诺）：`run(...) -> 退出码` 薄入口（MS-00002 已按此落地，见下节）；`ConfigSnapshot` 只读句柄；具体 `Components` 与逐模块类型化 command/event ports。初始化、健康检查和关闭由固定编排直接调用具体组件接口，不注册任意回调。

## 当前实现（R-00359 切片级 entity-chat Rust host）

`entity_chat` 模块是本切片的最小 Rust host：`host-runtime` 监督的 Simulation Owner Thread、有界 owner/ingress 队列、对 Account Server 准入凭证的离线验签、Room world-slot（绑定/五分钟重连/过期墓碑/隔离）、以及经 CoreCLR 加载的同一份 C# `ChatRoomWorld`。不扩展 `hello-wire-v1`。验收是 `integration/entity-chat` 的 11 场景原样复跑。

## 当前实现（MS-00002 Hello World 垂直切片）

`lumio-server` 二进制已是可运行的专用服务器进程：动态端口 loopback WebSocket（子协议 `lumio-hello-v1`）、双会话准入、SDK DLL 加载校验、CoreCLR Runtime 桥、权威 tick 路由、NDJSON audit 与优雅关闭。wire 真值是架构仓 `engine/wire/hello-wire-v1.json`，经 `--wire-contract` 启动时装载并驱动全部限额。crate 内模块划分：`cli`（参数）、`wire`（契约装载与错误码）、`audit`（NDJSON sink）、`session`（准入状态机）、`sdk_loader`（手写 FFI：sidecar/root 表校验 + `create_clr_host`/`clr_host_call`/`destroy_clr_host`）、`runtime_bridge`（op 协议编解码与 `ClrBridge`）、`server`（transport：子协议守卫、有界 ingress 队列、reader/writer 任务）、`world`（权威循环：enqueue→tick→路由、审计、graceful shutdown）。

约束与边界：

- **`create_clr_host` 每进程只允许成功一次**（CoreCLR 无法卸载，二次 `initialize_for_runtime_config` 返回 0x80008081）。本模块按"启动创建一次、关闭 destroy 一次"设计，`ClrBridge` 的 Drop 序保证不重复创建。
- 域级拒绝（`{"ok":false,"code":...}` + rc=0）映射为 wire `Error` 回执并记 `ingress_rejected` audit；FFI 层失败映射 `runtime_failure`。
- `--client` 参数仅为集成启动器兼容而保留，本波不实现静态 HTTP 服务（启动器传空）。
- 完整架构（host-runtime 监督、world-slot 聚合根、observability durable 管道）仍按模块规划逐步落地；本切片的 tokio 任务与定时器为过渡实现，未走监督 API。

## 上游与下游依赖

- **上游（调用本模块）**：操作系统进程管理器/容器编排（外部，经 [control-plane-adapter](../control-plane-adapter/README.md) 的命令通道与信号）；无仓内上游。
- **下游（本模块调用）**：作为组装根按序初始化全部模块；运行期依赖 [host-profiles](../host-profiles/README.md)（Preset 解析）、[host-runtime](../host-runtime/README.md)（监督事件汇聚）、[world-slot](../world-slot/README.md)（关闭/配置切换的聚合命令）、[control-plane-adapter](../control-plane-adapter/README.md)（状态上报）与 [observability](../observability/README.md)（事件与 Failure Bundle）。

## 生命周期与状态机

```text
Starting    — 配置编译、host-runtime 初始化（最早）、observability 初始化、host-profiles 解析
Ready       — release-agent/coreclr-host/world-slot 初始化完成
Serving     — transport 监听、Host Admission Gate 开启（world-slot）
Draining    — 收到关闭信号或维护指令，聚合根关闸停止新接入
Stopping    — 落盘完成，逐模块析构
Exited      — 按分类退出码退出，退出证据已报告
任一活动状态 -> Faulted — 写证据后按故障处置退出
```

析构顺序与初始化顺序严格相反；`Faulted` 时跳过无法安全执行的步骤，但 Failure Bundle 触发与 crash marker 写入不可跳过。

## 线程、队列与并发所有权

- 拥有 Main 线程与信号处理线程；不拥有任何工作线程池。
- 不拥有业务队列；配置快照通过只读共享（不可变数据）分发，无跨线程可变共享。
- 心跳汇聚使用有界通道，容量与判定阈值随进程级 Watchdog 参数一起在部署配置声明。

## 正常数据流与失败路径

- **正常**：配置文件 → 校验/合并/编译 → 不可变快照 → 按序初始化模块 → `Serving` → 信号 → 按序关闭 → `Exited(0)`。
- **失败路径**：
  - 配置校验失败：启动即失败，输出稳定错误与失败层级，退出码为配置类；不产生半初始化模块。
  - 某模块初始化失败：逆序析构已初始化模块，进入 `Faulted`，写启动失败证据。
  - 运行期进程级故障：panic hook/异常兜底 → crash marker + Failure Bundle 触发 → 终止；恢复由下次启动的 §6.5 流程完成。

## 错误分类、恢复与降级

- **可重试**：配置源暂时不可读（有限次重试后转可拒绝）。
- **可拒绝**：配置 Schema 校验失败、签名/Hash 不匹配、层级冲突——拒绝启动，不降级。
- **可致命**：OOM、Stack Overflow、CoreCLR 崩溃、Native UB——进程终止，从最近有效 Snapshot + WAL 恢复；**不得**把进程级故障伪装为可恢复 Session Fault。

## 配置、Capability 与安全约束

- 配置层级顺序与 Tick 边界切换规则是公共契约（架构源 §11.3）；Secret 与普通配表分离，密钥不入库、不进日志。
- dev-only 开关、种子数据、调试后门不得在生产开启（本仓 [rules/system.md](../../.spec/rules/system.md)）。
- 生效 Preset 与 Capability 由 [host-profiles](../host-profiles/README.md) 解析，`process` 只消费结果。

## 日志、Metrics、Trace 与 Audit

- 启动/关闭/配置切换/Faulted 迁移全部发 Audit 事件（durable，不可静默丢失）。
- Metrics：启动耗时分阶段、配置编译耗时、心跳延迟、退出码计数。
- 全部事件携带公共 correlation 字段（`productId`、`gameReleaseId`、`traceId`、`producerId`、`eventSeq` 等，Schema 见架构源 `schemas/logging-event.schema.json`）。

## 测试面、故障矩阵与性能指标

- **测试面**：DS 启停、每个生命周期迁移、重复关闭、初始化失败逆序析构、配置层级优先级与 duplicate-key 拒绝、退出码分类。
- **故障矩阵**：配置损坏、签名错误、模块初始化失败、启动中信号、运行中 panic、crash marker 存在时的恢复启动。
- **性能指标**：冷启动到 `Serving` 的时间、优雅关闭完成时间（须小于维护 deadline）、配置编译耗时。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-001-session-lifecycle.md`（所有权与时钟分界）、`docs/adr/ADR-010-persistence-config.md`（配置快照）、`docs/adr/ADR-011-observability.md`（Failure Bundle 触发义务）。
- 架构源 `schemas/config-table.schema.json`（正例 `fixtures/valid/config-table.json`，反例 `fixtures/invalid/config-duplicate-key.json`）、`schemas/failure-bundle.schema.json`（正例 `fixtures/valid/failure-bundle.json`，反例 `fixtures/invalid/failure-bundle-bad-hash.json`）。

## 尚未批准的决策门

- **SRV-D-016**（进程级 Watchdog 心跳源、失活窗口与自愈动作）：临时默认值为全部具名线程心跳 + 10 秒失活窗口 + 失活即按进程级故障退出；与 SRV-D-003（Slot 级）分别测量确认。登记见 [modules/README.md](../README.md) §11.2。
- 受 D-005（恢复窗口影响关闭落盘等待策略）与 SRV-D-010（Graceful 宽限窗口决定 Draining 上限）间接约束。
