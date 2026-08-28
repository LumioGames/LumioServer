---
status: pending
---

# A1-α 跨进程协议与生命周期全环自动化验收，并显式声明 A1-β 因 D-009（上行）与 ADR-028（下行状态载荷）双前置而 BLOCKED

以真实 `ws://127.0.0.1:<port>` 套接字、两个独立进程（`lumio-mvp-host` 与 `lumio-mvp-smoke-client`）自动化跑通设计 §9.1 的 17 步。它证明的是**协议与生命周期闭环**：跨进程 WSS 传输、通道认证与防重放、Admission saga 八步、复制状态机全链路、revision 严格前进、同连接 Resync 与跨连接 Full Resync 两条不同路径、连接代次与 Slot epoch 隔离、Quiesce 原子序列。**这是本批次能自动化交付的 A1 服务端能力全部**。

A1 字面退出条件里的「Bot 跨进程挖方块 / 看到方块被挖」**整体**属 A1-β，因**双前置**而 BLOCKED：下行侧公共 `FullSnapshot` / `Delta` 的 typed body 由 ADR-028（Accepted）冻结且其 Alternatives 明文否决 free-form payload，本仓不自行补字段（设计 §5.6-A），客户端因此看不到任何世界内容；上行侧 8 个冻结 `messageType` 中无一能承载 client→server gameplay 命令（D-009）。本卡显式声明并划出边界，不做任何绕过。

设计出处：`docs/specs/2026-08-28-mvp-csharp-host-design.md` §9.1 / §9.2 / §9.3 / §5.6。

## 涉及范围

- `mvp-host/tests/Lumio.Server.MvpHost.Integration.Tests/**`
- `mvp-host/eng/verify-integration.sh`
- `mvp-host/eng/verify-integration.ps1`

## 验收标准

- [ ] **先失败证据**：先提交 17 步的全部集成断言并在 `SmokeClient` 的 `a1-alpha` 场景尚未接通时执行 `cd mvp-host && bash eng/verify-integration.sh`，记录非零退出与 `Failed!` 汇总行；接通后重跑记录 `MVP_HOST_INTEGRATION_OK` 与退出码 0。两次输出写进交回物。
- [ ] `Lumio.Server.MvpHost.Integration.Tests` 声明 `<MvpHostProductionProject>false</MvpHostProductionProject>`，以**独立进程**方式（`System.Diagnostics.Process`）拉起 `lumio-mvp-host` 与 `lumio-mvp-smoke-client`，**不把它们作为库调用**；测试 `ProcessesAreRealSubprocessesTest` 断言两个进程的 PID 不等于测试宿主 PID。宿主一律以 `--enable-test-control --audit-trace-file <path>` 启动：本卡的判定面是**两个 trace 文件**——客户端 `--trace-file`（记「C」）与服务端 `--audit-trace-file`（记「S」），逐步归属与设计 §9.1 的「判定面」列一致：步骤 **5 / 8 / 17 只能由 S 判定**（它们观测的是服务端内部状态，客户端根本看不到）；步骤 **2 / 3 / 7 / 9 / 11 / 12 / 13 / 14 / 15 由 C + S 联合判定**；步骤 **1 / 4 / 6 / 10 / 16 由 C 判定**。
- [ ] `mvp-host/eng/verify-integration.sh` 与 `.ps1` 自解析脚本目录后 `cd` 到 `mvp-host/`，执行 `dotnet build build.proj -c Release` 后运行 `dotnet test tests/Lumio.Server.MvpHost.Integration.Tests/Lumio.Server.MvpHost.Integration.Tests.csproj -c Release --no-build`；成功时最后一行打印 `MVP_HOST_INTEGRATION_OK` 并退出 0，失败时打印 `MVP_HOST_INTEGRATION_FAIL` 并非零退出。该脚本**不进** `eng/verify-all.sh`（集成测试显式触发，依据 `.spec/knowledge/standards/testing.md`）。
- [ ] 步骤 1：SmokeClient 以 `Sec-WebSocket-Protocol: lumio.mvp.v0, <token>, <nonce>` 发起 upgrade，断言服务端回选 `lumio.mvp.v0` 且 HTTP 状态为 101。
- [ ] 步骤 2（判定面 C + S）：以错误 token 的第二次 upgrade，断言 close `1008`、**在此之前零 Envelope 字节**；并在**服务端 trace** 中断言存在一条 `kind:"audit"` 记录，其 `category=="Audit"`、`severity=="Warn"`、`scope=="Release"`、`releasePoolId` 非空、**`sessionId` 为 `null`**、`eventId` 与 `timestamp` 均非空且分别匹配 `common.schema.json#/$defs/id` 与 `#/$defs/timestamp` 的正则。
- [ ] 步骤 3（判定面 C + S）：重放同一 `nonce`，断言被拒绝，且**服务端 trace** 中存在一条 `reasonCode=="SessionAntiReplay"` 的 `kind:"audit"` 记录；该次拒绝**不消耗**下一次合法握手的窗口配额（随后一次合法握手成功）。
- [ ] 步骤 4：服务端首帧 `messageType == "Handshake"`，且 `body` 的 key 集合**恰好** `{role}`、`role == "Server"`。
- [ ] 步骤 5（判定面 S）：客户端回 `Handshake{role:"Client"}`，断言**服务端 trace** 中依次出现 8 条 `kind:"ack"` 记录，`effect` 按 `ReadGate → Authenticate → MatchExactRelease → ReserveSlot → CommitSlot → CreateSession → BindConnection → StartReplication` **有序**（顺序只依据 `seq` 判定，不依赖文件系统时间），8 条共享同一 `admissionAttemptId` 且各带对应 `slotEpoch` / `connectionEpoch`；随后出现一条 `kind:"state"` 记录，`sessionState=="Active"`。
- [ ] 步骤 6（判定面 C）：服务端下发 `FullSnapshot`，断言过双层校验；`reliability == "Reliable"`（公共硬约束，`tools/lumio_contract.py:802-803`）；`body` 的 key 集合**恰好等于** `{snapshotId, tickId, sessionRevisionVector, schemaEpoch, mappingSetHash}`（`_REPLICATION_BODY_REQUIRED` 的 `FullSnapshot` 组，`tools/lumio_contract.py:355-364`，**不多不少**）；`body.sessionRevisionVector` 含全 7 字段；`chunkRevisionSet` 的 key 匹配 canonical 正则 `^c:(0|-?[1-9][0-9]{0,9}):(0|-?[1-9][0-9]{0,9}):(0|-?[1-9][0-9]{0,9})$`（逐字取自 `tools/lumio_contract.py:401` 的 `_CHUNK_KEY`，**不得写宽**——D-013 把 ChunkId format 列为「改动需新 ADR 与 BaselineId」的项）；`mappingSetHash` 等于 `MvpWireConstants.MappingSetHash`（64 个 `0`）。
- [ ] 步骤 7（判定面 C + S）：客户端回 `BaselineAck{snapshotId, confirmedRevision}` 被接受；断言 **Transport ACK 与 Baseline ACK 是两条不同路径**——C 侧：`BaselineAck` 送达前客户端 trace 中不存在任何 `direction:"in"` 且 `messageType == "Delta"` 的记录（复制基线不前进）；S 侧：服务端 trace 在本步**不产生任何新的 `kind:"ack"` 记录**（Baseline 确认不复用 admission saga 的 ack 通道，两者不是同一条路径）。实现层「不同类型 / 不同队列 / 不同处理方法」的断言由 `implement-mvp-session-admission-saga-and-reconnect` 的 `TransportAckSeparateFromBaselineAckTest` 承接。
- [ ] 步骤 8（判定面 S）：经 `POST /test-control/inject-world-mutation`（**带外测试控制面，不经任何 Envelope**）注入一次世界变更，断言**服务端 trace** 中出现一条 `kind:"state"` 记录，其 `authorityRevision` 由 `N` 变为 `N+1`；变更在注入后的**第一个 tick 内**应用——带外入口 `IWorldMutationSink.TryEnqueueOpaqueMutation` 只入队，由 Slot Owner Thread 在下一次 `RunTick` 开头排空（设计 §6.5）。
- [ ] 步骤 9（判定面 C + S）：服务端下发 `Delta`，断言 `body` 的 key 集合**恰好等于** `{baseSnapshotId, fromRevision, toRevision, mappingSetHash, confirmationSequence, tombstones}`（`_REPLICATION_BODY_REQUIRED` 的 `Delta` 组，**不多不少**）且 `tombstones == []`；`toRevision > fromRevision` 且 `toRevision` 等于步骤 8 的 `N+1`；`baseSnapshotId` 等于步骤 6 的 `snapshotId`。**本步证明的是「服务端权威 revision 前进并被复制到客户端」，不是「客户端看到世界内容」**——后者因公共 `FullSnapshot` / `Delta` 的 typed body 无状态载荷字段而属 A1-β（`ABS-REPLICATION-STATE-PAYLOAD`，设计 §5.6-A / §9.2）；本卡不得对世界内容做任何断言。
- [ ] 步骤 10：客户端回 `DeltaAck{confirmationSequence, toRevision}` 被接受。
- [ ] 步骤 11（判定面 C + S）：客户端故意跳过一个 `Delta`（同连接内模拟 gap），**由客户端发出** `ResyncRequest{resyncReason}`——客户端 trace 中该记录的 `direction` 为 `"out"`；服务端接收后下发新 `FullSnapshot`，且**握手计数不变**（同连接内 Resync 不重新握手）。同时断言**服务端从不下发** `ResyncRequest`：客户端 trace 中不存在任何 `direction:"in"` 且 `messageType == "ResyncRequest"` 的记录（公共契约里它由检测到 gap 的副本方发出，架构源 v1.4 §7.1）。
- [ ] 步骤 12（判定面 C + S）：经 `POST /test-control/kick`（`{"sessionId":"<id>","reasonCode":"MaintenanceKick"}`）断言客户端**先**收到 `MaintenanceKick` Envelope、**随后**连接关闭；`ServerConnectionSession` 进入 `ReconnectWindow` 的断言落在**服务端 trace** 的 `kind:"state"` 记录上（`sessionState=="ReconnectWindow"`）。
- [ ] 步骤 13（判定面 C + S）：客户端以**新连接代次**重连，断言重做通道认证（新 nonce）+ 完整 Handshake；`PermissionGrant` 重新派生的证据取**服务端 trace** 的 `kind:"state"` 记录中 `grantEpoch` 严格递增；且**不存在任何 Resume Token 路径**（客户端 trace 中无任何跳过握手的步骤）。
- [ ] 步骤 14（判定面 C + S）：重连后服务端下发**全新 `FullSnapshot`**（Full Resync）→ `BaselineAck` → 回到 `Active`（服务端 trace 出现 `sessionState=="Active"` 的 `kind:"state"` 记录）；断言该 `FullSnapshot` 的 `sessionRevisionVector` 携带的 revision **严格大于**断连前最后一条 `Delta` 的 `toRevision`。**这是本批次能证明的最强命题**：Full Resync 后基线严格前进。世界内容一致性（「客户端观察到的世界状态与服务端权威状态逐键相等」）**不在本卡范围**，它需要公共 body 的状态载荷字段，归 A1-β（`ABS-REPLICATION-STATE-PAYLOAD`）。
- [ ] 步骤 15（判定面 C + S）：让另一个会话在 `ReconnectWindow` 内不重连，断言**服务端 trace** 出现 `kind:"state"` 记录 `sessionState=="Expired"`；并在窗口边界同时触发到期与重连，断言由 `MvpSessionControlInbox` 串行裁决、输者收到带已注册 `StableErrorId` 的稳定错误（客户端 trace 可见），且该会话在服务端 trace 中的**最终 `sessionState` 唯一**（`Expired` 或 `Syncing` 二者之一，按 `seq` 取最后一条 `kind:"state"` 记录，不出现中间态）。
- [ ] 步骤 16：三条拒绝路径各一个用例——`productId` / `gameReleaseId` 不匹配 → `Error{errorClass:"Rejectable", reasonCode:"ReleaseMismatch"}`；旧连接代次的消息 → `StaleConnectionGeneration`；超 `maxMessageBytes` → 在分配前拒绝并断连。
- [ ] 步骤 17（判定面 S）：经 `POST /test-control/begin-drain` 触发 Quiesce，断言**服务端 trace** 中四条 `kind:"ack"` 记录的 `effect` 按 `AdmissionClosed → Drained → SnapshotCut → Stopped` **有序**出现（顺序只依据 `seq`），每条带非空 `slotEpoch`，且 Gate 关闭发生在停 Tick 之前。
- [ ] 17 步全部以**两个 trace 文件**与进程退出码作为判定依据，**不解析日志文本**：① 客户端 `--trace-file`（每行 `{"step","direction","messageType","assertion","passed","detail"}`），断言 `passed:false` 的行数为 0 且 `step` 覆盖 1..17 全集；② 服务端 `--audit-trace-file`（每行**恰 17 个键**：`seq`、`kind`、`eventId`、`timestamp`、`category`、`severity`、`scope`、`releasePoolId`、`sessionId`、`reasonCode`、`admissionAttemptId`、`effect`、`sessionState`、`authorityRevision`、`slotEpoch`、`connectionEpoch`、`grantEpoch`），断言每行字段集恰为该 17 键、`kind` 取值域恰为 `{"audit","ack","state"}`、`seq` 全局单调递增。测试 `BothTraceFilesAreWellFormedTest` 逐行校验两个文件的字段集。
- [ ] 端口使用 `--listen ws://127.0.0.1:0` 由 OS 分配、从 `MVP_HOST_READY` 行解析实际端口，**不硬编码端口**；`--allow-insecure-loopback` 显式给出并在测试注释中写明这是 dev-only 档（生产 Profile 不可表达）。
- [ ] 每个用例在结束时终止子进程并断言宿主退出码为 `0`（正常 Quiesce 退出），无残留进程；`NoLeakedProcessTest` 在整个测试类结束后断言无本测试启动的存活子进程。
- [ ] **A1-β 显式声明（不做任何绕过）**：本卡在测试类头注释与交回物中写明——A1 字面退出条件「Bot 跨进程挖方块 / 看到方块被挖」**整体**属 A1-β 且 BLOCKED，阻塞源是**双前置**：① **架构源 D-009 / B8（上行）**——8 个冻结 `messageType` 无一能承载 client→server 命令，原文禁止任何仓库发明 dispatch wire format；② **架构源 ADR-028 / B4（下行）**——`FullSnapshot` / `Delta` 的 typed body 无状态载荷字段，该 ADR 的 Alternatives 原文 `Keeping a free-form payload was rejected because two implementations can pass the gate and disagree on Snapshot identity.` 明文否决 free-form payload，本仓因此不自行补字段。LumioClient 侧对应 **CC-8**（上行输入面无法表达「挖哪个方块」）与 **CC-9**（下行解码公共状态载荷并更新 bot 可观察世界状态）。对应 `absences.json` 的 `ABS-CLIENT-UPLINK-COMMAND` **与** `ABS-REPLICATION-STATE-PAYLOAD`。测试 `NoUplinkGameplayCommandTest` 断言 trace 中不存在任何 `direction:"out"` 且 `messageType` 不在 `{Handshake, BaselineAck, DeltaAck, ResyncRequest}` 内的记录。
- [ ] **本仓可独立完成 / 必须等对方仓交付的切分**（写进交回物，逐条）：本卡验收的**全部 17 步**只依赖本仓的 `lumio-mvp-host` 与 `lumio-mvp-smoke-client` 两个进程，**今天就能独立跑绿**，不依赖 LumioClient 与 LumioGameRuntime 的任何产物；与 LumioClient `modules/bot/host` 真机对接需要对方仓的 CC-1（`ClientConnectionCreateRequest` 增 endpoint 与凭据入参）、CC-2（WSS 客户端传输）、CC-3（上行帧改为 Envelope 形状）、CC-4（按设计 §8.1 映射表注入 classifier 与 message kind map）、CC-5（bot host CLI 支持 `--transport ws --endpoint --token`）、CC-7（远程工厂下 `Loopback` 为 null 的调用方处理）；A1-β 另需 **CC-8**（输入面能表达「挖哪个方块」，依赖架构源 B8 先解冻）与 **CC-9**（解码架构源冻结的公共状态载荷字段并据以更新 bot 可观察的世界状态，依赖架构源 B4 先解冻）——以上 CC-1..CC-5、CC-7、CC-8、CC-9 **均不在本卡范围**；与 LumioGameRuntime 的对接需要 RQ-1 / RQ-2 / RQ-3，同样不在本卡范围。这些一律作为后置跨仓卡由总调度排期。**本卡是集成卡雏形**：它把两个真进程、真套接字、真 Envelope 的链路跑通并留下两份机器可判的 trace，证明**协议与生命周期闭环**；它**不证明**「Bot 看到方块被挖」——不得把 A1-β 的任何断言伪装成本批次可完成。
- [ ] `cd /Users/cui/LumioGames/LumioServer/mvp-host && bash eng/verify-integration.sh` 退出码 0，末行 `MVP_HOST_INTEGRATION_OK`，完整输出写进交回物。
- [ ] `cd /Users/cui/LumioGames/LumioServer/mvp-host && bash eng/verify-all.sh` 退出码 0，末行 `MVP_HOST_VERIFY_OK`（默认验证命令**不**包含集成测试，本条证明加入集成工程后默认链路仍绿且未被拖慢）。
- [ ] 仓级收口门槛：`node .spec/tools/spec-lint.mjs` → `spec-lint: OK` 退出码 0；`node --test .spec/tools/spec-lint.test.mjs` → `fail 0` 退出码 0。
- [ ] `git status --porcelain` 只列出本卡「涉及范围」的三个路径。
- [ ] 未越界实现任何 `mvp-host/absences.json` 列出的条目；未向该文件追加条目。

## 依赖

`assemble-mvp-host-app-and-smoke-client`

## 接口

Consumes:

- 来自 `assemble-mvp-host-app-and-smoke-client`：可执行 `lumio-mvp-host` 的 CLI（`--listen` / `--allow-insecure-loopback` / `--host-profile` / `--product-id` / `--game-release-id` / `--shared-secret-file` / `--reconnect-window-seconds` / `--enable-test-control` / `--test-control-listen` / `--audit-trace-file`）、就绪行 `MVP_HOST_READY listen=<uri> testControl=<uri 或 ->`、退出码 `0` / `64` / `70`；**服务端 trace 文件契约**——`--audit-trace-file <path>` 产出每行一个 JSON 对象，字段集恰为 17 个键 `{seq, kind, eventId, timestamp, category, severity, scope, releasePoolId, sessionId, reasonCode, admissionAttemptId, effect, sessionState, authorityRevision, slotEpoch, connectionEpoch, grantEpoch}`，`kind` 取值域恰为 `{"audit","ack","state"}`，`seq` 全局单调递增，且 `--audit-trace-file` 仅在 `--enable-test-control` 同时给出时有效（否则退出码 `64`）；测试控制面三条路由 `POST /test-control/begin-drain`（`{"graceSeconds":<int>}`）、`POST /test-control/kick`（`{"sessionId":"<string>","reasonCode":"MaintenanceKick"}`）、`POST /test-control/inject-world-mutation`（`{"sessionId":"<string>","opaqueCommandBase64":"<string>"}`），响应体统一 `{"accepted":<bool>,"stableErrorId":<string|null>}`；可执行 `lumio-mvp-smoke-client` 的 CLI（`--endpoint` / `--token-file` / `--nonce` / `--product-id` / `--game-release-id` / `--scenario` / `--trace-file`）、8 个场景名、trace 每行字段集 `{"step","direction","messageType","assertion","passed","detail"}`、退出码 `0` / `64` / `65` / `70`。
- 来自 `scaffold-mvp-host-build-baseline`：`bash eng/verify-all.sh` 成功末行 `MVP_HOST_VERIFY_OK`；`verify-all` 的测试循环排除 `*.Integration.Tests`，因此本卡新增的集成工程不进默认链路。

Produces:

- `bash mvp-host/eng/verify-integration.sh`（Windows：`pwsh mvp-host/eng/verify-integration.ps1`）：成功末行 `MVP_HOST_INTEGRATION_OK` 退出码 0，失败打印 `MVP_HOST_INTEGRATION_FAIL` 并非零退出。这是 A1-α 的唯一显式触发入口，后置跨仓卡 `verify-a1-beta-bot-cross-process-mining`（本批次不落单）在其基础上扩展。
