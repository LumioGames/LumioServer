# MVP C# 宿主 · 首批实现卡（13 张）

R-00260 的实现卡集合。设计真值是 [`../2026-08-28-mvp-csharp-host-design.md`](../2026-08-28-mvp-csharp-host-design.md)；卡片格式的单一权威是 [`../../../.spec/tasks/README.md`](../../../.spec/tasks/README.md)。

| 项 | 值 |
|---|---|
| 架构基线 BaselineId | `LGE-V1.4-2026-08-27` |
| 来源需求 | R-00260（RM-00006 / MS-00001，P0） |
| 卡片数量 | 13（wave 0–8）|
| 收口门槛（仓级） | `node .spec/tools/spec-lint.mjs` + `node --test .spec/tools/spec-lint.test.mjs` |
| 收口门槛（C# 侧） | `cd mvp-host && bash eng/verify-all.sh`（成功末行 `MVP_HOST_VERIFY_OK`）；集成显式入口 `bash eng/verify-integration.sh`（成功末行 `MVP_HOST_INTEGRATION_OK`） |

> 本目录是**待落单的卡片草案**。开工时由主 loop 按 wave 复制进仓根 `.spec/tasks/`（多宿主共享任务真值目录），完成后按 `.spec/tasks/README.md` 的目录纪律删除；本目录保留为设计包的一部分。卡片放在 `docs/` 下不受 `spec-lint` 的任务卡 frontmatter 校验管辖（该校验只覆盖 `.spec/tasks/` 根目录），因此格式合规由 reviewer 人工核对。

## 卡表

| # | slug | 一句话目标 | wave | 依赖 |
|---|---|---|---|---|
| 1 | [`scaffold-mvp-host-build-baseline`](scaffold-mvp-host-build-baseline.md) | 建立 `mvp-host/` 构建根、SDK 与隔离校验脚本、缺席清单，并新增独立 dotnet CI job | 0 | 无 |
| 2 | [`vendor-architecture-contracts-and-fixture-mirror`](vendor-architecture-contracts-and-fixture-mirror.md) | 以只读镜像加 sha256 锁引入 6 个 C# 生成 artifact 与 4 份 schema、16 条 fixture | 1 | 1 |
| 3 | [`implement-mvp-host-platform-primitives`](implement-mvp-host-platform-primitives.md) | 实现 host-runtime 等价最小面：单调时钟、Timer 类型化投递、有界端口、具名受监督线程 | 1 | 1 |
| 4 | [`implement-mvp-envelope-wire-and-fixture-gate`](implement-mvp-envelope-wire-and-fixture-gate.md) | 实现 `MvpEnvelopeDocument`、双层校验、gate 六项判定、9 个按方向分组的 writer 与出站 exact-set 断言 | 2 | 2 |
| 5 | [`define-mvp-host-contracts-and-audit-surface`](define-mvp-host-contracts-and-audit-surface.md) | 定义跨模块唯一契约面与 Audit/Diagnostic 最小写入面，并建立全局架构门禁 | 3 | 3, 4 |
| 6 | [`implement-mvp-transport-core-and-bounded-queues`](implement-mvp-transport-core-and-bounded-queues.md) | 实现载体无关的连接注册表、连接代次、分配前校验闸、四条有界队列与故障装饰器 | 4 | 5 |
| 7 | [`implement-mvp-auth-stub-and-permission-gate`](implement-mvp-auth-stub-and-permission-gate.md) | 实现 injected exact-byte verifier、防重放窗口、不可变 `PermissionGrant` 与 gate 执行体 | 4 | 5 |
| 8 | [`implement-mvp-world-slot-aggregate-and-sim-port-stub`](implement-mvp-world-slot-aggregate-and-sim-port-stub.md) | 实现 `WorldSlotHost` 聚合根（前向迁移表与 `anyActiveTo` 规则两份独立集合）与 `IWorldSimulationPort` 参考存根 + `IWorldMutationSink` 实现 | 4 | 5 |
| 9 | [`implement-mvp-websocket-carrier-adapter`](implement-mvp-websocket-carrier-adapter.md) | 实现 `IByteCarrier` 的 WebSocket 版本：监听、子协议 token 终结、一消息一信封、Close 与空闲超时 | 5 | 6, 7 |
| 10 | [`implement-mvp-session-admission-saga-and-reconnect`](implement-mvp-session-admission-saga-and-reconnect.md) | 实现 `ServerConnectionSession` 八态、Admission saga 八步与恰好一次补偿、重连窗口与复制编排 | 5 | 6, 7, 8 |
| 11 | [`assemble-mvp-host-app-and-smoke-client`](assemble-mvp-host-app-and-smoke-client.md) | 组装可执行 `lumio-mvp-host` 与自带 `SmokeClient`，落实 CLI 契约与测试控制面门控 | 6 | 9, 10 |
| 12 | [`verify-a1-alpha-cross-process-replication-loop`](verify-a1-alpha-cross-process-replication-loop.md) | A1-α 跨进程复制与重连全环自动化验收，并声明 A1-β 因 **B4 + B8** 而 BLOCKED | 7 | 11 |
| 13 | [`writeback-csharp-standards-and-dual-machine-evidence`](writeback-csharp-standards-and-dual-machine-evidence.md) | 在 `code-style.md` / `testing.md` 追加并列的 C# 小节（纯追加），并回填 Windows 侧 SDK 一手证据 | 8 | 1, 2, 12 |

**后置跨仓卡（本批次不落单，需总调度跨仓排期）**：`verify-a1-beta-bot-cross-process-mining` —— A1 字面退出条件正本，依赖架构源 **B4 + B8**（分别解冻下行的公共状态载荷字段与上行的 client→server gameplay 输入承载）与 LumioClient **CC-1..CC-5 + CC-8 + CC-9**。

## 依赖 DAG

> **2026-08-28 修订**：原 wave 0 的 `fix-repository-policy-baseline-drift` **已作废删除**——其目标已由上游
> `origin/main` 的 `9fe0cd7` 达成（把 repository-policy 的仓库边界断言从 v1.2 retarget 到 v1.4）；本轮在
> `637b464` 上实测 16 条断言 **16/16 通过**。卡集 14 → **13** 张，全部 wave 前移一档。

```
wave 0:  [1]  scaffold-mvp-host-build-baseline                            (无前置)
              |
wave 1:  [2]  vendor-architecture-contracts   [3] implement-platform-primitives   (并行)
              |                                     |
wave 2:  [4]  implement-envelope-wire ----------------+
              |                                     |
wave 3:  [5]  define-host-contracts-and-audit <------+
              |
wave 4:  [6] transport-core   [7] auth-stub   [8] world-slot + sim-port-stub       (三路并行)
              |                   |                |
wave 5:  [9]  websocket-carrier <-+                |
         [10] session-admission-saga <-------------+                               (两路并行)
              |
wave 6:  [11] assemble-mvp-host-app-and-smoke-client
              |
wave 7:  [12] verify-a1-alpha-cross-process-replication-loop
              |
wave 8:  [13] writeback-csharp-standards-and-dual-machine-evidence
```
