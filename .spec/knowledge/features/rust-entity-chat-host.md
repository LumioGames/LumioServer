---
name: rust-entity-chat-host
description: 切片级最小 Rust host——查 entity-chat 纯托管：Runtime 绑定/查询/快照、NativeCore 定时、Room 网线广播
metadata:
  type: doc
  status: 已交付
---

# 切片级最小 Rust host（entity-chat）

`modules/process` 的 `entity_chat` 是 RM-00011 在 Rust 上的切片宿主：进程/时钟/监督线程、有界队列、Account Server 准入凭证验签、Room 会话表、CoreCLR 消费 Runtime 公开面。宿主不拥有绑定表、发号、查询 switch 或快照旁路。

## 背景 / 目标

ADR-056：Rust 宿主是接力交付面，只托管与传输。Room 世界是 Runtime `EcsWorld`；绑定/查询/快照/Persist 只在 Runtime；定时只在 NativeCore 内核（wallClock + tickFrame）。

## 设计

- **会话表**：只保存 `connection ↔ Runtime 绑定句柄` 与 `sess-*` 会话号。`NetEntityId` 由 Runtime 身份表发号（32 位小写 hex）。
- **Runtime 消费**：CoreCLR `entity-chat-host` 转发 `Admit` / `Disconnect` / `Rebind` / `Expire` / `QueryAttribute` / `BuildFullSnapshot` / `BuildDelta` / `CapturePersist` / `RestorePersist`。恢复路径不 `Admit`、不新建 Active 绑定。
- **定时**：host-runtime 是 NativeCore ABI 适配层。五分钟断线保留走 `wallClock` one-shot；Tick 走 `tickFrame` repeating。删除 `expire_due` 轮询。
- **Room 网线**：loopback WebSocket。准入/重连发送 Runtime `BuildFullSnapshot`（含 `stateBlocks`）；每 Tick 把 `BuildDelta` 字节广播给本 Room 连接；同一 `connectionId` 可有多个观察者（Playwright + harness），后连者不得顶掉先连者的 egress。顶号先发 `ConnectionSuperseded` 再关旧连接。S8 证据 `connectionSupersededReceived` 只来自旧 `RoomClient` 收帧，不得用宿主 `takeover` 布尔冒充。S3 在 101 条 `chat.input` 之前挂上 `c-browser` Room WS；`playwrightRan` 只在浏览器真正从网线收到 Room 帧时为 true。
- **解析 / 查询**：`ResolveByNetEntityId` 接受 Runtime 32-hex 与 C-1 u64；HostEntry 把 Runtime `OkEntity`（无 Binding）补成列出的五元组。S5 unauthorized 走声明过的 claim-scoped `EntityIdentity.claimedMark`（`restrictedFlag` 未声明 → `RequestError`，不得冒充 Unauthorized）。
- **Tick 分批**：Runtime `ChatCommandRuntime.RunTick` 经 `ChatIngressWorld` 默认 `EcsBudget.MaxChangeEntries=128`。每条 `chat.input` 写两个 ChatComponent 字段，单 Tick 最多 64 条；超过则 `Command reservation budget exceeded`、Runtime `_faulted`、`BuildDelta` 为 `changedBlocks:[]`。宿主按 `MAX_CHAT_INPUTS_PER_TICK` 穿插 `tickFrame`，`pending > 64` 不得 `RunTick`。`apply_pending_chat_ticks` 在 `tick.ok` 为 false 或 pending 不下降时结束，不自建第二份事件队列。
- **Client Bot**：S6 发言由 suite 拉起 `Lumio.Client.Bot.Host`（可执行路径经 `LumioClientRoot` / `LUMIO_CLIENT_ROOT` / `LUMIO_BOT_HOST` 或仓根相对 `LumioClient` 兄弟发现，缺失 BLOCKED）。Harness 只传参并读其日志目录，禁止 startup-hooks 环境注入、禁止生成 hook csproj、禁止宿主自写 ABI 装载。启动参数：`--server`（Room 地址）、`--account-from` / `--account-to`（账号起止，默认 Bot01–Bot100）、`--engine-native`、`--log-dir`；同名环境变量 `LumioBotServer` / `LumioBotAccountFrom` / `LumioBotAccountTo` / `LumioEngineNative`（及 `LUMIO_ENGINE_NATIVE`）/ `LumioBotLogDir` 作备用，命令行优先。Bot.Host 往 `--log-dir/bot-host.ndjson` 写 JSON lines（`kind=chat.input`）；`timer-trace.json` 不是证据。R4-04 未合入、日志无 `chat.input` 时场景为 `BLOCKED: 等 R4-04`，不得 pass。suite 按宿主 `pending_wire_chat_inputs` 穿插 tick，写 `release.flag` 结束进程；禁止 `host.admit_chat_input` 冒充 Bot 发言，禁止把常量 `[5,10,15]` 写成证据。验收尺子只有 Game `verify-evidence.mjs`，本仓不保留第二把尺。
- **Persist**：`CapturePersist` / `RestorePersist` 走 Runtime 公开面（`RestorePersist` 的第二参是 `ReadOnlyMemory<byte>`）。默认 `MaxSnapshotBytes=4096` 只能装下约 6 个聊天实体；101 实体 Capture 为 Retryable 时不得把 `restoredWindow: 0` / `processB=null` 写成 S7 ok。
- **发现**：外部产物经 `LUMIO_*` 环境变量与仓根相对路径；缺失即 BLOCKED，不硬编码开发机绝对路径。
- **复跑**：`lumio-entity-chat-replay` 两轮；`manifest.conclusion=SUCCESS` 只在 Game `verify-evidence.mjs` oracle 通过之后写。`--restore-snapshot` 供 S7 进程 B 单独启 CLR 恢复。

## 待解决

- 完整 101 实体 acceptance 依赖 Runtime / NativeCore / Game 产物路径；缺失时测试以 BLOCKED 失败而非跳过。S3 的 Playwright Room 观察同样依赖 `LUMIO_GAME_ROOT`。
- Runtime `ChatIngressWorld.Create` 默认预算装不下 101 实体 Persist；S7 跨进程恢复待 Runtime 放大 `MaxSnapshotBytes`。
- `mvp-host/` 仍冻结，归 N-13。

## 相关

- 决策：[`../../decisions/0007-rust-host-consume-runtime.md`](../../decisions/0007-rust-host-consume-runtime.md)
- 实现：[`../../../modules/process/src/entity_chat/`](../../../modules/process/src/entity_chat/)
- 托管入口：[`../../../entity-chat-host/`](../../../entity-chat-host/)
