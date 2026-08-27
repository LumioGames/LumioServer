# network 模块

> Reactor、Envelope 结构校验、可靠性/分片/Ack、限流背压、Ingress/Egress 有界队列与传输 Adapter。

## 模块定位与目标

`network` 是进程与外界字节流之间的唯一边界。它把不可信输入在分配前拒绝、把可信消息放进有界队列、把 Tick 产物送回网络——并且**从不**跨过队列直接触碰 Gameplay。所有传输实现（Socket/TLS/InMemory）都在 Adapter 之后，供应商选择是决策门 D-004，不属于本模块的稳定契约。

## 负责什么

- Reactor：监听 Endpoint 绑定、连接接受、读写事件驱动（实现框架属 D-004，行为契约在此冻结）。
- Envelope 结构校验：按架构源 `schemas/replication-envelope.schema.json` 校验 `protocolVersion`、`length`、`sequence`、`sessionId`、`productId`、`gameReleaseId`、`messageType`、`reliability`、`integrity`、`traceId`；最大长度与畸形输入在**分配前**拒绝（架构源 §7.3、ADR-005）。
- 可靠/不可靠通道：Transport ACK、重传、分片与重组；Transport ACK 与 Replication Baseline ACK 严格分离（Baseline ACK 语义归 Runtime）。
- 限流与背压：每连接速率限制（SRV-D-006）、可靠积压阈值降级/断开（SRV-D-002）。
- 有界队列所有权：per-session Ingress（SRV-D-001）与 Egress（SRV-D-002）队列的容量、优先级、满载动作与 Metrics。
- 连接级权限过滤的**执行点**：解码后、入队前，按连接注册表中绑定的权限上下文过滤 `messageType`；权限语义归 [auth](../auth/README.md)，绑定动作归 [session](../session/README.md)（见 [modules/README.md](../README.md) §3.2）。
- 传输 Adapter 接口：暴露消息批与三类稳定错误（可重试、可拒绝、可致命）；InMemory Adapter 服务 LocalEmbedded，且不绕过 Envelope/Codec/大小限制/队列（架构源 ADR-009）。
- 网络故障注入的执行：按 [host-profiles](../host-profiles/README.md) 声明的 Fault Decorator（延迟/抖动/丢包/乱序/重复/断线/重连/QueueFull）作用于 Adapter 层。

## 明确不负责什么

- 不做认证决策、票据校验或防重放判定（归 [auth](../auth/README.md)）。
- 不做 Admission、Session 生命周期或路由（归 [session](../session/README.md)、[release-router](../release-router/README.md)）。
- 不调用任何 Gameplay/Runtime 入口；网络线程的唯一出口是有界队列。
- 不定义 Envelope Schema、`messageType` 枚举或 `resyncReason`（归架构源）；不把第三方网络类型写入稳定契约。
- 不拥有 Replication 语义（FullSnapshot/Delta/Resync 的内容与状态归 Runtime；本模块只搬运）。

## 拥有的状态与资源

- 监听 Endpoint、连接注册表（传输句柄、限流计数、权限上下文的只读绑定）。
- per-session Ingress/Egress 有界队列及其满载策略状态。
- 可靠通道的重传缓冲、分片重组缓冲（均有大小上限）。
- 传输 Adapter 实例与 Fault Decorator 状态（含确定性 Seed）。

## 输入、输出与稳定接口

- **输入**：外部字节流（不可信）、Egress 队列中的出站消息、`session` 下发的连接绑定/解绑指令、`maintenance` 经编排触发的广播（如 `MaintenanceKick`）。
- **输出**：Ingress 队列中已过校验的消息批（供 Simulation Owner Thread 消费）、出站字节流、连接级稳定错误。
- **稳定接口**：`bind(endpoint)`、`enqueue_egress(sessionId, envelope)`、`drain_ingress(slotId, budget) -> Batch`、`disconnect(sessionId, stableReason)`、`broadcast(poolScope, envelope)`。

## 上游与下游依赖

- **上游**：[session](../session/README.md)（连接绑定与断开指令）、[release-router](../release-router/README.md)（Endpoint 配置）、[maintenance](../maintenance/README.md)（经编排的广播/断开）。
- **下游**：[host-profiles](../host-profiles/README.md)（传输/故障 Profile 查询）、[observability](../observability/README.md)（事件与 Metrics）。

## 生命周期与状态机

- 连接（传输层，本仓细化设计）：`Accepted -> EnvelopeValidated -> Bound（绑定 Session 与权限上下文）-> Active -> Draining -> Closed`；任一状态可因可致命错误进入 `Closed(fault)`。
- 模块整体随 [process](../process/README.md) 的 `Serving/Draining` 状态开关监听与新连接接受。

## 线程、队列与并发所有权

- 拥有 Reactor 线程与发送线程（数量为部署配置）。
- Ingress 队列：生产者是 Reactor 线程，消费者是 Simulation Owner Thread（[world-slot](../world-slot/README.md) 拥有该线程）；单生产者-单消费者边界清晰。
- Egress 队列：生产者是 Simulation Owner Thread（`EgressPublish` 之后），消费者是发送线程。
- 队列容量、优先级、满载动作、Metrics 是每个队列的必备声明；禁止无界增长（架构源 §4.3）。

## 正常数据流与失败路径

- **正常入站**：字节流 → 长度/版本/完整性校验 → 解码 → 权限过滤 → per-session Ingress 入队 → Tick 消费。
- **正常出站**：`EgressPublish` → Egress 队列 → 可靠性处理（ACK/重传/分片）→ 发送。
- **失败路径**：
  - 超长/畸形/完整性失败：分配前拒绝，回稳定错误，计数；重复出现按限流策略断开。
  - Ingress 满：Unreliable 丢弃并计数；Reliable 按 SRV-D-001 断开连接（不静默丢可靠消息，架构源 ADR-009）。
  - Egress 可靠积压超阈值：先降速后断开（SRV-D-002）。
  - 限流超限：先延迟后断开（SRV-D-006）。
  - 传输层故障（对端断开、IO 错误）：进入重连路径——重连从 Handshake/FullSnapshot 开始，除非有效 Baseline 被显式保留（架构源 ADR-009）。

## 错误分类、恢复与降级

- **可重试**：瞬时 IO 错误、发送窗口暂满。
- **可拒绝**：畸形 Envelope、超大小上限、未通过权限过滤、限流超限——回稳定错误码。
- **可致命**：监听绑定失败、Reactor 资源耗尽——上报 [process](../process/README.md) 按进程级处置。
- **降级**：可靠积压降速、Unreliable 丢弃、Diagnostic 采样；全部降级动作可在 Metrics 观测。

## 配置、Capability 与安全约束

- Endpoint、Reactor 线程数、队列容量、限流参数来自不可变配置快照。
- 传输 Profile（是否走 Socket/TLS、是否 InMemory）由 [host-profiles](../host-profiles/README.md) 声明；LocalEmbedded 不得绕过 Schema/Codec/Envelope/权限/大小限制/队列。
- 认证、防重放、限流、背压和审计不能被本地快捷路径跳过（本仓 [repository-architecture.md](../../.spec/knowledge/standards/repository-architecture.md)）。

## 日志、Metrics、Trace 与 Audit

- Metrics：连接数、每队列深度/丢弃/满载次数、重传率、分片率、限流触发数、复制字节与重传字节（对应架构源 ADR-016 指标清单）。
- 断开与拒绝事件带稳定原因写 Diagnostic；维护踢人（`MaintenanceKick`）的广播结果写 Audit（由 maintenance 编排关联 `maintenanceId`）。
- 网络时间戳与队列状态进入 Diagnostic Hash，不进权威 Simulation Hash。

## 测试面、故障矩阵与性能指标

- **测试面**：Wire Envelope 编解码、可靠性/分片/Ack、限流、背压、认证前过滤、防重放联动、网络故障注入（Fault Decorator 全谱系）、LocalEmbedded 与 LocalSplitProcess 同命令流对比。
- **故障矩阵**：丢包/乱序/重复/断线/重连、QueueFull、超长消息、gap 后未 Resync 的拒绝路径（对应架构源反例 Fixture `fixtures/invalid/replication-gap-without-resync.json`）。
- **性能指标**：吞吐（消息/秒、字节/秒）、Ingress 入队 p99、Egress 发送 p99、1/10/25/50/100/150/200 Bot Workload 下的队列深度曲线。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-005-replication-prediction.md`（Envelope 分离 ACK、拒绝先于分配）、`docs/adr/ADR-009-local-transport.md`（LocalEmbedded 保真、三类错误、Fault Decorator）。
- 架构源 `schemas/replication-envelope.schema.json`：正例 `fixtures/valid/replication-full-snapshot.json`、`fixtures/valid/replication-delta.json`；反例 `fixtures/invalid/replication-gap-without-resync.json`。

## 尚未批准的决策门

- **D-004**（Transport/Codec/压缩 OSS 栈）：临时默认值为不冻结任何供应商，成熟 OSS 置于 Adapter 后评估；Adapter 内选型不改基线，Envelope/Codec 变更才改基线。
- **SRV-D-001**（Ingress 容量与满载动作）、**SRV-D-002**（Egress 容量与可靠积压阈值）、**SRV-D-006**（限流与背压参数）：临时默认值与批准条件见 [modules/README.md](../README.md) §11.2。
