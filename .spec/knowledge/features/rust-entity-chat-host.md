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
- **Room 网线**：loopback WebSocket。准入/重连发送 Runtime `BuildFullSnapshot`（含 `stateBlocks`）；每 Tick 把 `BuildDelta` 字节广播给本 Room 连接；顶号先发 `ConnectionSuperseded` 再关旧连接。
- **发现**：外部产物经 `LUMIO_*` 环境变量与仓根相对路径；缺失即 BLOCKED，不硬编码开发机绝对路径。
- **复跑**：`lumio-entity-chat-replay` 两轮；`manifest.conclusion=SUCCESS` 只在 Game `verify-evidence.mjs` oracle 通过之后写。

## 待解决

- 完整 101 实体 acceptance 依赖 Runtime / NativeCore / Game 产物路径；缺失时测试以 BLOCKED 失败而非跳过。
- `mvp-host/` 仍冻结，归 N-13。

## 相关

- 决策：[`../../decisions/0007-rust-host-consume-runtime.md`](../../decisions/0007-rust-host-consume-runtime.md)
- 实现：[`../../../modules/process/src/entity_chat/`](../../../modules/process/src/entity_chat/)
- 托管入口：[`../../../entity-chat-host/`](../../../entity-chat-host/)
