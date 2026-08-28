---
status: pending
---

# 实现载体无关的 transport 核心：连接注册表、连接代次、分配前校验闸、四条有界队列与故障装饰器

transport 是进程与外界字节流之间的唯一边界。本卡交付**与字节载体无关**的那一半：连接注册表（全仓唯一写入者）、`ConnectionEpoch`、Envelope 校验闸、四条有界队列与背压、限流、可注入故障装饰器，以及 `IByteCarrier` SPI 的消费侧。WSS 具体载体由下游卡实现，本卡用 `TestKit` 的内存 carrier 单测，因此不依赖任何网络。

设计出处：`docs/specs/2026-08-28-mvp-csharp-host-design.md` §4.3（断言机制纪律）/ §6.1 / §7.4（禁用面与分析器接线归属）。

## 涉及范围

- `mvp-host/src/Lumio.Server.MvpHost.Transport/**`（含 `mvp-host/src/Lumio.Server.MvpHost.Transport/queues.json`）
- `mvp-host/tests/Lumio.Server.MvpHost.Transport.Tests/**`

## 验收标准

- [ ] **先失败证据**：先提交 `Lumio.Server.MvpHost.Transport.Tests` 的全部测试（此时 `Transport` 只有空骨架），执行 `cd mvp-host && dotnet test tests/Lumio.Server.MvpHost.Transport.Tests/Lumio.Server.MvpHost.Transport.Tests.csproj -c Release`，记录 `Failed!` 汇总行；实现后重跑记录 `Passed!  - Failed: 0`。两次输出写进交回物。
- [ ] `Lumio.Server.MvpHost.Transport` 声明 `<MvpHostLayer>4</MvpHostLayer>`，`ProjectReference` 恰为 `Lumio.Server.MvpHost.HostContracts` 与 `Lumio.Server.MvpHost.Observability` 两个。**不引用 `Auth` / `Session` / `WorldSlot`**（由上游架构测试 `ForbiddenEdgesTest` 守住）。
- [ ] 连接状态机逐字实现 `Accepted → EnvelopeValidated → Bound → Active → Draining → Closed`，任一状态因可致命错误 `→ Closed(fault)`；触发条件对应：`Accepted` = carrier 接受且通道认证结果已交付；`EnvelopeValidated` = 首帧过结构校验；`Bound` = `ConnectionCommand.Bind` 被应用；`Active` = 首个 ingress 入队；`Draining` = `SetDrain(true)`；`Closed` = `Close` 命令 / carrier 关闭 / 空闲截止 / 致命错误。测试 `ConnectionStateMachineTransitionsTest` 逐条驱动并断言非法迁移被拒绝。
- [ ] `ConnectionEpochBumpTest`：每次 `Bind` / `Unbind` 被应用后 `ConnectionEpoch` 递增；携带旧 epoch 的任何 `ConnectionCommand` 被拒绝且 ack 的 `StableErrorId == "StaleConnectionGeneration"`。
- [ ] `ConnectionRegistrySoleWriterTest`：断言连接注册表的写入 API 全部是 `internal`，`Transport` 程序集外无法写入；`Session` 只能通过 `ITransportControlPort.TrySend(in ConnectionCommand)` 影响注册表。
- [ ] **分配前拒绝**：`OversizeRejectedBeforeAllocationTest` 用一个声明长度超过 `TransportEndpointOptions.MaxMessageBytes` 的入站消息，断言实现在累计接收字节越限时立即中止读取并关闭连接，**且过程中没有分配过等于消息声明长度的缓冲**（用一个记账型 carrier 断言单次分配上限不超过配置的接收缓冲大小）。畸形与完整性失败同样在完整消息物化前被拒。
- [ ] 四条有界队列按设计 §6.1 表实现并在 `queues.json` 登记（每条七项字段齐全）：`MvpIngressQueue`（per-connection，所有者 transport，单一接收循环 → Slot Owner Thread，严格 FIFO SPSC，256 条 / 256 KiB，Unreliable 丢弃并计数、**Reliable 以 `QueueFull` 断开连接**，Gate 关闭后停收）；`MvpEgressQueue`（per-connection，Owner Thread → 发送循环，严格 FIFO SPSC，512 条 / 1 MiB，可靠积压先降速持续超阈断开，断开前 flush ≤ 1 秒）；`MvpConnectionCommandInbox`（session → 连接命令循环，FIFO per connection epoch，64 条 / ack 超时 5 秒，回 `QueueFull` ack，Closed 后只收 `Close` 并 ack）；`MvpTransportEventOutbox`（**所有者是 session**，transport → session，FIFO，256 条，`Closed` / `Faulted` 走保留槽终态永不丢弃、非终态满载则关闭该连接并写 diagnostic，保留槽必达）。
- [ ] `ReliableQueueFullDisconnectsTest`：填满某连接的 `MvpIngressQueue` 后再投一条 `Reliability == "Reliable"` 的消息，断言该连接被关闭且 `ConnectionCloseReason == Fault`、`StableErrorId == "QueueFull"`；换成 `Reliability == "Unreliable"` 时断言消息被丢弃且丢弃计数加一、连接仍存活。
- [ ] `TerminalEventReservedSlotTest`：把 `MvpTransportEventOutbox` 填到满，随后产生一个 `ConnectionEvent.Closed`，断言该终态事件仍被投递（保留槽），而一个非终态事件在同样条件下导致连接被关闭并写出一条 diagnostic。
- [ ] 故障装饰器在两处各挂一次——解码后 / ingress 入队前，egress 出队后 / 交 carrier 前；`ITransportFaultPolicy` **在组装期注入**（构造函数参数），生产 Profile 传 `PassThroughFaultPolicy`。测试 `FaultPolicyIsInvokedAtBothHooksTest` 用 `TestKit.ScriptedTransportFaultPolicy` 断言两个挂点各被调用一次，且 `TransportFaultContext.IsIngress` 分别为 `true` / `false`。
- [ ] `FaultPolicyNotHardcodedTest`（**断言机制按设计 §4.3 的纪律定死**——`System.Reflection` 看不到方法体与构造点，且 `PassThroughFaultPolicy` 本就住在本程序集内，因此不能写成「反射断言不存在 `new` 硬编码」）：改为两条可判断言。① **签名级反射断言**——`TransportService.Create` 的 `ITransportFaultPolicy` 参数存在且**无默认值**，且 `TransportService` 内不存在类型为任何具体故障策略类的字段或属性（只有接口类型 `ITransportFaultPolicy` 的字段）。② **ArchUnitNET 方法调用依赖断言**——`Transport` 程序集内，除 `PassThroughFaultPolicy` 自身的类型定义外，**不存在对 `PassThroughFaultPolicy` 构造函数的调用依赖**（唯一构造点在组装根 `App`，不在本程序集内）。
- [ ] 错误分类按设计 §6.1 表实现：可拒绝一律**只断该连接**，不上升为 Slot 或进程故障（测试 `ConnectionFaultDoesNotEscalateTest` 断言畸形帧 / 超限 / 限流 / 旧 epoch 四种输入都只产生 `ConnectionEvent.Closed`，不产生任何 Slot 或进程级事件）；可致命（监听绑定失败、carrier 资源耗尽）产出一个进程级致命事件。
- [ ] 限流：按 provisional 64 msg/s、突发 128 实现每连接入站限流，超限按可拒绝处理；每 tick 每连接 egress 批量上限 8 条。两个数值以标注 `provisional` 的配置常量出现，**不写成公共常量或性能承诺**。
- [ ] 空闲超时经 `ITimerService` 投递 `ConnectionCommand.Close`（provisional 15 秒），**不自建轮询线程**；测试 `IdleTimeoutUsesTimerServiceTest` 用 `TestKit.FakeMonotonicClock` 推进虚拟时间断言 `Close` 命令被投递，且 `Transport` 程序集内不出现 `Thread.Sleep` / `Task.Delay` / `DateTime`（由禁用面分析器保证，构建即失败）。
- [ ] `PermissionGrantRefIsOpaqueTest`（**断言机制按设计 §4.3 的纪律定死**，原「反射断言不存在解释性使用」不可落地）：① **签名级反射断言**——`Transport` 程序集内 `PermissionGrantRef` 只作为字段类型、参数类型或返回类型出现，不存在任何以其 `Value` 为入参的判定方法（无 `bool XxxFrom(ulong grantValue)` 形态的成员）；② **ArchUnitNET 方法调用依赖断言**——`Transport` 对 `PermissionGrantRef.Value` 属性 getter 的调用依赖数**为 0**（相等比较用 `record struct` 自带的 `Equals`，不读取内部数值）；③ `Transport` 不引用 `Lumio.Server.MvpHost.Auth`（程序集引用级断言）。若 ① 与 ② 在 ArchUnitNET 上被证明不可表达，**降级为「签名级 ① + 评审项」并在交回物中写明这是降级项**，不得改用 IL 字节扫描，也不得引入任何未冻结的分析包（设计 §14 J4）。
- [ ] `BannedApiIsEnforcedAcrossProductionProjectsTest` 的**手工探针证据**（全仓级 RS0030 拦截，从 `implement-mvp-host-platform-primitives` 下沉至本卡）：在 `Lumio.Server.MvpHost.Transport`（`Platform` 之外的第一个生产工程）内临时写一行 `System.DateTime.UtcNow`，执行 `cd mvp-host && dotnet build build.proj -c Release`，断言构建**失败**并报 `RS0030`；删除探针后重跑，断言构建恢复成功。两段输出（失败时的 `RS0030` 行与恢复后的 `Build succeeded`）写进交回物，探针文件**不得进入提交**。理由：分析器接线落在 `scaffold-mvp-host-build-baseline` 的 `mvp-host/Directory.Build.props`（该文件不在本卡涉及范围），而 `implement-mvp-host-platform-primitives` 落地时 `Platform` 之外只有同 wave 并行的契约镜像工程，往里塞探针即破坏 wave 2 的文件集互斥（设计 §7.4）。
- [ ] `cd /Users/cui/LumioGames/LumioServer/mvp-host && bash eng/verify-all.sh` 退出码 0，末行 `MVP_HOST_VERIFY_OK`。
- [ ] `cd mvp-host && dotnet test tests/Lumio.Server.MvpHost.Transport.Tests/Lumio.Server.MvpHost.Transport.Tests.csproj -c Release --no-build` 输出 `Passed!  - Failed: 0`。
- [ ] 仓级收口门槛：`node .spec/tools/spec-lint.mjs` → `spec-lint: OK` 退出码 0；`node --test .spec/tools/spec-lint.test.mjs` → `fail 0` 退出码 0。
- [ ] `git status --porcelain` 只列出 `mvp-host/src/Lumio.Server.MvpHost.Transport/**` 与 `mvp-host/tests/Lumio.Server.MvpHost.Transport.Tests/**`（与同 wave 的 auth、world-slot 两卡文件集交集为空）。
- [ ] 未越界实现任何 `mvp-host/absences.json` 列出的条目；未向该文件追加条目。特别地：未实现分片重组（`ABS-WIRE-FRAGMENTATION`，`maxFragmentBytes` 只作声明值登记）。

## 依赖

`define-mvp-host-contracts-and-audit-surface`

## 接口

Consumes:

- 来自 `define-mvp-host-contracts-and-audit-surface`：`TransportConnectionId`、`ConnectionEpoch`、`PermissionGrantRef`、`ValidatedEnvelopeBytes`、`OutboundEnvelopeBytes`、`ServerSessionId`、`ConnectionCommand`（5 个派生）、`ConnectionEvent`（6 个派生）、`ConnectionCloseReason`、`ITransportControlPort`、`ITransportEventPort`、`IIngressReader`、`IEgressWriter`、`IByteCarrier`、`ITransportFaultPolicy`、`TransportFaultAction`、`TransportFaultContext`、`ITransportService`、`TransportEndpointOptions`、`BindEndpointResult`、`CarrierAccept`、`CarrierReceive`、`AckResult`；`ObservabilityModule.Create`、`ObservabilityServices`、`IDiagnosticWriter`、`DiagnosticRecord`。
- 来自 `implement-mvp-host-platform-primitives`（经 HostContracts 传递）：`IMonotonicClock`、`ITimerService`、`IBoundedInbox<T>`、`IBoundedOutbox<T>`、`QueueBudget`、`EnqueueResult`、`PlatformModule.CreateInbox<T>`。
- 来自 `implement-mvp-envelope-wire-and-fixture-gate`（经 HostContracts 传递）：`MvpEnvelopeReader.TryReadHeader` / `.Validate`、`EnvelopeHeaderView`、`EnvelopeParseStatus`、`EnvelopeParseResult`。
- 来自 `define-mvp-host-contracts-and-audit-surface` 的 `TestKit`：`InMemoryByteCarrier`、`FakeMonotonicClock`、`ScriptedTransportFaultPolicy`。

Produces（命名空间 `Lumio.Server.MvpHost.Transport`）:

- `public sealed class TransportService : ITransportService, System.IDisposable { public static TransportService Create(IByteCarrier carrier, in TransportEndpointOptions options, IMonotonicClock clock, ITimerService timers, ITransportFaultPolicy faultPolicy, IBoundedOutbox<ConnectionEvent> eventOutbox, IDiagnosticWriter diagnostics); public BindEndpointResult BindEndpoint(in TransportEndpointOptions options); public ITransportControlPort Control { get; } public IIngressReader Ingress { get; } public IEgressWriter Egress { get; } }`
- `public sealed class PassThroughFaultPolicy : ITransportFaultPolicy { public TransportFaultAction Decide(in TransportFaultContext ctx); }`
- `public static class TransportProvisionalDefaults { public const int IngressMaxItems = 256; public const long IngressMaxBytes = 262144; public const int EgressMaxItems = 512; public const long EgressMaxBytes = 1048576; public const int CommandInboxMaxItems = 64; public const int CommandAckTimeoutSeconds = 5; public const int EventOutboxMaxItems = 256; public const int InboundMessagesPerSecond = 64; public const int InboundBurst = 128; public const int EgressBatchPerTickPerConnection = 8; public const int IdleTimeoutSeconds = 15; public const int DefaultMaxMessageBytes = 65536; }`（全部标注 `provisional`，对应 SRV-D-001 / D-002 / D-015 / D-006）
- 供 `implement-mvp-websocket-carrier-adapter` 实现的 SPI 契约：`IByteCarrier` 的实现方必须保证「一次 `ReceiveAsync` 循环组装出的一段字节 = 一个完整 Envelope」，并在累计字节超过 `TransportEndpointOptions.MaxMessageBytes` 时返回 `CarrierReceive { Received = false, Closed = true }` 而不是继续分配。
