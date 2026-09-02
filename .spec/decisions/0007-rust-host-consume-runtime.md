# 0007 · Rust entity-chat 宿主改为纯消费 Runtime 与 NativeCore

- 日期:2026-09-02
- 状态:生效
- 取代:[0003](0003-host-reconnect-window.md) 在 Rust entity-chat 路径上的「Host Timer 持有五分钟窗」

## 背景

ADR-056 要求同一职责一份实现。Rust `host.rs` 曾自有绑定表、发号、查询 switch、`expire_due` 轮询与进程内聊天窗。

## 决策

Rust entity-chat 宿主只保留连接会话表与网线。绑定/发号/查询/FullSnapshot/Delta/Persist 经 CoreCLR 调 Runtime；五分钟到期与 Tick 经 NativeCore `timer_*` ABI（wallClock / tickFrame）。顶号先发 C-1 `ConnectionSuperseded` 再关旧连接。

## 后果

C# `mvp-host/` 仍按 N-13 冻结，不在本决策内改写。0002 描述的 C# 绑定登记仍是 mvp-host 现状，不是 Rust 路径真值。
