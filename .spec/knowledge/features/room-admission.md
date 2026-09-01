---
name: room-admission
description: Game Server Room 准入与 Player/Bot 绑定登记；改 verify_admission、顶号或跨 Room 隔离时查
metadata:
  type: doc
  status: 已交付
---

# Room 准入与绑定登记

`mvp-host` 的 `Lumio.Server.MvpHost.Admission` 是 RM-00011 在 C# MVP 宿主上的 Room 准入与连接绑定登记：离线 `verify_admission`、Player/Bot 分类、五元组绑定、顶号重绑与 Room 隔离。它是 Host 身份面，不是第二套 ECS。

## 背景 / 目标

Game Server 只在 Account Server 准入凭证验证通过后才为连接分配运行时实体身份。实体种类由已认证 `loginName` + `botToolContext` 推导，客户端不得提交 `EntityType`。同一 AccountId 同时只有一条活跃绑定。

## 设计

- **verify_admission**：进程内端口。`AccountAdmissionVerifier` 调用 `account-server` 的 `AdmissionCredential.Verify`，不重写 Ed25519，也不接受用户名/口令。
- **分类**：`^Bot[0-9]+$` 且 `botToolContext=true` → Bot；否则 Player。Bot 命名空间且无工具上下文 → `bot_namespace_admission_forbidden`。
- **绑定五元组**：`{accountId, roomId, netEntityId, entityType: player|bot, connectionGeneration}`。AccountId 只作为身份属性值；记录不携带 AccountEntity 对象引用。
- **顶号**：同一账号第二条已认证准入踢旧连接，发出 `TakeoverNotice`（`reasonCode=connection_superseded`），同 Room 重绑同一 `NetEntityId` 且 `connectionGeneration` 严格递增。
- **隔离**：解析与列表均限定单一 Room；对他 Room 实体返回 `cross_room_reference`。
- **组装**：`Lumio.Server.MvpHost.App` 经 `HostComposition.CreateRoomAdmissionRegistry` 接线。契约真值是架构仓 `engine/wire/entity-binding-and-query-v1.json`（本仓镜像 `mvp-host/contract/`）。

## 待解决

- 重连保留窗、过期销毁与墓碑（R-00350 / C-4 Timer）。
- 把 `TakeoverNotice` 推到游戏连接信封（C-1 / R-00355）。

## 相关

- 契约：架构仓 `engine/wire/entity-binding-query.v1`、`lumio.account-port.v1`
- 实现：[`mvp-host/src/Lumio.Server.MvpHost.Admission/`](../../../mvp-host/src/Lumio.Server.MvpHost.Admission/)
- 账号服：[`account-server.md`](account-server.md)
