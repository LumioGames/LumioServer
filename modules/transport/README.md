# transport 模块

> Reactor、Envelope 结构校验、可靠性/分片/Ack、限流背压、Ingress/Egress 有界队列、连接注册表唯一写入者与传输 Adapter。

## 模块定位与目标

`transport` 是进程与外界字节流之间的唯一边界。它把不可信输入在分配前拒绝、把可信消息放进有界队列、把 Tick 产物送回网络——并且**从不**跨过队列直接触碰 Gameplay。连接注册表的唯一写入者是本模块：[session](../session/README.md) 经 `BindConnection/UnbindConnection` 类型化命令请求变更，本模块在 Reactor 上下文串行应用并以连接 epoch 拒绝迟到命令——不存在跨模块共享可变表。所有传输实现（Socket/TLS/InMemory）都在 Adapter 之后，供应商选择是决策门 D-004，不属于本模块的稳定契约。

## 负责什么

- Reactor：监听 Endpoint 绑定、连接接受、读写事件驱动（实现框架属 D-004，行为契约在此冻结）；连接与 Reactor 分片的亲和关系固定（SRV-D-011）——一条连接终身归属一个 Reactor 分片，保证 per-session Ingress 是严格 SPSC。
- Envelope 结构校验：按架构源 `schemas/replication-envelope.schema.json` 校验 `protocolVersion`、`length`、`sequence`、`sessionId`、`productId`、`gameReleaseId`、`messageType`、`reliability`、`integrity`、`traceId`；最大长度与畸形输入在**分配前**拒绝（架构源 §7.3、ADR-005）。
- 可靠/不可靠通道：Transport ACK、重传、分片与重组；Transport ACK 与 Replication Baseline ACK 严格分离（Baseline ACK 语义归 Runtime）。
- 限流与背压：每连接速率限制（SRV-D-006）、可靠积压阈值降级/断开（SRV-D-002）；消费 [auth](../auth/README.md) 发出的 `ReplayStorm` 类型化信号收紧对应连接的限流（组装期接线，无 transport -> auth 编译依赖）。
- 连接注册表所有权：传输句柄、限流计数、授权对象引用、连接 epoch——唯一写入者是本模块；session 的 Bind/Unbind 命令与断开请求经有界命令队列进入，Reactor 上下文串行应用并回 ack。
- 有界队列所有权：per-session Ingress（SRV-D-001）与 Egress（SRV-D-002）队列的容量、优先级、满载动作与 Metrics。
- 连接级权限过滤的**执行点**：解码后、入队前，按注册表中绑定的不可变授权对象过滤 `messageType`；权限语义归 [auth](../auth/README.md)，绑定动作由 session 命令驱动（见 [modules/README.md](../README.md) §3.2 分工）。
- 广播执行：`broadcast(poolScope, envelope)` 类型化命令（来自 [maintenance-agent](../maintenance-agent/README.md) 等编排方）的机械执行与结果回报。
- 传输 Adapter 接口：暴露消息批与三类稳定错误（可重试、可拒绝、可致命）；InMemory Adapter 服务 LocalEmbedded，且不绕过 Envelope/Codec/大小限制/队列（架构源 ADR-009）。
- 网络故障注入的执行：按 [host-profiles](../host-profiles/README.md) 声明的 Fault Decorator（延迟/抖动/丢包/乱序/重复/断线/重连/QueueFull）作用于 Adapter 层。

## 明确不负责什么

- 不做认证决策、票据校验或防重放判定（归 [auth](../auth/README.md)）。
- 不做 Admission、Session 生命周期或路由（归 [session](../session/README.md)、[release-agent](../release-agent/README.md)）。
- 不调用任何 Gameplay/Runtime 入口；Reactor 线程的唯一出口是有界队列。
- 不定义 Envelope Schema、`messageType` 枚举或 `resyncReason`（归架构源）；不把第三方网络类型写入稳定契约。
- 不拥有 Replication 语义（FullSnapshot/Delta/Resync 的内容与状态归 Runtime；本模块只搬运）。
- 不做消息级分发路由（未来归 [protocol-dispatch](../protocol-dispatch/README.md)，公共契约冻结前封锁，D-009）。

## 拥有的状态与资源

- 监听 Endpoint、连接注册表（传输句柄、限流计数、授权对象引用、连接 epoch、Reactor 分片亲和）。
- 连接命令有界队列（Bind/Unbind/Disconnect/Broadcast，SRV-D-015 约定）。
- per-session Ingress/Egress 有界队列及其满载策略状态。
- 可靠通道的重传缓冲、分片重组缓冲（均有大小上限）。
- 传输 Adapter 实例与 Fault Decorator 状态（含确定性 Seed）。

## 输入、输出与稳定接口

- **输入**：外部字节流（不可信）、Egress 队列中的出站消息、session 的连接命令、编排方的广播命令、auth 的 `ReplayStorm` 信号。
- **输出**：Ingress 队列中已过校验的消息批（供 Simulation Owner Thread 消费）、出站字节流、`HandshakeReady(connId)` 与 `ConnectionClosed(connId, epoch)` 事件（送 session）、连接级稳定错误、命令 ack。
- **稳定接口**：`bind_endpoint(endpoint)`；`enqueue_egress(sessionId, envelope)`；`drain_ingress(slotId, budget) -> Batch`；`submit(command: BindConnection | UnbindConnection | Disconnect | Broadcast) -> Ack`（连接命令队列入口）。

## 上游与下游依赖

- **上游**：[session](../session/README.md)（连接命令）、[maintenance-agent](../maintenance-agent/README.md)（广播命令）、[world-slot](../world-slot/README.md)（Owner Thread 拉取 Ingress）。Endpoint 配置由 [process](../process/README.md) 组装期接线（来源为 [release-agent](../release-agent/README.md) 校验过的 Catalog/Manifest 数据与配置快照），运行期无命令边。
- **下游**：[host-profiles](../host-profiles/README.md)（传输/故障 Profile 查询）、[host-runtime](../host-runtime/README.md)（线程监督、重传定时）、[observability](../observability/README.md)（事件与 Metrics）。

## 生命周期与状态机

- 连接（传输层，本仓细化设计）：`Accepted -> EnvelopeValidated -> Bound（Bind 命令应用，授权对象与 epoch 就位）-> Active -> Draining -> Closed`；任一状态可因可致命错误进入 `Closed(fault)`。每次 Bind/Unbind 递增连接 epoch；旧 epoch 命令拒绝并回稳定错误。
- 模块整体随 [process](../process/README.md) 的 `Serving/Draining` 状态开关监听与新连接接受。

## 线程、队列与并发所有权

- 拥有 Reactor 线程与发送线程（数量为部署配置，经 [host-runtime](../host-runtime/README.md) 受监督创建）。
- Ingress 队列：生产者是该连接亲和的单一 Reactor 分片，消费者是 Simulation Owner Thread——严格 SPSC（SRV-D-011）。
- Egress 队列：生产者是 Simulation Owner Thread（`EgressPublish` 之后），消费者是发送线程。
- 连接注册表只在 Reactor 上下文写；读侧（权限过滤）在同一分片上下文，无锁。
- 队列容量、优先级、满载动作、Metrics 是每个队列的必备声明；禁止无界增长（架构源 §4.3）。

## 正常数据流与失败路径

- **正常入站**：字节流 → 长度/版本/完整性校验 → 解码 → 授权对象过滤 → per-session Ingress 入队 → Tick 消费。
- **正常出站**：`EgressPublish` → Egress 队列 → 可靠性处理（ACK/重传/分片）→ 发送。
- **失败路径**：
  - 超长/畸形/完整性失败：分配前拒绝，回稳定错误，计数；重复出现按限流策略断开。
  - Ingress 满：Unreliable 丢弃并计数；Reliable 按 SRV-D-001 断开连接（不静默丢可靠消息，架构源 ADR-009）。
  - Egress 可靠积压超阈值：先降速后断开（SRV-D-002）；断开完成前的出站排空语义（Egress flush）随 SRV-D-002 一并声明。
  - 限流超限 / `ReplayStorm` 信号：先延迟后断开（SRV-D-006）。
  - 迟到连接命令（旧 epoch）：稳定拒绝并回 ack，注册表不受影响。
  - 传输层故障（对端断开、IO 错误）：发 `ConnectionClosed` 事件；重连从 Handshake/FullSnapshot 开始，除非有效 Baseline 被显式保留（架构源 ADR-009）。

## 错误分类、恢复与降级

- **可重试**：瞬时 IO 错误、发送窗口暂满。
- **可拒绝**：畸形 Envelope、超大小上限、未通过权限过滤、限流超限、旧 epoch 命令——回稳定错误码。
- **可致命**：监听绑定失败、Reactor 资源耗尽——上报 [process](../process/README.md) 按进程级处置。
- **降级**：可靠积压降速、Unreliable 丢弃、Diagnostic 采样；全部降级动作可在 Metrics 观测。

## 配置、Capability 与安全约束

- Endpoint、Reactor 线程数、队列容量、限流参数来自不可变配置快照。
- 传输 Profile（是否走 Socket/TLS、是否 InMemory）由 [host-profiles](../host-profiles/README.md) 声明；LocalEmbedded 不得绕过 Schema/Codec/Envelope/权限/大小限制/队列。
- 认证、防重放、限流、背压和审计不能被本地快捷路径跳过（本仓 [repository-architecture.md](../../.spec/knowledge/standards/repository-architecture.md)）。

## 日志、Metrics、Trace 与 Audit

- Metrics：连接数、每队列深度/丢弃/满载次数、重传率、分片率、限流触发数、复制字节与重传字节（对应架构源 ADR-016 指标清单）、命令队列深度与迟到命令拒绝数。
- 断开与拒绝事件带稳定原因写 Diagnostic；维护踢人（`MaintenanceKick`）的广播结果写 Audit（由 maintenance-agent 编排关联 `maintenanceId`）。
- 网络时间戳与队列状态进入 Diagnostic Hash，不进权威 Simulation Hash。

## 测试面、故障矩阵与性能指标

- **测试面**：Wire Envelope 编解码、可靠性/分片/Ack、限流、背压、认证前过滤、`ReplayStorm` 联动、连接命令串行应用与旧 epoch 拒绝、SPSC 亲和不变量、网络故障注入（Fault Decorator 全谱系）、LocalEmbedded 与 LocalSplitProcess 同命令流对比。
- **故障矩阵**：丢包/乱序/重复/断线/重连、QueueFull、超长消息、gap 后未 Resync 的拒绝路径（对应架构源反例 Fixture `fixtures/invalid/replication-gap-without-resync.json`）。
- **性能指标**：吞吐（消息/秒、字节/秒）、Ingress 入队 p99、Egress 发送 p99、1/10/25/50/100/150/200 Bot Workload 下的队列深度曲线。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-005-replication-prediction.md`（Envelope 分离 ACK、拒绝先于分配）、`docs/adr/ADR-009-local-transport.md`（LocalEmbedded 保真、三类错误、Fault Decorator）。
- 架构源 `schemas/replication-envelope.schema.json`：正例 `fixtures/valid/replication-full-snapshot.json`、`fixtures/valid/replication-delta.json`；反例 `fixtures/invalid/replication-gap-without-resync.json`。

## 尚未批准的决策门

- **D-004**（Transport/Codec/压缩 OSS 栈）：临时默认值为不冻结任何供应商，成熟 OSS 置于 Adapter 后评估；Adapter 内选型不改基线，Envelope/Codec 变更才改基线。
- **SRV-D-001**（Ingress 容量与满载动作）、**SRV-D-002**（Egress 容量、可靠积压阈值与断开前排空语义）、**SRV-D-006**（限流与背压参数，含 `ReplayStorm` 收紧幅度）、**SRV-D-011**（连接-Reactor 分片亲和与再平衡禁令）：临时默认值与批准条件见 [modules/README.md](../README.md) §11.2。
