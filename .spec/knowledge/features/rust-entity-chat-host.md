---
name: rust-entity-chat-host
description: 切片级最小 Rust host——查 entity-chat 的 owner 线程、准入验签、Room world-slot 与 CoreCLR 复跑
metadata:
  type: doc
  status: 已交付
---

# 切片级最小 Rust host（entity-chat）

`modules/process` 的 `entity_chat` 是 RM-00011 在 Rust 上的切片宿主：进程/时钟/监督线程、有界队列、Account Server 准入凭证验签、Room world-slot、CoreCLR 托管同一份 C# `ChatRoomWorld`。它复跑 R-00354 的 11 场景，证据格式与 `integration/entity-chat` 对账器相同。

## 背景 / 目标

Owner 要求看到 Rust 真行为，且双轨只覆盖这一切片。合约保持宿主无关；C# MVP host 在复跑通过后冻结为 reference。

## 设计

- **host-runtime**：单调时钟（可 advance）、有界 MPSC、具名受监督 owner 线程。
- **verify_admission**：消费 `lumio.account-port.v1` 的 LumioBin + LumioSignatureV1 + Ed25519，不重写账号服。
- **Room world-slot**：绑定五元组、顶号、五分钟重连、过期墓碑、跨 Room 隔离、C-2 查询。
- **Gameplay**：CoreCLR 加载 `entity-chat-host` 入口，再 `Assembly.LoadFrom` `ChatRoomWorld`。不把 ECS 语义搬进 Rust。Chat 上行在 Rust host 解码冻结 `InputCommand`（`chat.input` + `payloadSha256`）后再把 text 交给 `ChatRoomWorld`。
- **复跑**：`modules/process/tests/entity_chat_acceptance.rs` 对 sibling Account Server 跑两轮。

## 待解决

- 51 张 Rust 主线（完整 host-runtime / world-slot crate 面）不在本切片。
- `lumio-mvp-host` FullGraph 64 连接预算仍不是 101 路产品路径。

## 相关

- 决策：[`../../decisions/0004-csharp-mvp-host-frozen-reference.md`](../../decisions/0004-csharp-mvp-host-frozen-reference.md)
- 实现：[`../../../modules/process/src/entity_chat/`](../../../modules/process/src/entity_chat/)
- 托管入口：[`../../../entity-chat-host/`](../../../entity-chat-host/)
