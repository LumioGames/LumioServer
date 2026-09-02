---
name: rust-entity-chat-host
description: 切片级最小 Rust host——查 entity-chat 的 owner 线程、host-timer tick、Playwright 与 CoreCLR 复跑
metadata:
  type: doc
  status: 已交付
---

# 切片级最小 Rust host（entity-chat）

`modules/process` 的 `entity_chat` 是 RM-00011 在 Rust 上的切片宿主：进程/时钟/host-timer/监督线程、有界队列、Account Server 准入凭证验签、Room world-slot、CoreCLR 托管同一份 C# `ChatRoomWorld`。它复跑 R-00354 的 11 场景。SUCCESS 对账器是 Game `1169a66` `verify-evidence.mjs`；不得把 `hostProcess.process` 写成 `lumio-mvp-host`。

## 背景 / 目标

Owner 要求看到 Rust 真行为，且双轨只覆盖这一切片。合约保持宿主无关。identical suite 已在 `lumio-entity-chat-replay` 上通过后，C# MVP 冻结为 reference（[0006](../../decisions/0006-csharp-mvp-host-frozen-after-rust-identical-suite.md)）。

## 设计

- **host-runtime**：单调时钟（可 advance）、有界 MPSC、具名受监督 owner 线程、切片级 one-shot/periodic `HostTimer`（`run_tick` 作为 timer 回调，不是 for-loop `run_tick`）。
- **verify_admission**：消费 `lumio.account-port.v1` 的 LumioBin + LumioSignatureV1 + Ed25519，不重写账号服。
- **Room world-slot**：绑定五元组、顶号、五分钟重连、过期墓碑、跨 Room 隔离、C-2 查询。Host NetEntityId 投影为 `nent_{instanceKey:x16}{seq:x16}`（进程实例前缀，销毁后不复用）；sessionId 是 `sess-*`，不得与 NetEntityId 互指。
- **Gameplay**：CoreCLR 加载 `entity-chat-host` 入口，再 `Assembly.LoadFrom` `ChatRoomWorld`。不把 ECS 语义搬进 Rust。Chat 上行在 Rust host 解码冻结 `InputCommand`（`chat.input` + `payloadSha256`）后再把 text 交给 `ChatRoomWorld`。
- **复跑**：`lumio-entity-chat-replay` 对 sibling Account Server 跑两轮；census 来自 host-audit 逐条 `nent_*`。S3 复用 Game `runPlaywrightBrowser`（真 Chromium，不注入 DOM）。S6 `tickSource`/`cadence` 为 `host-timer`。S7 `snapshotSource` 为 `lumio-entity-chat-replay`；Restore 落在同一 CoreCLR `ChatRoomWorld`，不新建 `LocalGameplay`，不回填 client chat window。
- **Game 对账器**：Game `1169a66` `verify-evidence.mjs` 接受 `lumio-mvp-host` **或** `lumio-entity-chat-replay`。本切片 SUCCESS 以该文件 `--dir` exit 0 为准。`modules/process/tests/verify_rust_evidence.mjs` 只是仓内分叉，不是 SUCCESS 谓词。不得把 `live-rust-host` 写入 snapshotSource allowlist。

## 待解决

- 51 张 Rust 主线（完整 host-runtime / world-slot crate 面）不在本切片。
- `lumio-mvp-host` FullGraph 连接预算在 Server PR #18 后是 **128**，仍不是本切片的 101 路产品路径（切片走 `EntityChatHost`，不克隆 FullGraph）。

## 相关

- 决策：[`../../decisions/0006-csharp-mvp-host-frozen-after-rust-identical-suite.md`](../../decisions/0006-csharp-mvp-host-frozen-after-rust-identical-suite.md)（取代 0005）
- 实现：[`../../../modules/process/src/entity_chat/`](../../../modules/process/src/entity_chat/)
- 托管入口：[`../../../entity-chat-host/`](../../../entity-chat-host/)
- Game 对账器：Game `integration/entity-chat/verify-evidence.mjs`（`1169a66`）
