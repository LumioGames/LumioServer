# 0006 · C# MVP host 冻结为 reference，identical suite 以 Rust replay 为交付面

- 日期:2026-09-02
- 状态:生效

## 背景

R-00354 已在 C# `lumio-mvp-host` 上 live-green。R-00359 要求切片级 Rust host 用同一份 Game origin/main `1169a66` `verify-evidence.mjs` 作为 SUCCESS 谓词，不得假冒 `lumio-mvp-host`。两轮独立证据包已在 `lumio-entity-chat-replay` 上通过（真 Playwright、host-timer tick、同一 CoreCLR 上的 last-message Restore）。

## 决策

1. C# `mvp-host/` 冻结为 reference：保留源码，不再作为本切片交付面；整目录删除仍等到 51 张 Rust 主线。
2. 本切片 SUCCESS 对账器是 Game `1169a66` `verify-evidence.mjs`。`hostProcess.process` 必须是 `lumio-entity-chat-replay`。
3. Playwright 为真实 Chromium；S6 `tickSource`/`cadence` 为 `host-timer`；S7 `snapshotSource` 为 `lumio-entity-chat-replay`。Restore 在同一 `EntityChatHost` / CoreCLR `ChatRoomWorld` 上执行，不新建 `LocalGameplay`。
4. 不得把 `nent_*` 写入 FullSnapshot body（ADR-045 exact-set）；不得把 `live-rust-host` 加入任何 allowlist。

## 后果

`mvp-host/` 源码保留作对照。Rust `EntityChatHost` + CoreCLR `ChatRoomWorld` + 真 Account Server 是 identical suite 的交付宿主。
