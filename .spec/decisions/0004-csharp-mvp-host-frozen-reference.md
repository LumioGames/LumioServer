# 0004 · C# MVP host 在切片验收通过后冻结为 reference

- 日期:2026-09-02
- 状态:生效

## 背景

R-00359 要求在切片级最小 Rust host 上复跑与 R-00354 相同的 101-entity 验收套件。合约宿主无关，不得在本仓分叉。Owner 要求重叠窗口硬限制在这一切片：通过后 C# MVP host 退为 reference，而不是无限双轨。

## 决策

1. entity-chat 切片的交付宿主是 `modules/process` 的 Rust host（CoreCLR 加载同一份 C# `ChatRoomWorld`）。
2. `mvp-host/` C# MVP host 冻结为 reference：保留源码与 Hello 归档，不再作为本切片的交付面；整目录删除仍等到 51 张 Rust 主线。
3. 公共契约零本地补丁；合约缺陷走架构变更单。

## 后果

切片验收证据必须来自 Rust host 复跑（`lumio-entity-chat-replay` + CoreCLR `ChatRoomWorld`）。C# MVP 与 Game 仓 `GameRoomHost` 只作对照，不再扩展 hello-wire-v1。Game `verify-evidence.mjs` 仍锁定 `lumio-mvp-host` 时，以本仓 oracle 为 SUCCESS 谓词，不得为通过 Game 对账器而假冒进程名。
