# session 模块

> Session Admission 管道、Release 固定、重连窗口、ReplicationContext 句柄与 ServerConnectionSession 注册表。

## 模块定位与目标

`session` 拥有"一个连接如何成为会话、会话在断线后如何存续、会话属于哪个 WorldSlot"的全部编排。Session 一旦建立就精确固定 `productId + gameReleaseId`，跨越重连窗口保持身份与上下文。本模块的每连接记录（`ServerConnectionSession`）是 **Host 私有状态**：它不是、也不得被命名或建模为 Client 拥有的公共 `ClientReplicaSession` 状态机，不与其做状态映射，永不跨 wire（架构源 ADR-001，v1.1 明文）。Server 只保存远端 Client 的 Connection/Replication Context 句柄——Client ReplicaWorld 不是 Server WorldSlot 的物理对象。

## 负责什么

- Admission 管道：读取 [world-slot](../world-slot/README.md) 的 Host Admission Gate 状态（闸门关闭时稳定拒绝），串联 [auth](../auth/README.md)（身份/防重放/授权）、[release-agent](../release-agent/README.md)（`ExactRelease` 匹配）、world-slot（容量/配额裁决与绑定），产出接纳或稳定原因拒绝。
- Session 建立与 Release 固定：接纳后固定 `productId + gameReleaseId`，经 `bind_session` 绑定 WorldSlot，创建 per-client Connection/ReplicationContext **句柄**（Replication 内容语义归 Runtime）。
- 授权对象绑定：把 [auth](../auth/README.md) 产出的**不可变授权对象**（权限上下文快照，SRV-D-013）经类型化命令 `BindConnection` 交给 [transport](../transport/README.md) 与连接绑定；撤销/变更 = 下发新授权对象并递增连接 epoch，不做原地可变共享。
- 重连窗口：断线后保留 Session 元数据与 ReplicationContext 句柄至窗口超时（SRV-D-004，Timer 经 [host-runtime](../host-runtime/README.md) 投递）；**V1 不提供 Session Resume Token**（架构源 §7.3，v1.2 明文）——新连接代次的重连必须重新完成通道认证与完整 Handshake（经 auth 重校验并**重新派生**授权对象），同一连接内的 Resync 不重新握手；重连只能被路由到 Catalog 允许的目标 Release；重连从 Handshake/FullSnapshot 开始，除非有效 Baseline 被显式保留（架构源 ADR-009）。窗口到期与重连的竞争在本模块的命令队列上串行裁决：以先到达的类型化命令为准，输者收到稳定错误。
- Session 终结执行：正常关闭、`SessionLocalProven` 故障的隔离终结（命令来自 world-slot 裁决）、`MaintenanceKick` 执行（命令来自 [maintenance-agent](../maintenance-agent/README.md)；断开与广播经 transport 类型化命令），全部写 Audit。
- Drain 进度上报：维护期间向 maintenance-agent 报告存量 Session 计数。

## 明确不负责什么

- 不拥有 Host Admission Gate（归 [world-slot](../world-slot/README.md)；本模块只读闸门并执行 Admission 管道，"停止接入"是聚合根的闸门动作，不是本模块的开关）。
- 不做身份认证与权限裁决（归 [auth](../auth/README.md)）；不做版本匹配裁决（归 [release-agent](../release-agent/README.md)）。
- 不写连接注册表（注册表唯一写入者是 [transport](../transport/README.md)；本模块经 `BindConnection/UnbindConnection` 类型化命令请求变更并接收结果 ack）。
- 不拥有 SimulationSession（Runtime 拥有逻辑模拟上下文）；不拥有 Replication 语义（FullSnapshot/Delta/BaselineAck/Resync 状态机归 Runtime；本模块只持有 Context 句柄并编排其创建/销毁）。
- 不拥有传输连接（归 transport）；Session 与 Connection 是两层身份，断线时 Connection 消亡而 Session 可存续。
- 不裁决故障分级（归 world-slot 依据 Runtime 见证裁决；本模块只执行隔离终结命令）。
- 不决定维护语义（归 maintenance-agent）；只执行"踢出"的机械动作。

## 拥有的状态与资源

- `ServerConnectionSession` 注册表：`sessionId -> { 固定的 productId + gameReleaseId、WorldSlot 绑定、授权对象引用、Connection 绑定 + 连接 epoch、重连窗口 deadline }`。
- per-client Connection/ReplicationContext 句柄（内容归 Runtime）。
- Session 命令收件箱（有界，SRV-D-015 约定）与拒绝原因统计。

## 输入、输出与稳定接口

- **输入**：transport 的 `HandshakeReady`（已过结构校验的新连接握手）与 `ConnectionClosed` 事件、重连请求、world-slot 的 `GateStateChanged` 事件与隔离终结命令、maintenance-agent 的 `KickRemaining` 命令、host-runtime 投递的窗口到期命令。
- **输出**：Admission 裁决（接纳/稳定原因拒绝）、对 transport 的 `BindConnection/UnbindConnection/Disconnect` 类型化命令、对 world-slot 的 `bind_session/release_session` 调用、Session 生命周期 Audit 事件、Drain 进度。
- **稳定接口**：`admit(handshake) -> SessionRef | StableReason`；`reconnect(sessionId, credentials) -> SessionRef | StableReason`；`kick(sessionId | poolScope, MaintenanceKick | FaultReason)`；`drain_progress() -> ActiveSessionCount`。

## 上游与下游依赖

- **上游**：[maintenance-agent](../maintenance-agent/README.md)（踢人命令、drain 查询）、[world-slot](../world-slot/README.md)（隔离终结命令、闸门事件——事件流向，编译依赖仍是 session -> world-slot）。
- **下游**：[auth](../auth/README.md)、[release-agent](../release-agent/README.md)、[world-slot](../world-slot/README.md)、[transport](../transport/README.md)、[host-runtime](../host-runtime/README.md)、[observability](../observability/README.md)。

## 生命周期与状态机

`ServerConnectionSession` 状态机（本仓私有细化设计；命名与公共 `ClientReplicaSession` 严格区隔，不做状态映射）：

```text
Admitted -> Syncing（Runtime 执行 FullSnapshot/BaselineAck 序列，本模块只观察完成事件）
 -> Active
Active -> ReconnectWindow（连接断开，Session 存续）
ReconnectWindow -> Syncing（重连成功，重新同步）
ReconnectWindow -> Expired（窗口超时，Session 终结）
任一状态 -> Closed（正常关闭）/ Kicked（维护/管理）/ Faulted（SessionLocalProven 隔离）
```

- 状态迁移只能由本模块发起；Runtime/Gameplay 回调不能改变本状态机。
- Session 终结后的迟到命令以稳定错误拒绝（连接 epoch 校验），不得改写新 Session。
- 同步进度的语义状态（Baseline 是否有效、Resync 原因）归 Runtime；`Syncing` 只表示"本模块在等待 Runtime 的完成事件"，不复制 Runtime 内部状态。

## 线程、队列与并发所有权

- 无自有线程；Admission 在编排路径执行，注册表变更与窗口到期/重连竞争在有界命令收件箱上串行裁决（SRV-D-015 约定），不做跨线程可变共享。
- 不拥有消息队列；Ingress/Egress 归 [transport](../transport/README.md)，本模块只管理会话身份与绑定关系。

## 正常数据流与失败路径

- **正常**：握手 → 读 Gate → `admit`（auth → release-agent → world-slot 绑定）→ Session 建立并固定 Release → 授权对象经 `BindConnection` 绑定 → Runtime 开始 FullSnapshot 序列 → `Active` → 正常关闭。
- **失败路径**：
  - Gate 关闭：以维护原因稳定拒绝并附剩余宽限信息。
  - 认证失败/版本不匹配/容量不足：稳定原因拒绝，写 Audit，不产生半建 Session。
  - 断线：进入 `ReconnectWindow`，保留元数据；窗口超时 `Expired` 并释放全部句柄。
  - 窗口到期与重连同刻竞争：命令队列串行裁决，后到者稳定拒绝。
  - 重连携带过期 Baseline：走 Full Resync（`resyncReason` 枚举归架构源 Envelope Schema）。
  - `SessionLocalProven` 隔离命令：终结该 Session，其他 Session 不受影响。
  - Audit 背压（observability 状态事件）：报告给 world-slot 聚合根，由聚合根决定关闸；本模块不自行开关接入。

## 错误分类、恢复与降级

- **可重试**：容量暂满（客户端可退避重试）。
- **可拒绝**：认证失败、Release 不匹配、重放、Gate 关闭、窗口过期后的重连、旧连接 epoch 的迟到命令。
- **可致命**：无本模块独立致命路径；注册表不一致按进程级诊断上报。
- **降级**：无隐式降级；接入收紧是聚合根闸门动作且可审计。

## 配置、Capability 与安全约束

- 重连窗口时长与保留资源上限来自不可变配置快照（SRV-D-004）。
- V1 默认不接受跨 Release 连接（D-007）；`compatibilityPolicy` 字段虽预留 `DeclaredNMinusOne`，启用需新 ADR、握手规则与 Fixture。
- 重连必须经 auth 重校验并重新派生授权对象；Session 存续不等于身份或权限豁免（SRV-D-013）。

## 日志、Metrics、Trace 与 Audit

- Session 建立/重连/踢出/过期全部写 Audit（correlation `scope` 为 `Session`，关联 `sessionId`、`productId`、`gameReleaseId`、必要时 `maintenanceId`）。
- Metrics：活跃 Session 数、Admission 接纳/拒绝率（按稳定原因）、重连成功率、窗口过期数、竞争裁决计数。
- 全部维护断开与恢复动作进入 Audit 与 Failure Bundle（架构源 §13.3）。

## 测试面、故障矩阵与性能指标

- **测试面**：Admission 全链路（含每个拒绝分支与 Gate 关闭分支）、握手/重连、Session/WorldSlot 绑定、重复 dispose、终结后迟到命令拒绝（epoch）、窗口到期与重连竞争、隔离终结不外溢、LocalEmbedded 两棵树隔离下的 Session 独立性。
- **故障矩阵**：断线/重连风暴、窗口边界竞争、维护踢人与重连路由、授权对象撤销后旧对象失效。
- **性能指标**：Admission 延迟 p99、重连恢复时长、200 Bot Workload 下注册表操作开销。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-001-session-lifecycle.md`（聚合根、Host 私有连接记录、epoch 语义）、`docs/adr/ADR-005-replication-prediction.md`（FullSnapshot/BaselineAck/Resync 编排背景）、`docs/adr/ADR-009-local-transport.md`（重连从 Handshake 开始）、`docs/adr/ADR-012-release-update-maintenance.md`（重连路由与踢人）。
- 架构源 `schemas/common.schema.json`（`sessionRevisionVector`）：正例 `fixtures/valid/session-revision-vector.json`，反例 `fixtures/invalid/session-revision-negative.json`。
- 架构源 `schemas/replication-envelope.schema.json`（`messageType`、`resyncReason`）：正例 `fixtures/valid/replication-full-snapshot.json`。

## 尚未批准的决策门

- **SRV-D-004**（重连窗口时长与保留资源上限）：临时默认值为 120 秒窗口、窗口内保留 Session/ReplicationContext 元数据；Vertical Slice 阶段结合真实断线数据确认。
- **SRV-D-013**（授权对象派生与撤销语义）：临时默认值为接纳时派生不可变授权对象、重连重派生、撤销走连接 epoch 递增；安全评审确认。
- **D-007**（N/N-1 兼容窗口）：临时默认值为拒绝——精确 `productId + gameReleaseId` 匹配 + 强制更新路径。均登记于 [modules/README.md](../README.md) §11。
