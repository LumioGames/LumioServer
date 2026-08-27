# protocol-dispatch 模块（预留，未解锁）

> 生成式 RPC/Message 分发边界的占位声明——公共 dispatch 契约冻结（公共决策门 D-009）之前，本模块不得动工。

## 模块定位与目标

`protocol-dispatch` 预留"从 Envelope `messageType` 到类型化处理入口"的生成式分发层。V1 当前的公共 wire 面只有架构源 `schemas/replication-envelope.schema.json` 声明的复制 MessageTypes；不存在公共的 MessageId 命名空间、RPC envelope 或生成式 dispatch 契约。在架构源以新 ADR + Schema + Fixture + 新 BaselineId 冻结该契约（D-009）之前，本仓不得私造任何 dispatch wire 格式或手写第二套消息路由。

设立本占位的目的：把"未来一定会出现的分发需求"显式钉在一个被封锁的边界上，防止它在实现期悄悄长进 [transport](../transport/README.md)（触碰消息语义）或 [session](../session/README.md)（长出路由表）。

## 负责什么（解锁后）

- 消费架构源生成的 dispatch 契约（MessageId 注册表、payload Schema、协议 epoch），生成或装配服务器侧类型化分发表。
- 把解码后的消息按契约路由到声明的处理入口；未注册消息以稳定错误拒绝。
- 协议 epoch 校验与不匹配拒绝。

## 明确不负责什么

- 不定义 MessageId、RPC envelope、payload Schema 或协议 epoch（归架构源，D-009）。
- 不做 Envelope 结构校验、可靠性、分片（归 [transport](../transport/README.md)）。
- 不做权限过滤（语义归 [auth](../auth/README.md)，执行点在 transport）。
- 不承载复制语义（FullSnapshot/Delta/Resync 归 Runtime）。

## 当前状态与解锁条件

- **状态**：目录与本 README 是唯一产物；无接口、无实现、无内部契约。
- **解锁条件**（全部满足）：
  1. 架构源新增 dispatch ADR 并 Accepted；
  2. `schemas/` 新增 dispatch 契约 Schema 与正反 Fixture；
  3. 新 BaselineId 发布并同步七仓镜像；
  4. 本仓按新基线补全本 README 的全部标准章节（状态、接口、队列、故障矩阵）。
- 在此之前，任何模块 README、调用链或代码把本模块作为**可用依赖**引用都是门审驳回项（登记其封锁状态的中心文档条目除外）。

## 对应 ADR、Schema 与 Fixture

- 待定：架构源 `docs/architecture/DECISIONS_PENDING.md` D-009 是唯一权威登记处。注意：架构源 ADR-022（v1.2）已冻结生成式 Active 消息的 Protocol/Permission 门**字段集**，但未裁决 dispatch 契约本身——D-009 仍未解决，本模块仍封锁。
- 现存唯一相邻契约：`schemas/replication-envelope.schema.json`（`messageType` 枚举，归 [transport](../transport/README.md) 消费）。

## 尚未批准的决策门

- **D-009**（RPC/Message dispatch 契约）：临时默认值为不冻结、本模块封锁；解锁条件见上节。登记见 [modules/README.md](../README.md) §11.1。
