---
name: room-admission
description: Game Server Room 准入、绑定登记、nent 投影与 test-control 查询；改 verify_admission、顶号、过期或跨 Room 隔离时查
metadata:
  type: doc
  status: 已交付
---

# Room 准入与绑定登记

`mvp-host` 的 `Lumio.Server.MvpHost.Admission` 是 RM-00011 在 C# MVP 宿主上的 Room 准入与连接绑定登记：离线 `verify_admission`、Player/Bot 分类、五元组绑定、顶号重绑、五分钟断线保留与 Room 隔离。它是 Host 身份面，不是第二套 ECS。11 场景 live-green 之前 C# MVP 仍是交付面（[0005](../../decisions/0005-csharp-mvp-host-unfrozen-until-live-11.md)）；Rust `entity_chat` 是 C-5 复跑目标。

## 背景 / 目标

Game Server 只在 Account Server 准入凭证验证通过后才为连接分配运行时实体身份。实体种类由已认证 `loginName` + `botToolContext` 推导，客户端不得提交 `EntityType`。同一 AccountId 同时只有一条活跃绑定。

## 设计

- **verify_admission**：进程内端口。`AccountAdmissionVerifier` 调用 `account-server` 的 `AdmissionCredential.Verify`，不重写 Ed25519，也不接受用户名/口令。
- **分类**：`^Bot[0-9]+$` 且 `botToolContext=true` → Bot；否则 Player。Bot 命名空间且无工具上下文 → `bot_namespace_admission_forbidden`。
- **绑定五元组**：`{accountId, roomId, netEntityId, entityType: player|bot, connectionGeneration}`。AccountId 只作为身份属性值；记录不携带 AccountEntity 对象引用。
- **顶号**：仅当该 AccountId 已在**请求的同一 Room** 有活跃绑定（或同一连接的幂等重复）时才顶号：踢旧连接、发出 `TakeoverNotice`（`reasonCode=connection_superseded`）、重绑同一 `NetEntityId` 且 `connectionGeneration` 严格递增。
- **跨 Room**：已在另一 Room 存活的 AccountId 再 `Admit` 到不同 Room → `invalid_request` 拒绝。原 Room 绑定、`NetEntityId`、`connectionGeneration` 不变，旧连接不踢、不解绑、不分配新实体、不做跨 World 转移。
- **隔离**：解析与列表均限定单一 Room；对他 Room 实体返回 `cross_room_reference`。
- **重连窗**：断线后实体保留五分钟（Host `ITimerService` + 进程本地单调时钟，C-4）。该窗口不是 Native Tick/Frame。测试 Profile 可覆写 10 秒，须标明 test override。
- **输入**：仅拒绝已断线连接的输入；同 Room 其他连接与实体继续。断线实体对 Room 观察者显式 `Disconnected`。
- **重绑**：窗内新准入复用同一 `NetEntityId`，`connectionGeneration` 严格递增；复制 FullSnapshot 只含当前纪元，不含墓碑与旧代。这不是持久化 Snapshot/Restore。
- **过期**：到期墓碑化 A（`tombstoned`）；之后同 AccountId 登录创建 B，新的 `NetEntityId`。旧引用永不改指 B。
- **进程重启**：登记是进程内状态，不保留旧连接窗；重启后必须重新 login。`NetEntityId` 含进程实例前缀，避免重启后序号撞车。
- **组装**：`FullGraphComposition.Create` 经 `HostComposition.CreateRoomAdmissionRegistry` 接线 Host 时钟与 Timer。通道升级在共享密钥之外还接受 C-3 `admissionCredential`；握手路径 `Admit` 保留 `ConnectionBinding`，经 `LiveElevenHost.OnAdmitted` 投影 `nent_*` 到 17-key host-audit，并 `Assembly.LoadFrom` 宿主 sibling `ChatRoomWorld`。公钥来自 `LUMIO_ACCOUNT_ADMISSION_PUBLIC_KEY_HEX`（缺省则进程内临时密钥，仅供未对接账号服的 smoke）。契约真值是架构仓 `engine/wire/entity-binding-and-query-v1.json`（本仓镜像 `mvp-host/contract/`）。
- **test-control（loopback）**：`GET /test-control/bindings` 列出 `nent_*`；`POST` query/chat/tick/expire/snapshot/restore/room-admit 驱动 S5–S11。Tick 走 `ITimerService`，不 for-loop。FullSnapshot body 不加 `netEntityId`。

## 待解决

- 把 `TakeoverNotice` 推到游戏连接信封（C-1 / R-00355）。

## 相关

- 契约：架构仓 `engine/wire/entity-binding-query.v1`、`lumio.account-port.v1`
- 实现：[`mvp-host/src/Lumio.Server.MvpHost.Admission/`](../../../mvp-host/src/Lumio.Server.MvpHost.Admission/)
- 账号服：[`account-server.md`](account-server.md)
