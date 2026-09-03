# 0008 · C# MVP host 继续冻结为 reference；冻结条件改为 Rust 宿主已按 ADR-056 通过

- 日期:2026-09-03
- 状态:生效
- 取代:[0006](0006-csharp-mvp-host-frozen-after-rust-identical-suite.md)

## 背景

[0006](0006-csharp-mvp-host-frozen-after-rust-identical-suite.md) 在 R-00359 rust identical suite 通过后把 C# `mvp-host/` 冻结为 reference。架构仓 ADR-056 发现那次 suite 验收的不是设计里的 Runtime ECS / 单一绑定 / 内核定时 / 真广播与落盘，并要求：C# 冻结在 Rust 宿主按 ADR-056 重新通过前撤回。

R-00374（N-10）把 `lumio-entity-chat-replay` 改为消费 Runtime 绑定/查询/快照与 NativeCore `timer_*`。R-00376（N-12）live10 两包两轮 11 场景 `ok:true`，Game `verify-evidence.mjs --dir` pack-a/pack-b `ok:true`。R-00377（N-13）独立深审六项 Fixture 通过。

## 决策

1. C# `mvp-host/` **继续**冻结为 reference：保留源码，不是本切片交付面；整目录删除仍等到 51 张 Rust 主线。本卡不改 `mvp-host/` 实现。
2. 冻结条件现已满足：Rust `lumio-entity-chat-replay` 按 ADR-056 通过（live10 + Game oracle `--dir` 两包 `ok:true`）。0006 以 identical suite / `ChatRoomWorld` / `host-timer` 为交付面的前提作废。
3. 本切片 SUCCESS 宿主只能是 `lumio-entity-chat-replay`。不得假冒 `lumio-mvp-host`。Game oracle 以 N-12 合入 SHA 为准，不再是 0006 钉死的 `1169a66`。
4. `mvp-host/` 仍含 r2 路径（`RoomAdmissionRegistry`、`ITimerService`、`TakeoverNotice`、`ChatRoomWorldAdapter`）。那是冻结对照，不是 ADR-056 交付真值。Room 顶号交付面是 C-1 `ConnectionSuperseded`。
5. GitHub `MVP C# host policy`（CS0234）与 `Cargo entity-chat 11-scenario` 在 `3c65343` 上仍是 **FAILURE**。`11-scenario` job `continue-on-error: true`。两条都不是 SUCCESS；required `Cargo entity-chat acceptance` 为 success。本卡不把 CI 红改写成绿，不修 CS0234。

## 后果

交付面是 Rust entity-chat 宿主 + CoreCLR HostEntry 调 Runtime + NativeCore 定时内核。C# MVP 源码冻结留作对照，带着 CS0234 政策债。0007 描述的 Rust 纯消费路径仍是生效真值。
