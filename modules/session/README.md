# session 模块

> Session Admission、Release 固定、重连窗口、ReplicationContext 句柄与 Session 到 WorldSlot 的路由。

## 模块定位与目标

`session` 拥有"一个连接如何成为会话、会话在断线后如何存续、会话属于哪个 WorldSlot"的全部编排。Session 一旦建立就精确固定 `productId + gameReleaseId`，跨越重连窗口保持身份与上下文；Server 只保存远端 Client 的 Connection/Replication Context——Client ReplicaWorld 不是 Server WorldSlot 的物理对象（架构源 §3.1、ADR-001）。

## 负责什么

- Admission 管道：串联 [auth](../auth/README.md)（身份/防重放/权限）、[release-router](../release-router/README.md)（`ExactRelease` 匹配与 Pool 路由）、[world-slot](../world-slot/README.md)（容量/配额检查），产出接纳或稳定原因拒绝。
- Session 建立与 Release 固定：接纳后固定 `productId + gameReleaseId`，绑定 WorldSlot，创建 per-client Connection/ReplicationContext **句柄**（Replication 内容语义归 Runtime）。
- 权限上下文绑定：把 [auth](../auth/README.md) 产出的权限上下文写入连接注册表，供 [network](../network/README.md) 在解码后过滤（见 [modules/README.md](../README.md) §3.2 分工）。
- 重连窗口：断线后保留 Session 元数据与 ReplicationContext 句柄至窗口超时（SRV-D-004）；窗口内重连需经 auth 重校验，重连只能被路由到 Catalog 允许的目标 Release；重连从 Handshake/FullSnapshot 开始，除非有效 Baseline 被显式保留（架构源 ADR-009）。
- Admission 开关：维护/Drain/Audit 背压时停止新接入（受 [maintenance](../maintenance/README.md) 编排与 [observability](../observability/README.md) 背压状态驱动）。
- Session 终结执行：正常关闭、Session Fault 隔离踢出、`MaintenanceKick` 执行（广播经 [network](../network/README.md)），全部写 Audit。

## 明确不负责什么

- 不做身份认证与权限裁决（归 [auth](../auth/README.md)）；不做版本匹配裁决（归 [release-router](../release-router/README.md)）。
- 不拥有 SimulationSession（Runtime 拥有逻辑模拟上下文；`SimulationSession` 不等于远端 Client 对象，架构源 §1.2）。
- 不拥有 Replication 语义（FullSnapshot/Delta/BaselineAck/Resync 状态机归 Runtime；本模块只持有 Context 句柄并编排其创建/销毁）。
- 不拥有传输连接（归 [network](../network/README.md)）；Session 与 Connection 是两层身份，断线时 Connection 消亡而 Session 可存续。
- 不决定维护语义（归 [maintenance](../maintenance/README.md)）；只执行"停止接入/踢出"的机械动作。

## 拥有的状态与资源

- Session 注册表：`sessionId -> { 固定的 productId + gameReleaseId、WorldSlot 绑定、权限上下文引用、Connection 绑定、重连窗口截止 }`。
- per-client Connection/ReplicationContext 句柄（内容归 Runtime）。
- Admission 开关状态与拒绝原因统计。

## 输入、输出与稳定接口

- **输入**：`network` 转交的新连接握手、重连请求、编排层的 Admission 开关指令、`MaintenanceKick` 执行指令、Slot/CoreCLR 上报的 Session Fault。
- **输出**：Admission 裁决（接纳/稳定原因拒绝）、Session 生命周期事件（Audit）、连接注册表更新（权限上下文绑定）、Session 终结动作。
- **稳定接口**：`admit(handshake) -> SessionRef | StableReason`；`reconnect(sessionId, credentials) -> SessionRef | StableReason`；`close_admission(scope, reason)` / `open_admission(scope)`；`kick(sessionId | poolScope, MaintenanceKick | FaultReason)`。

## 上游与下游依赖

- **上游**：[maintenance](../maintenance/README.md)（停止接入、踢人编排）。
- **下游**：[auth](../auth/README.md)、[release-router](../release-router/README.md)、[world-slot](../world-slot/README.md)、[network](../network/README.md)、[observability](../observability/README.md)。

## 生命周期与状态机

Server 侧 Session 状态机（本仓细化设计；与公共 `ClientReplicaSession` 状态机——`Disconnected/Connecting/Negotiating/Synchronizing/Active/...`，架构源 §3.2——的对应关系标注如下）：

```text
Admitted（对应 Client 侧 Negotiating 通过）
 -> Handshaking（FullSnapshot/BaselineAck 进行中，对应 Synchronizing）
 -> Active（对应 Active/Resyncing）
Active -> ReconnectWindow（连接断开，Session 存续）
ReconnectWindow -> Handshaking（重连成功，重新同步）
ReconnectWindow -> Expired（窗口超时，Session 终结）
任一状态 -> Closed（正常关闭）/ Kicked（维护/管理）/ Faulted（Session Fault）
```

- 状态迁移只能由本模块发起；Runtime/Gameplay 回调不能改变 Session 状态机（架构源 §3.2 所有权规则）。
- Session 终结后的迟到回调以稳定错误拒绝，不得改写新 Session（架构源 ADR-001 失败语义）。

## 线程、队列与并发所有权

- 无自有线程；Admission 在编排路径执行，Session 注册表的运行期读写以细粒度同步保护。
- 不拥有消息队列；Ingress/Egress 归 [network](../network/README.md)，本模块只管理会话身份与绑定关系。

## 正常数据流与失败路径

- **正常**：握手 → `admit`（auth → release-router → world-slot 容量）→ Session 建立并固定 Release → 权限上下文绑定 → Runtime 开始 FullSnapshot 序列 → `Active` → 正常关闭。
- **失败路径**：
  - 认证失败/版本不匹配/容量不足：稳定原因拒绝，写 Audit，不产生半建 Session。
  - 断线：进入 `ReconnectWindow`，保留元数据；窗口超时 `Expired` 并释放全部句柄。
  - 重连携带过期 Baseline：走 Full Resync（`resyncReason` 枚举归架构源 Envelope Schema）。
  - Session Fault（来自 coreclr-host 分级）：隔离踢出该 Session，其他 Session 不受影响。
  - Admission 关闭期间的新握手：以维护原因稳定拒绝并附截止时间信息。

## 错误分类、恢复与降级

- **可重试**：容量暂满（客户端可退避重试）。
- **可拒绝**：认证失败、Release 不匹配、重放、Admission 关闭、窗口过期后的重连。
- **可致命**：无本模块独立致命路径；注册表不一致按进程级诊断上报。
- **降级**：无隐式降级；Admission 收紧（背压/维护）是显式编排动作且可审计。

## 配置、Capability 与安全约束

- 重连窗口时长与保留资源上限来自不可变配置快照（SRV-D-004）。
- V1 默认不接受跨 Release 连接（D-007）；`compatibilityPolicy` 字段虽预留 `DeclaredNMinusOne`，启用需新 ADR、握手规则与 Fixture。
- 重连必须经 auth 重校验；Session 存续不等于身份豁免。

## 日志、Metrics、Trace 与 Audit

- Session 建立/重连/踢出/过期全部写 Audit（关联 `sessionId`、`productId`、`gameReleaseId`、`releasePoolId`、必要时 `maintenanceId`）。
- Metrics：活跃 Session 数、Admission 接纳/拒绝率（按稳定原因）、重连成功率、窗口过期数。
- 全部维护断开与恢复动作进入 Audit 与 Failure Bundle（架构源 §13.3）。

## 测试面、故障矩阵与性能指标

- **测试面**：Admission 全链路（含每个拒绝分支）、握手/重连、Session/WorldSlot 绑定、重复 dispose、终结后迟到回调拒绝、LocalEmbedded 两棵树隔离下的 Session 独立性（架构源 ADR-001 验证清单）。
- **故障矩阵**：断线/重连风暴、窗口边界竞争（超时瞬间重连）、维护踢人与重连路由、Session Fault 隔离不外溢。
- **性能指标**：Admission 延迟 p99、重连恢复时长、200 Bot Workload 下注册表操作开销。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-001-session-lifecycle.md`（Session/World/Host 所有权与状态机）、`docs/adr/ADR-005-replication-prediction.md`（FullSnapshot/BaselineAck/Resync 编排背景）、`docs/adr/ADR-009-local-transport.md`（重连从 Handshake 开始）、`docs/adr/ADR-012-release-update-maintenance.md`（重连路由与踢人）。
- 架构源 `schemas/common.schema.json`（`sessionRevisionVector`）：正例 `fixtures/valid/session-revision-vector.json`，反例 `fixtures/invalid/session-revision-negative.json`。
- 架构源 `schemas/replication-envelope.schema.json`（`messageType`、`resyncReason`）：正例 `fixtures/valid/replication-full-snapshot.json`。

## 尚未批准的决策门

- **SRV-D-004**（重连窗口时长与保留资源上限）：临时默认值为 120 秒窗口、窗口内保留 Session/ReplicationContext 元数据；Vertical Slice 阶段结合真实断线数据确认。登记见 [modules/README.md](../README.md) §11.2。
- **D-007**（N/N-1 兼容窗口）：临时默认值为拒绝——精确 `productId + gameReleaseId` 匹配 + 强制更新路径；开放窗口需新 ADR、握手规则与 Fixture。登记见 [modules/README.md](../README.md) §11.1。
