---
status: pending
---

# 实现 auth 存根：injected exact-byte verifier、防重放窗口、不可变 PermissionGrant 与 gate 执行体

auth 拥有「这个连接是谁、允许它做什么」的全部裁决。MVP 的 token 存根只落在 WSS 通道认证层，**绝不进入任何 Envelope 字段**；凭据 wire 格式因 D-011 冻结而不得发明。本卡只实现行为契约与 injected verifier adapter——这是 Rust 侧设计已给出的合法落点（`modules/auth/src/adapters/injected.rs`，原文「仅测试/集成用 exact-byte verifier，不成为 wire 标准」）。

设计出处：`docs/specs/2026-08-28-mvp-csharp-host-design.md` §4.3（断言机制纪律）/ §5.6-C（子协议承载与 body 扩展的原则性区别）/ §5.8 / §6.2 / §6.6。

## 涉及范围

- `mvp-host/src/Lumio.Server.MvpHost.Auth/**`（含 `mvp-host/src/Lumio.Server.MvpHost.Auth/queues.json`）
- `mvp-host/tests/Lumio.Server.MvpHost.Auth.Tests/**`

## 验收标准

- [ ] **先失败证据**：先提交 `Lumio.Server.MvpHost.Auth.Tests` 的全部测试（此时 `Auth` 只有空骨架），执行 `cd mvp-host && dotnet test tests/Lumio.Server.MvpHost.Auth.Tests/Lumio.Server.MvpHost.Auth.Tests.csproj -c Release`，记录 `Failed!` 汇总行；实现后重跑记录 `Passed!  - Failed: 0`。两次输出写进交回物。
- [ ] `Lumio.Server.MvpHost.Auth` 声明 `<MvpHostLayer>4</MvpHostLayer>`，`ProjectReference` 恰为 `Lumio.Server.MvpHost.HostContracts` 与 `Lumio.Server.MvpHost.Observability` 两个。**不引用 `Transport` / `Session` / `WorldSlot`**。
- [ ] `InjectedExactByteCredentialVerifier` 实现 `ICredentialVerifier`：启动期从 `--shared-secret-file` 指向的文件载入比对材料，`Verify` 做**常量时间** exact-byte 比对（用 `System.Security.Cryptography.CryptographicOperations.FixedTimeEquals`）。测试 `VerifierIsConstantTimeCompareTest`（**断言机制按设计 §4.3 的纪律定死**——`System.Reflection` 看不到方法体，原「反射断言实现体调用了 …」不可落地）分三条：① **ArchUnitNET 方法调用依赖断言**——`InjectedExactByteCredentialVerifier` 对 `System.Security.Cryptography.CryptographicOperations.FixedTimeEquals` **存在**方法调用依赖；② **ArchUnitNET 方法调用依赖断言**——同一类型对 `System.Linq.Enumerable.SequenceEqual` 与 `System.MemoryExtensions.SequenceEqual` 的调用依赖数**为 0**；③ 「不存在短路的 `==` 逐字节比较」在 IL 层不可判，**降级为评审项 + 一条时序无关性单测**（等长不同前缀与等长不同尾缀两组输入，断言判定结果均为 `CredentialVerdict.Rejected` 且走同一条代码路径，不因首字节不同而提前返回）。**③ 是降级项，须在交回物中明确写出**；不得改用 IL 字节扫描，也不得引入任何未冻结的分析包（设计 §14 J4）。
- [ ] `VerifierMaterialMissingIsFatalTest`：比对材料文件缺失或不可读时，构造 verifier 抛出并携带明确原因，**不返回一个恒 Accept 的降级实现**；`NoAuthBypassSwitchTest` 断言 `Auth` 程序集内不存在任何名为 `SkipAuth` / `DisableAuth` / `AllowAnonymous` 的开关、常量或配置字段。
- [ ] `OpaqueCredentialInput` 是 `sealed class` + `IDisposable`，**重写 `ToString()` 返回固定字面量 `"OpaqueCredentialInput"`**，不实现 `Equals` / `GetHashCode` 的值语义，且不带任何序列化特性。测试 `CredentialNeverSerializableTest` 断言该类型不可被 `System.Text.Json` 序列化出内容（序列化结果不含载荷字节），`ToString()` 不泄漏任何输入字节。
- [ ] 防重放窗口 `IAntiReplayWindow` 实现：窗口 provisional 30 秒 + 单调 nonce，键为 `(PrincipalId, nonce)`。测试 `ReplayedNonceRejectedTest` 断言同一 `(principal, nonce)` 第二次返回 `AntiReplayVerdict.Replayed`；`OutOfWindowRejectedTest` 用 `FakeMonotonicClock` 推进超过 30 秒后断言返回 `OutOfWindow`；`InvalidCredentialDoesNotConsumeWindowTest` 断言凭据无效的请求**不消耗**防重放窗口配额（同一 nonce 随后仍可被一次合法请求使用）。
- [ ] `ReplayStorm` 信号：连续命中达到阈值时产出类型化信号，并按 provisional SRV-D-006 把该来源配额减半。测试 `ReplayStormHalvesQuotaTest` 断言信号被产出且后续配额为原值的一半。
- [ ] `PermissionGrant` 是不可变 `sealed record`，字段为 `PrincipalId Principal, string Role, ImmutableArray<string> Claims, ImmutableArray<string> AllowedMessageTypes, GrantEpoch Epoch, MonotonicInstant ExpiresAt`；测试 `GrantIsImmutableTest` 断言全部属性只读、集合是 `ImmutableArray`，且不存在任何在派生后修改 grant 的公开 API。
- [ ] `RegrantOnReconnectBumpsEpochTest`：对同一 principal 连续两次 `Authorize`，断言第二个 `PermissionGrant.Epoch` 严格大于第一个（重连必须重新派生授权对象，SRV-D-013）。
- [ ] `AuthHasNoOwnThreadTest`：断言 `Auth` 程序集内不存在 `INamedThreadSupervisor.Start` 的调用，也不存在任何 `Thread` / `Task.Run` 的使用——认证在调用方（session 编排路径）上同步执行。
- [ ] gate 执行体：调用 `MvpProtocolPermissionGate.Evaluate` 并把 `MvpPermissionGateResult` 映射为 `AckResult`；`GateExecutionAddsNoCriterionTest`（**断言机制按设计 §4.3 的纪律定死**，原「反射断言执行路径上只有一次 `Evaluate` 调用且结果未被二次覆盖」不可落地）分三条：① **ArchUnitNET 方法调用依赖断言**——`MvpAuthorizationService.EvaluateMessagePermission` 对 `MvpProtocolPermissionGate.Evaluate` 存在方法调用依赖，且 `Auth` 程序集内对该方法的调用依赖**只来自这一个方法**；② **签名级反射断言**——`Auth` 程序集内不存在任何「接受 `MvpPermissionGateResult` 并返回另一个 `MvpPermissionGateResult`」的方法（即不存在可用于二次覆盖判定结果的成员）；③ 「结果未被二次覆盖」降级为**评审项 + 一条定向用例**：对一个 gate 判 `Accept` 的请求，`EvaluateMessagePermission` 必返回 `AckResult { Accepted = true, StableErrorId = null }`。**③ 是降级项，须在交回物中明确写出。**
- [ ] `RoleClaimsAreAdmissionContextTest`（**断言机制按设计 §4.3 的纪律定死**，原「扫描本工程对 `MvpEnvelopeWriter` 的全部调用点」不可落地）：① **ArchUnitNET 方法调用依赖断言**——`Auth` 程序集对 `Lumio.Server.MvpHost.Wire.MvpEnvelopeWriter` 的**任何**方法调用依赖数为 **0**（`Auth` 根本不构造出站信封，因此 `Role` / `Claims` 不可能被写进任何 Envelope 字段）；② **签名级反射断言**——`Auth` 的公开与内部 API 的返回类型中不出现 `System.ReadOnlyMemory<byte>` 形态的信封字节。`Role` 与 `Claims` 是准入上下文，ADR-022 明确否决把它们作为每条消息的 wire 字段。
- [ ] 错误语义：**可重试类为空**——测试 `AuthNeverRetriesTest` 断言 `AuthenticateOutcome` 不含任何表示「稍后重试」的取值，且 `Auth` 不实现任何重试循环。可拒绝路径产出的 `StableErrorId` 只能取 `SessionAntiReplay` / `ReleaseMismatch` / `RoleMismatch` / `ClaimNotGranted` / `MessagePermissionDenied` / `StaleConnectionGeneration` / `SessionMismatch` 之一（测试 `AuthErrorIdsAreRegisteredTest` 逐条比对 `Lumio.Gen.ContractTypes.Catalog.StableErrorIds`）。
- [ ] **凭据无效不发 Envelope Error**：`CredentialFailureHasNoEnvelopeTest` 断言凭据比对失败时 `Auth` 返回的 `AuthenticateOutcome.StableErrorId` 为 `null`、不构造任何出站 Envelope，只产出一条 Audit 记录（43 个已注册 ErrorCode 中无「凭据无效」语义码，见 `absences.json` 的 `ABS-AUTH-CREDENTIAL-ERRORCODE`）。
- [ ] 审计形状：拒绝事件经 `IAuditWriter.WriteReleaseScopedReject` 写出，字段为 `category="Audit"`、`severity="Warn"`、`correlation.scope="Release"`、带 `releasePoolId`、**不含 `sessionId`**、`durability="Durable"`、`redaction="Applied"`；测试 `AuthRejectAuditMatchesFixtureTest` 以镜像 `fixtures/valid/logging-auth-reject-audit.json` 为金标准逐字段比对，字段集比对**另含 `eventId` 与 `timestamp` 两项**（两者由 `Observability` 内部填充，`Auth` 不传：`eventId` 按 `event-{producerId}-{eventSeq}` 生成，`timestamp` 来自 `Platform` 的 `IWallClock`；`logging-event.schema.json` 实测 `required` 恰 7 项且 `additionalProperties: false`，缺任一项即产不出合法事件）。同一测试用 `TestKit.RecordingHostTraceSink` 断言：每次 `IAuditWriter.WriteReleaseScopedReject` 成功写入后，`IHostTraceSink` 上**镜像出恰一条** `kind:"audit"` 记录（`Auth` 侧无需显式调用 `Trace.Audit`，镜像由 `IAuditWriter` 的实现完成，设计 §6.6 / §9.3）。
- [ ] `NoCredentialInLogsTest`：把一条已知字节串作为凭据走完一次失败认证，断言产出的全部 Audit 与 Diagnostic 记录的序列化文本中**不含**该字节串的任何编码形式（原文 / base64 / hex）。
- [ ] Audit 背压不得静默放行：`AuditBackpressureStopsAdmissionTest` 断言 `MvpAuditQueue` 达阈时认证路径产出「请求停止接纳新连接」的类型化结果，而不是继续返回 Accept。
- [ ] 两条队列在 `queues.json` 登记（七项字段齐全）：`MvpAuthRequestQueue`（所有者 auth，生产者 session，FIFO per connection epoch，容量 32，满载返回 `AuthBusy`（**映射 `QueueFull`**）由 session 决定重试或关闭，关闭后拒绝新请求）——`queues.json` 的 `onFull` 字段值逐字写 `AuthBusy（映射 QueueFull）`，与 `implement-mvp-world-slot-aggregate-and-sim-port-stub` 给 `AggregateBusy` 的写法同口径；`MvpAuthEventQueue`（**所有者 session**，生产者 auth runner，FIFO per request id，容量 64，满载写 diagnostic emergency 且**不得丢成功 ack**，关闭后只交付已入队项）。
- [ ] `cd /Users/cui/LumioGames/LumioServer/mvp-host && bash eng/verify-all.sh` 退出码 0，末行 `MVP_HOST_VERIFY_OK`。
- [ ] `cd mvp-host && dotnet test tests/Lumio.Server.MvpHost.Auth.Tests/Lumio.Server.MvpHost.Auth.Tests.csproj -c Release --no-build` 输出 `Passed!  - Failed: 0`。
- [ ] 仓级收口门槛：`node .spec/tools/spec-lint.mjs` → `spec-lint: OK` 退出码 0；`node --test .spec/tools/spec-lint.test.mjs` → `fail 0` 退出码 0。
- [ ] `git status --porcelain` 只列出 `mvp-host/src/Lumio.Server.MvpHost.Auth/**` 与 `mvp-host/tests/Lumio.Server.MvpHost.Auth.Tests/**`（与同 wave 的 transport、world-slot 两卡文件集交集为空）。
- [ ] 未越界实现任何 `mvp-host/absences.json` 列出的条目；未向该文件追加条目。特别地：未定义任何凭据 / 票据 / nonce / rotation / 签名算法 / 密钥派生的**线格式**，未向 `Handshake` body 增加 `role` 之外的任何字段，未实现 Session Resume Token。

## 依赖

`define-mvp-host-contracts-and-audit-surface`

## 接口

Consumes:

- 来自 `define-mvp-host-contracts-and-audit-surface`：`AuthRequestId`、`PrincipalId`、`GrantEpoch`、`ServerSessionId`、`TransportConnectionId`、`ConnectionEpoch`、`OpaqueCredentialInput`、`VerificationContext`、`CredentialVerdict`、`CredentialVerification`、`ICredentialVerifier`、`AntiReplayVerdict`、`IAntiReplayWindow`、`PermissionGrant`、`IAuthorizationService`、`AuthenticateCommand`、`AuthenticateOutcome`、`SessionScope`、`AckResult`；`ObservabilityServices`（含 `Trace` 属性）、`IAuditWriter.WriteReleaseScopedReject`、`IDiagnosticWriter.Write`、`AuditRecord`（含 `EventId` / `Timestamp`）、`DiagnosticRecord`（含 `EventId` / `Timestamp`）、`IHostTraceSink`、`NullHostTraceSink`。
- 来自 `implement-mvp-envelope-wire-and-fixture-gate`（经 HostContracts 传递）：`MvpPermissionGateRequest`、`MvpPermissionGateResult`、`MvpPermissionVerdict`、`MvpProtocolPermissionGate.Evaluate` / `.ActiveFieldNames`。
- 来自 `implement-mvp-host-platform-primitives`（经 HostContracts 传递）：`IMonotonicClock`、`MonotonicInstant`、`IBoundedInbox<T>`、`QueueBudget`、`EnqueueResult`、`PlatformModule.CreateInbox<T>`。
- 来自 `define-mvp-host-contracts-and-audit-surface` 的 `TestKit`：`FakeMonotonicClock`、`FakeWallClock`、`ContractMirrorFixtures.Load`、`RecordingHostTraceSink`。

Produces（命名空间 `Lumio.Server.MvpHost.Auth`）:

- `public sealed class InjectedExactByteCredentialVerifier : ICredentialVerifier { public static InjectedExactByteCredentialVerifier FromSecretFile(string path); public CredentialVerification Verify(OpaqueCredentialInput credential, in VerificationContext context); }`
- `public sealed class MvpAntiReplayWindow : IAntiReplayWindow { public static MvpAntiReplayWindow Create(IMonotonicClock clock, int windowSeconds, int stormThreshold); public AntiReplayVerdict Check(PrincipalId principal, string nonce, MonotonicInstant receivedAt); public bool TryDrainReplayStorm(out PrincipalId offender); }`
- `public sealed class MvpAuthorizationService : IAuthorizationService { public static MvpAuthorizationService Create(ICredentialVerifier verifier, IAntiReplayWindow antiReplay, ObservabilityServices observability, string releasePoolId); public AuthenticateOutcome Authenticate(in AuthenticateCommand command); public PermissionGrant Authorize(PrincipalId principal, in SessionScope scope); public AckResult EvaluateMessagePermission(in MvpPermissionGateRequest request); public bool AdmissionMustStop { get; } }` —— 这四个成员就是 `HostContracts` 的 `IAuthorizationService` 的全部成员；`Session` 只经该接口调用本实现，两个程序集互不引用，实例由 `assemble-mvp-host-app-and-smoke-client` 在组装期注入。
- `public enum AuthQueueAdmission { Accepted, AuthBusy, Closed }` —— `MvpAuthRequestQueue` 的入队结果。`AuthBusy` 是**模块内部状态、不是 `StableErrorId`**：`ids/index.json` 的 43 个 ErrorCode 中无此值（实测全表已核对），`AggregateBusy` 同样不在册；一旦需要对外表达（进 `AckResult.StableErrorId` 或 `Error.body.reasonCode`），一律映射为已注册的 `QueueFull`。写法照抄 `implement-mvp-world-slot-aggregate-and-sim-port-stub` 给 `AggregateBusy` 的口径；`define-mvp-host-contracts-and-audit-surface` 的全构建图断言 `AllStableErrorIdsAreRegisteredTest` 会断言这两个标识符不出现在任何 `StableErrorId` 位置。
- `public static class AuthProvisionalDefaults { public const int AntiReplayWindowSeconds = 30; public const int ReplayStormThreshold = 8; public const int AuthRequestQueueMaxItems = 32; public const int AuthEventQueueMaxItems = 64; public const int GrantLifetimeSeconds = 300; }`（全部标注 `provisional`，对应 SRV-D-005 / D-006 / D-013）
- 通道认证的输入契约（供 `implement-mvp-websocket-carrier-adapter` 与 `assemble-mvp-host-app-and-smoke-client` 消费）：WSS Upgrade 的 `Sec-WebSocket-Protocol` 三段 `lumio.mvp.v0, <opaqueTokenB64Url>, <opaqueNonceB64Url>`；第 2 段解 base64url 后构造 `OpaqueCredentialInput`，第 3 段原样作为 `VerificationContext.Nonce`；两段在 WebSocket 建立完成前被消费并丢弃，**永不进入任何 Envelope 字段**。该承载登记为 `mvp-host/absences.json` 的 `ABS-AUTH-CREDENTIAL-CARRIAGE`（设计 §5.6-C、§6.2、§11 G18、§12.1 B10），适用与 `length`（设计 §5.7）**同一条退场纪律**：架构源一旦冻结通道认证的凭据承载方式，本仓即改用公共形态并删除子协议位序约定；名字里的 `mvp` 与 `v0` 是这条纪律的可见退场标记，**不得去掉**。本卡**不定义**凭据 blob 的内部格式、算法、轮换或 nonce 派生，只实现 D-011 已冻结的行为契约「准入前必须先过防重放」。
