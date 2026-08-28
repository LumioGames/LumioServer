---
status: pending
---

# 实现 IByteCarrier 的 WebSocket 版本：监听、Upgrade 期子协议 token 终结、一消息一信封、Close 与空闲超时

WebSocket 只是又一个 Adapter：它替换字节载体，**不得绕过** Schema、Codec、Envelope、认证、权限过滤、大小限制、有界队列与 Tick 交付。本卡零第三方 NuGet，能力全部来自 `Microsoft.AspNetCore.App` 共享框架；`Transport` 核心保持零框架引用因而可无 Web 宿主单测。

设计出处：`docs/specs/2026-08-28-mvp-csharp-host-design.md` §5.2 / §6.1 / §6.2 的 token 落点 / §7.4 的 ws-wss 分档。

## 涉及范围

- `mvp-host/src/Lumio.Server.MvpHost.Transport.WebSocket/**`
- `mvp-host/tests/Lumio.Server.MvpHost.Transport.WebSocket.Tests/**`

## 验收标准

- [ ] **先失败证据**：先提交 `Lumio.Server.MvpHost.Transport.WebSocket.Tests` 的全部测试（此时载体只有空骨架），执行 `cd mvp-host && dotnet test tests/Lumio.Server.MvpHost.Transport.WebSocket.Tests/Lumio.Server.MvpHost.Transport.WebSocket.Tests.csproj -c Release`，记录 `Failed!` 汇总行；实现后重跑记录 `Passed!  - Failed: 0`。两次输出写进交回物。
- [ ] `Lumio.Server.MvpHost.Transport.WebSocket` 声明 `<MvpHostLayer>5</MvpHostLayer>`，`ProjectReference` 恰为 `Lumio.Server.MvpHost.Transport` 一个；使用 `Microsoft.NET.Sdk` 并带 `<FrameworkReference Include="Microsoft.AspNetCore.App" />`；**零 `PackageReference`**。测试 `NoThirdPartyPackageTest` 解析该工程 `obj/project.assets.json` 断言 `libraries` 为空数组、`frameworks` 只含 `net10.0`，把该断言的实际读数写进交回物。
- [ ] 该工程是**类库**而非 Web 应用（`OutputType` 不为 `Exe`）；`Transport` 核心工程不带任何 `FrameworkReference`（由 `Architecture.Tests` 的分层断言与本卡的 `TransportCoreStaysFrameworkFreeTest` 共同守住）。
- [ ] `WebSocketByteCarrier` 实现 `IByteCarrier` 的四个方法（`AcceptAsync` / `ReceiveAsync` / `TrySend` / `Close`），并遵守 `implement-mvp-transport-core-and-bounded-queues` 的 SPI 契约：一次接收循环组装出的一段字节 = 一个完整 Envelope（`WebSocketMessageType.Text`，`EndOfMessage` 为真时才交付）。
- [ ] **一 WS 消息 = 一 Envelope，不实现自定义分片重组**：测试 `OneWebSocketMessageIsOneEnvelopeTest` 断言把两个 Envelope 拼进一个 WS 消息时整条消息被拒并关闭连接；把一个 Envelope 拆成两个独立 WS 消息时同样被拒。`transportPolicy.maxFragmentBytes` 只作声明值，不参与任何重组逻辑。
- [ ] **超限在分配前拒绝**：`OversizeAbortedBeforeBufferGrowthTest` 发送一条累计字节超过 `TransportEndpointOptions.MaxMessageBytes` 的 WS 消息，断言实现在累计 `WebSocketReceiveResult.Count` 越限的那一刻中止读取并以 `CarrierReceive { Received = false, Closed = true }` 返回，且过程中单次分配不超过固定接收缓冲大小（用内存记账断言峰值分配上界）。
- [ ] Upgrade 期通道认证：从 `Sec-WebSocket-Protocol` 解析三段 `lumio.mvp.v0, <opaqueTokenB64Url>, <opaqueNonceB64Url>`，接受时**回选 `lumio.mvp.v0`** 完成 101 握手。测试 `SubprotocolNegotiationTest` 断言响应的 `Sec-WebSocket-Protocol` 恰为 `lumio.mvp.v0`。该子协议位序承载登记为 `mvp-host/absences.json` 的 `ABS-AUTH-CREDENTIAL-CARRIAGE`（D-011 未冻结凭据线格式），源码常量处注释必须写明它适用与 `length` 同一条退场纪律：架构源冻结凭据承载方式后即改用公共形态并删除该位序约定；`lumio.mvp.v0` 里的 `mvp` 与 `v0` 是退场标记，**不得去掉**。
- [ ] `TokenNeverReachesEnvelopeTest`：断言 token 与 nonce 两段在 WebSocket 建立完成后即被清除，且本工程不存在把它们写入任何 `MvpEnvelopeWriter` 调用参数的代码路径（反射 + 源常量扫描）。
- [ ] 认证失败以 **WebSocket close `1008`** 拒绝，**不发送任何 Envelope**：测试 `BadCredentialClosesWith1008Test` 断言客户端收到 close status `1008` 且在此之前**零字节**应用数据；同时断言产出了一条 Release 作用域的 Audit 事件，且该事件含 `eventId` 与 `timestamp` 两个 required 字段（`contract-mirror/schemas/logging-event.schema.json` 实测 `required` 7 项且 `additionalProperties:false`，缺任一项即产不出合法事件）——两项由 `Observability` 内部填充，本工程只调用 `IAuditWriter.WriteReleaseScopedReject(...)`，不自行构造时间戳。
- [ ] 断线检测三源各有一条测试：`CloseFrameDetectedTest`（对端发 Close 帧）、`ReceiveThrowsDetectedTest`（底层 `ReceiveAsync` 抛出）、`IdleDeadlineDetectedTest`（空闲截止 provisional 15 秒，经 `ITimerService` 投递 `ConnectionCommand.Close`，用 `FakeMonotonicClock` 推进虚拟时间，**不自建轮询线程**）。
- [ ] 服务端主动关闭入口：`Close(TransportConnectionId, ConnectionCloseReason)` 能在任意时刻关闭指定连接；测试 `ServerInitiatedCloseTest` 断言 `ConnectionCloseReason.MaintenanceKick` 时客户端先收到已排队的出站消息再收到 Close 帧（断开前 flush ≤ 1 秒）。
- [ ] `ws://` 与 `wss://` 分档：`allowInsecureLoopback` 默认 `false`；为 `false` 时用 `ws://` 前缀 `BindEndpoint` 返回 `BindEndpointResult { Bound = false }` 并带已注册 `StableErrorId`；为 `true` 时只允许 `127.0.0.1` / `::1` 且只在 Host Profile 为 `LocalSplitProcess` / `LocalEmbedded` 时生效。测试 `InsecureLoopbackGatingTest` 覆盖这三种组合。本卡**不实现** `wss://` 真实证书链路（独立后续卡），但 `RequireTls = true` 的配置路径必须能拒绝非 TLS 端点而不是静默降级。
- [ ] `NoSocketTypeTest`：断言本工程不直接使用 `System.Net.Sockets.Socket`（由 `eng/banned-public-api.txt` 的分析器条目在构建期强制；把一次故意引入 `Socket` 后 `dotnet build` 报 `RS0030` 的输出写进交回物）。
- [ ] 端到端载体自测：`CarrierRoundTripsValidFixtureTest` 在同进程内起一个真实 Kestrel 监听（端口 `0`），用 `System.Net.WebSockets.ClientWebSocket` 连接，收发镜像 `fixtures/valid/replication-handshake.json` 的字节，断言 `Transport` 核心侧收到的 `ValidatedEnvelopeBytes.Header.MessageType == "Handshake"`。
- [ ] `cd /Users/cui/LumioGames/LumioServer/mvp-host && bash eng/verify-all.sh` 退出码 0，末行 `MVP_HOST_VERIFY_OK`。
- [ ] `cd mvp-host && dotnet test tests/Lumio.Server.MvpHost.Transport.WebSocket.Tests/Lumio.Server.MvpHost.Transport.WebSocket.Tests.csproj -c Release --no-build` 输出 `Passed!  - Failed: 0`。
- [ ] 仓级收口门槛：`node .spec/tools/spec-lint.mjs` → `spec-lint: OK` 退出码 0；`node --test .spec/tools/spec-lint.test.mjs` → `fail 0` 退出码 0。
- [ ] `git status --porcelain` 只列出 `mvp-host/src/Lumio.Server.MvpHost.Transport.WebSocket/**` 与 `mvp-host/tests/Lumio.Server.MvpHost.Transport.WebSocket.Tests/**`（与同 wave 的 session 卡文件集交集为空）。
- [ ] 未越界实现任何 `mvp-host/absences.json` 列出的条目；未向该文件追加条目。特别地：未新增任何 ErrorCode（`ABS-TRANSPORT-PROFILE-ID` 要求 WebSocket 能力字符串只作本仓私有 `provisional` 常量，不得公共化），未实现分片重组（`ABS-WIRE-FRAGMENTATION`），未定义任何凭据 blob 的内部格式、算法、轮换或 nonce 派生——`ABS-AUTH-CREDENTIAL-CARRIAGE` 只登记「子协议位序承载」这一搬运方式，本卡按该登记搬运不透明字节，不越界定义格式。

## 依赖

`implement-mvp-transport-core-and-bounded-queues`, `implement-mvp-auth-stub-and-permission-gate`

## 接口

Consumes:

- 来自 `implement-mvp-transport-core-and-bounded-queues`：`TransportService.Create(...)`、`ITransportControlPort`、`IIngressReader`、`IEgressWriter`、`PassThroughFaultPolicy`、`TransportProvisionalDefaults`；以及 SPI 契约「一次 `ReceiveAsync` 循环组装出的一段字节 = 一个完整 Envelope，累计超过 `TransportEndpointOptions.MaxMessageBytes` 时返回 `CarrierReceive { Received = false, Closed = true }` 而不是继续分配」。
- 来自 `implement-mvp-auth-stub-and-permission-gate`：通道认证输入契约（`Sec-WebSocket-Protocol` 三段 `lumio.mvp.v0, <opaqueTokenB64Url>, <opaqueNonceB64Url>`；第 2 段解 base64url 后构造 `OpaqueCredentialInput`，第 3 段原样作为 `VerificationContext.Nonce`）；`ICredentialVerifier.Verify(OpaqueCredentialInput, in VerificationContext)`；`MvpAntiReplayWindow.Check(PrincipalId, string, MonotonicInstant)`；`IAuditWriter.WriteReleaseScopedReject(...)`。
- 来自 `define-mvp-host-contracts-and-audit-surface`：`IByteCarrier`、`CarrierAccept`、`CarrierReceive`、`TransportConnectionId`、`ConnectionCloseReason`、`TransportEndpointOptions`、`BindEndpointResult`；`TestKit.FakeMonotonicClock`。

Produces（命名空间 `Lumio.Server.MvpHost.Transport.WebSocket`）:

- `public sealed class WebSocketByteCarrier : IByteCarrier, System.IAsyncDisposable { public static WebSocketByteCarrier Create(in WebSocketCarrierOptions options, ICredentialVerifier verifier, IAntiReplayWindow antiReplay, IMonotonicClock clock, ITimerService timers, IAuditWriter audit); public System.Threading.Tasks.ValueTask<CarrierAccept> AcceptAsync(System.Threading.CancellationToken ct); public System.Threading.Tasks.ValueTask<CarrierReceive> ReceiveAsync(TransportConnectionId c, System.Memory<byte> buffer, System.Threading.CancellationToken ct); public bool TrySend(TransportConnectionId c, System.ReadOnlyMemory<byte> bytes); public bool Close(TransportConnectionId c, ConnectionCloseReason reason); public string BoundUri { get; } }`
- `public readonly record struct WebSocketCarrierOptions(string ListenUri, bool RequireTls, bool AllowInsecureLoopback, string HostProfile, int MaxMessageBytes, int MaxConnections, int IdleTimeoutSeconds, string ProductId, string GameReleaseId, string ReleasePoolId);`
- `public static class WebSocketCarrierConstants { public const string Subprotocol = "lumio.mvp.v0"; public const int CloseStatusPolicyViolation = 1008; public const string ProvisionalTransportCapability = "WebSocketTransport"; }`——`ProvisionalTransportCapability` 是**本仓私有的 Host Profile 声明**，源码处注释写明 `provisional, replace with registered ID after R-00258`，不得当作公共 Capability ID 使用。
