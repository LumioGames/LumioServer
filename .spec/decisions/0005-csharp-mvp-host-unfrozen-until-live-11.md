# 0005 · C# MVP host 在 11 场景 live-green 之前不得冻结为 reference

- 日期:2026-09-02
- 状态:生效

## 背景

[0004](0004-csharp-mvp-host-frozen-reference.md) 在 R-00359 rust 切片复跑通过后把 C# MVP 冻结为 reference。R-00354 的 11 场景仍要在 `lumio-mvp-host` 上 live 执行：S5 五结局查询、S6 host timer tick、S7 last-message snapshot、S8 `nent_*` 重绑投影、S9 tombstone、S10 第二 Room、S11 event-order。Rust host 是 C-5 复跑目标，不是退役 C# 的许可证。

## 决策

1. 废止「切片验收通过后 C# MVP 即冻结」：在 11 场景于 `lumio-mvp-host` live-green 之前，C# MVP 仍是交付面。
2. Rust `lumio-entity-chat-replay` 保持 C-5 复跑目标与对照，不得假冒 `lumio-mvp-host` 进程名。
3. 不得把 `netEntityId` 写进 FullSnapshot body（ADR-045 exact-set）；`nent_*` 投影走 17-key host-audit 与 `GET /test-control/bindings`。
4. `ChatRoomWorld` 经 `Assembly.LoadFrom` 宿主，不复制 Game 源、不加 Game `ProjectReference`。

## 后果

- `mvp-host` 继续扩展 loopback test-control 与 nent 投影直到 11 场景 live-green。
- 整目录删除仍等到 51 张 Rust 主线。
