# 0002 · Room 准入做成 Host 绑定登记，不引入第二套 ECS

- 日期：2026-09-01
- 状态：生效

## 背景

R-00346 要求 Game Server 在准入后为 Player/Bot 建立隔离 Room 内的运行时身份，并执行顶号重绑。LumioServer 不拥有 GameRuntime ECS 存储；`EcsWorld` 创建 API 为内部接口。把 ChatRoomWorld 或 LumioGameRuntime 拉进本仓会越过仓库边界。

## 决策

1. 在 `mvp-host` 新增 `Lumio.Server.MvpHost.Admission`：登记绑定五元组、分配永不复用的 `NetEntityId`、按 loginName/botToolContext 分类、发出显式 `TakeoverNotice`。
2. 不引用 LumioGameRuntime，不复制 LumioGame 的 ChatRoomWorld，不给 `Simulation.Reference` 增加 Player/Bot/ECS 词汇。
3. `verify_admission` 复用 `Lumio.Server.Account.AdmissionCredential.Verify`。Architecture.Tests 把该库纳入构建图（层号 3），而不是放宽「构建图外」断言或复制 Ed25519。

## 后果

- Host 侧可验收 101 实体主迹、顶号、跨 Room 隔离与 Bot 命名空间拒绝，而不拥有组件存储。
- 查询面的属性三维与墓碑/重连窗仍归后续卡片；本登记只提供绑定与解析所需的身份属性。
