---
status: pending
---

# 以只读镜像加 sha256 锁引入架构源的 6 个 C# 生成 artifact 与 4 份 schema、16 条 fixture

本仓根没有 `schemas/` / `fixtures/` / `ids/` 镜像，CI 里也没有跨仓路径；靠环境变量指向兄弟仓会让 CI 上等于没有这道门。本卡把契约真值以字节级只读镜像携带进 `mvp-host/`，配 sync/verify 双脚本与漂移退出码，使后续所有协议卡「今天就能独立跑绿」。

设计出处：`docs/specs/2026-08-28-mvp-csharp-host-design.md` §3.2 / §5.1 / §5.5 / §7.2。

## 涉及范围

- `mvp-host/contract-mirror/MIRROR.md`
- `mvp-host/contract-mirror/schemas/replication-envelope.schema.json`
- `mvp-host/contract-mirror/schemas/common.schema.json`
- `mvp-host/contract-mirror/schemas/protocol-permission-gate.schema.json`
- `mvp-host/contract-mirror/schemas/logging-event.schema.json`
- `mvp-host/contract-mirror/fixtures/valid/replication-handshake.json`
- `mvp-host/contract-mirror/fixtures/valid/replication-full-snapshot.json`
- `mvp-host/contract-mirror/fixtures/valid/replication-baseline-ack.json`
- `mvp-host/contract-mirror/fixtures/valid/replication-delta.json`
- `mvp-host/contract-mirror/fixtures/valid/replication-delta-ack.json`
- `mvp-host/contract-mirror/fixtures/valid/replication-resync.json`
- `mvp-host/contract-mirror/fixtures/valid/replication-maintenance-kick.json`
- `mvp-host/contract-mirror/fixtures/valid/replication-error.json`
- `mvp-host/contract-mirror/fixtures/valid/protocol-permission-gate-accept.json`
- `mvp-host/contract-mirror/fixtures/valid/state-machine-world-slot-host.json`
- `mvp-host/contract-mirror/fixtures/valid/logging-auth-reject-audit.json`
- `mvp-host/contract-mirror/fixtures/invalid/replication-gap-without-resync.json`
- `mvp-host/contract-mirror/fixtures/invalid/replication-missing-snapshot-identity.json`
- `mvp-host/contract-mirror/fixtures/invalid/replication-unregistered-message-type.json`
- `mvp-host/contract-mirror/fixtures/invalid/replication-integrity-value-mismatch.json`
- `mvp-host/contract-mirror/fixtures/invalid/protocol-permission-gate-stale-generation.json`
- `mvp-host/src/Lumio.Server.MvpHost.GeneratedContracts/**`
- `mvp-host/tests/Lumio.Server.MvpHost.GeneratedContracts.Tests/**`
- `mvp-host/eng/generate-contracts.sh`
- `mvp-host/eng/generate-contracts.ps1`
- `mvp-host/eng/verify-generated-contracts.sh`
- `mvp-host/eng/verify-generated-contracts.ps1`
- `mvp-host/eng/sync-contract-mirror.sh`
- `mvp-host/eng/sync-contract-mirror.ps1`
- `mvp-host/eng/verify-contract-mirror.sh`
- `mvp-host/eng/verify-contract-mirror.ps1`
- `mvp-host/eng/contract-mirror.sha256`
- `mvp-host/eng/verify-all.sh`（在上游卡的版本上插入两条契约校验步骤）
- `mvp-host/eng/verify-all.ps1`（同上）
- `mvp-host/README.md`（补记契约镜像的同步与校验命令）

## 验收标准

- [ ] **先失败证据**：先写 `mvp-host/tests/Lumio.Server.MvpHost.GeneratedContracts.Tests` 的三个测试（下列 `MirrorHashTest` / `BaselineSentinelTest` / `ContractArtifactDebtTest`）并在镜像文件尚未落地时运行 `cd mvp-host && dotnet test tests/Lumio.Server.MvpHost.GeneratedContracts.Tests/Lumio.Server.MvpHost.GeneratedContracts.Tests.csproj -c Release`，记录 `Failed!` 汇总行；镜像与生成物落地后重跑记录 `Passed!  - Failed: 0`。两次输出写进交回物。
- [ ] `mvp-host/contract-mirror/` 下的 4 份 schema 与 16 条 fixture 与 `$LUMIO_ARCHITECTURE_ROOT` 对应文件**字节相同**（用 `shasum -a 256` 双侧比对，逐文件哈希写进交回物）。镜像集合**恰为**「涉及范围」列出的这 20 个文件：不得增补 `fixtures/invalid/session-revision-legacy-chunk-key.json`——实测它是裸的 `session-revision-vector` 版本向量对象（顶层键为 `tickId / gameRevision / voxelWorldRevision / chunkRevisionSet / replicationRevision / configRevision / schemaEpoch`），不是 replication 信封，属本仓**不镜像**的那份 schema；增补会让镜像与 4 份 schema 的集合不自洽，也与设计 §5.5 / §14 J12 已定的「16 条 fixture / 20 个受哈希锁文件」计数冲突。canonical chunk-key 的拒绝用例改由 `implement-mvp-envelope-wire-and-fixture-gate` 自造（不新增 fixture）。
- [ ] `mvp-host/eng/contract-mirror.sha256` 逐行列出**镜像自架构源的 20 个文件**（4 份 schema + 16 条 fixture）的 sha256 与仓库相对路径；本仓手写的 `MIRROR.md` **不进哈希清单**——架构源没有对应文件，进清单会与上一条「与架构源对应文件字节相同」互斥。`cd mvp-host && bash eng/verify-contract-mirror.sh` 退出码 0；人为改动镜像中任一字节后重跑退出码为 `33` 并打印被改动的路径（把这次故意破坏与恢复的两段输出写进交回物）。
- [ ] `mvp-host/contract-mirror/MIRROR.md` 记录：来源仓绝对路径、来源 commit sha、`BaselineId = LGE-V1.4-2026-08-27`、同步命令 `bash eng/sync-contract-mirror.sh`、校验命令 `bash eng/verify-contract-mirror.sh`、以及退场条件（架构源发布可用的 C# 契约类型后本镜像随本仓 DTO 一并删除）；另记录下一条要求的「本目录不放任何 `.csproj` / `Directory.Build.*`」的实测理由。该文件是 `scaffold-mvp-host-build-baseline` 写定的 `absences.json` 中 `ABS-AUTH-CREDENTIAL-CARRIAGE` 的 `source` 路径（`Architecture.Tests` 的 `AbsencesManifestTest` 校验该路径真实存在），因此**不得改名或移除**。
- [ ] `mvp-host/contract-mirror/` 下**不存在任何 `.csproj`、`.cs`、`Directory.Build.props` / `Directory.Build.targets`**——契约生成物走**源码拷贝**而非工程引用（设计 §4 的四条定死）。理由必须写进 `MIRROR.md`：本轮实测子目录空壳 `Directory.Build.props` **拦不住**父级 `Directory.Build.targets`（MSBuild 对 `.props` 与 `.targets` 的向上查找各自独立），构建输出 `VALIDATE-RAN TFM=net8.0 RootPropsSeen=` 后随即 `error : TFM must be net10.0 but was net8.0` 与 `Build FAILED`，因此架构源原样 `net8.0` 的 6 个生成工程无法作为工程引入本构建根。验证：`cd mvp-host && find contract-mirror -name '*.csproj' -o -name '*.cs' -o -name 'Directory.Build.*'` 无输出（把该命令与空输出写进交回物）。
- [ ] `mvp-host/src/Lumio.Server.MvpHost.GeneratedContracts/` 内含架构源 `packages/csharp/` 6 个 artifact 的只读**源码**拷贝（放在 `Generated/` 子目录且该目录内文件不得手改）——实测这 6 个 artifact 共 **7** 个 `.cs` 源文件（`Lumio.Gen.LanguageBinding` 含 `Bindings.cs` 与 `RootAbi.cs` 两个，其余五个各一个），只拷 `.cs`、**不拷 `.csproj`**；外加一个手写的 `GeneratedContractManifest`，记录架构源 commit、`BaselineId`、`schemaEpoch` 与拷进 `Generated/` 的**每个 `.cs` 文件**的 sha256（7 项）。该工程零 `PackageReference`，`MvpHostLayer` 为 `0`，无 `ProjectReference`。拷进 `Generated/` 的 `.cs` 随本工程以 `net10.0` 编译；为规避 `TreatWarningsAsErrors` + 分析器，`Generated/` 下放一份目录级 `.editorconfig` 声明 `generated_code = true`，该文件由「涉及范围」的 `mvp-host/src/Lumio.Server.MvpHost.GeneratedContracts/**` 通配覆盖。
- [ ] `cd mvp-host && bash eng/verify-generated-contracts.sh` 在 `$LUMIO_ARCHITECTURE_ROOT` 可达时重新生成到临时目录并逐文件比对，一致则退出 0；人为改动 `Generated/` 内任一字节后重跑退出码为 `32`（把故意破坏与恢复的两段输出写进交回物）。`$LUMIO_ARCHITECTURE_ROOT` 不可达时只校验本地 manifest 记录的哈希，仍能退出 0。
- [ ] `MirrorHashTest`：测试在运行期读取 `contract-mirror/`，断言**镜像自架构源的 20 个文件**的 sha256 与 `eng/contract-mirror.sha256` 逐行相等，且 `contract-mirror/` 下**除显式白名单 `MIRROR.md` 外**不存在未登记的文件。白名单**只有 `MIRROR.md` 一项**（`Directory.Build.props` 已按上文从该目录删除），否则「字节级镜像哈希」与「无未登记文件」两条互斥、测试落地即恒红。
- [ ] `BaselineSentinelTest`：断言 `Lumio.Gen.ContractTypes.Catalog.BaselineId == "LGE-V1.4-2026-08-27"`，且 `Catalog.SchemaIds` 含 `replication-envelope`、`protocol-permission-gate`、`logging-event`，且 `Catalog.StableErrorIds` 的元素个数为 43。
- [ ] `ContractArtifactDebtTest`（自过期守卫）：断言 (a) `Lumio.Gen.LanguageBinding.Bindings` 中存在把 `replication-envelope` 映射到 C# 类型名 `ReplicationEnvelope` 的条目；(b) **在 `Lumio.Server.MvpHost.GeneratedContracts` 这一个程序集内、`Lumio.Gen.*` 命名空间下**反射不到名为 `ReplicationEnvelope` 的公开类型；(c) `Lumio.Gen.ProtocolPermissionValidator.ActivePermissionFields.Names` 长度为 15（实测架构源该数组恰 15 个字符串字面量），且**该命名空间下**不存在任何公开的校验方法（只有字段名表）。测试注释写明两件事：① 任一条变红即表示架构源生成物已到位，是删除本仓临时 DTO 与手写 gate 的指令；② **不得**写成「遍历本工程引用到的全部 `Lumio.Gen.*` 程序集」——源码拷贝方案下不存在独立的 `Lumio.Gen.*` 程序集，那样写会因集合恒空而**静默失效**（设计 §4 第 4 条、§5.3 护栏 5）。
- [ ] **不硬编码架构源 fixture 总数**：全仓测试中不出现对 `python3 tools/lumio_contract.py validate` 输出条数（如 160 / 167）的断言；断言只针对本仓镜像的 20 个文件与 `BaselineId` 哨兵。另按设计 §5.5 逐条归属：8 条正向 replication + 4 条反向 replication + 2 条 gate + 1 条 world-slot 状态机 + 1 条 logging audit = **16 条 fixture**；加 4 份 schema 即 20 个受哈希锁文件。
- [ ] `mvp-host/eng/verify-all.sh` 与 `.ps1` 在 `eng/verify-sdk` 之后、`dotnet restore` 之前插入 `eng/verify-contract-mirror` 与 `eng/verify-generated-contracts` 两步，失败时打印 `MVP_HOST_VERIFY_FAIL contract-mirror` 或 `MVP_HOST_VERIFY_FAIL generated-contracts` 并非零退出。
- [ ] `mvp-host/README.md` 补记 `bash eng/sync-contract-mirror.sh`、`bash eng/verify-contract-mirror.sh`、`bash eng/generate-contracts.sh`、`bash eng/verify-generated-contracts.sh` 四条命令及其漂移退出码（33 / 32），并注明 `contract-mirror/` 与 `Generated/` 均为不得手改的生成/镜像物。
- [ ] `cd /Users/cui/LumioGames/LumioServer/mvp-host && bash eng/verify-all.sh` 退出码 0，末行 `MVP_HOST_VERIFY_OK`。
- [ ] `cd mvp-host && dotnet test tests/Lumio.Server.MvpHost.GeneratedContracts.Tests/Lumio.Server.MvpHost.GeneratedContracts.Tests.csproj -c Release --no-build` 输出 `Passed!  - Failed: 0`。
- [ ] 仓级收口门槛：`node .spec/tools/spec-lint.mjs` → `spec-lint: OK` 退出码 0；`node --test .spec/tools/spec-lint.test.mjs` → `fail 0` 退出码 0。
- [ ] 本卡改动文件集与 51 张 Rust 卡的 349 个独占文件交集为空（用上游卡 `scaffold-mvp-host-build-baseline` 验收里那条 `python3` 命令验证，输出 `[]`）。
- [ ] 未越界实现任何 `mvp-host/absences.json` 列出的条目；未向 `mvp-host/absences.json` 追加任何条目（该文件由 `scaffold-mvp-host-build-baseline` 一次性写定）。

## 依赖

`scaffold-mvp-host-build-baseline`

## 接口

Consumes:

- 来自 `scaffold-mvp-host-build-baseline`：工程目录布局（生产工程在 `mvp-host/src/<AssemblyName>/`、测试工程在 `mvp-host/tests/<AssemblyName>/`）；每个 csproj 必须声明 `<MvpHostLayer>`，测试工程必须声明 `<MvpHostProductionProject>false</MvpHostProductionProject>`；`PackageReference` 不带 `Version`；`bash eng/verify-all.sh` 成功末行 `MVP_HOST_VERIFY_OK`。

Produces:

- 程序集 `Lumio.Server.MvpHost.GeneratedContracts`（`MvpHostLayer=0`，零 `ProjectReference`、零 `PackageReference`），转发架构源生成类型，下游可直接使用的静态成员：`Lumio.Gen.ContractTypes.Catalog.BaselineId`（`string`）、`Catalog.SchemaIds`（`string[]`）、`Catalog.StableErrorIds`（`string[]`，43 值）、`Lumio.Gen.ContractTypes.StateTransitionTable.All`（`Transition(Machine, From, To, Event)` 三元组集合）、`Lumio.Gen.ProtocolPermissionValidator.ActivePermissionFields.Names`（`string[]`，15 值）、`Lumio.Gen.LanguageBinding.Bindings`（`Binding(SchemaId, RustType, CsharpType)` 集合）。
- 镜像路径常量（下游测试装载 fixture 与 schema 的唯一来源，路径相对 `mvp-host/`）：`contract-mirror/schemas/replication-envelope.schema.json`、`contract-mirror/schemas/common.schema.json`、`contract-mirror/schemas/protocol-permission-gate.schema.json`、`contract-mirror/schemas/logging-event.schema.json`、`contract-mirror/fixtures/valid/`（11 个文件）、`contract-mirror/fixtures/invalid/`（5 个文件）。`contract-mirror/` 下共 **21** 个文件 = 20 个受哈希锁的架构源镜像 + 1 个本仓手写的 `MIRROR.md`。
- `Lumio.Server.MvpHost.GeneratedContracts` 内公开静态类 `GeneratedContractManifest`，成员：`public static string ArchitectureBaselineId { get; }`、`public static string ArchitectureCommit { get; }`、`public static int SchemaEpoch { get; }`、`public static IReadOnlyList<string> ArtifactHashes { get; }`。
