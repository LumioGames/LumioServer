---
status: pending
---

# 建立 mvp-host/ 构建根、SDK 与隔离校验脚本、缺席清单，并新增独立 dotnet CI job

把全部 .NET 构建根文件下沉到新顶层目录 `mvp-host/`，使 C# 树与未来 Rust workspace 物理隔离；同时把「隔离」从约定变成门禁（三条结构不变量 + CI job）。本卡不写任何 `.cs` / `.csproj`，落地后 `mvp-host/` 处于「零工程但可验证」状态。

设计出处：`docs/specs/2026-08-28-mvp-csharp-host-design.md` §3.2 / §3.4 / §4.2 / §7.1 / §7.2 / §7.4 / §7.5 / §7.6 / §15。

## 涉及范围

- `mvp-host/README.md`
- `mvp-host/absences.json`
- `mvp-host/global.json`
- `mvp-host/Directory.Build.props`
- `mvp-host/Directory.Build.targets`
- `mvp-host/Directory.Packages.props`
- `mvp-host/NuGet.config`
- `mvp-host/.editorconfig`
- `mvp-host/build.proj`
- `mvp-host/eng/verify-sdk.sh`
- `mvp-host/eng/verify-sdk.ps1`
- `mvp-host/eng/verify-isolation.sh`
- `mvp-host/eng/verify-isolation.ps1`
- `mvp-host/eng/verify-all.sh`
- `mvp-host/eng/verify-all.ps1`
- `mvp-host/eng/banned-public-api.txt`
- `.gitignore`
- `.gitattributes`
- `.github/workflows/repository-policy.yml`

## 验收标准

- [ ] **先失败证据（结构不变量先红后绿）**：先写 `mvp-host/eng/verify-isolation.sh`，然后在仓库根临时创建 `Directory.Build.props`（空 `<Project/>`）与 `modules/probe.csproj`（空 `<Project/>`），执行 `cd mvp-host && bash eng/verify-isolation.sh`，记录退出码 `34` 与输出中逐条列出的两个违规路径；删除这两个临时文件后重跑，记录 `MVP_HOST_ISOLATION_OK` 与退出码 0。两次完整输出写进交回物。
- [ ] `mvp-host/eng/verify-isolation.sh` 与 `.ps1` 各自实现设计 §3.4 的三条断言，任一违规打印 `MVP_HOST_ISOLATION_VIOLATION <path>` 逐条列出并以退出码 `34` 结束，全部通过时打印 `MVP_HOST_ISOLATION_OK` 并退出 0：① 仓库根不存在 `global.json`、`Directory.Build.props`、`Directory.Build.targets`、`Directory.Packages.props`、`NuGet.config`；② `modules/`、`crates/`、`tools/`、`benches/`、`contracts/`、`generated/`、`tests/` 七个仓根目录（存在时）下不存在 `*.csproj`、`*.cs`、`*.slnx`；③ `mvp-host/**` 下不存在 `*.rs` 与 `Cargo.toml`。脚本自行解析自身所在目录后再定位仓库根，不依赖调用方 cwd。
- [ ] `mvp-host/global.json` 的内容恰为 `{"sdk":{"version":"10.0.100","rollForward":"latestFeature","allowPrerelease":false}}`（可含缩进与换行，字段值逐字相同）；仓库根不新增 `global.json`。
- [ ] `mvp-host/eng/verify-sdk.sh` 与 `.ps1` 自解析脚本目录后 `cd` 到 `mvp-host/`，成功时打印单行 `SDK_OK sdk=<实际 SDK 版本> runtime=<实际 Microsoft.NETCore.App 版本>` 并退出 0；失配时打印 `SDK_MISMATCH expected=<期望> actual=<实际>` 并以非零退出码结束。失配条件恰为三选一：SDK 版本前缀不是 `10.0.`、**或** `Microsoft.NETCore.App` 版本前缀不是 `10.0.`、**或** runtime 的 `major.minor` 与 SDK 的 `major.minor` 不相等。**确切补丁号不得写进门禁**——把 runtime 号写死就是重犯设计 §7.1 点名的反面样板（`LumioClient/eng/verify-toolchain.sh` 硬 `grep -q '10.0.400'`）：任一台机器升一个 runtime 补丁、或 Windows 侧 runtime 号不同，`verify-sdk` 即红，而后续 13 张卡每条验收都以 `bash eng/verify-all.sh` 为前置。补丁号只作为**交回物里记录的观测值**。本机实跑 `bash eng/verify-sdk.sh` 的输出，连同 `dotnet --version` / `dotnet --list-sdks` / `dotnet --list-runtimes` 的原始输出一并写进交回物（本轮设计侧实测：SDK `10.0.400`、`Microsoft.NETCore.App 10.0.11`，二者 `major.minor` 均为 `10.0`）。
- [ ] `mvp-host/Directory.Build.props` 显式设置以下 13 个属性：`Nullable=enable`、`ImplicitUsings=disable`、`TreatWarningsAsErrors=true`、`EnableNETAnalyzers=true`、`AnalysisLevel=latest-recommended`、`EnforceCodeStyleInBuild=false`、`Deterministic=true`、`ManagePackageVersionsCentrally=true`、`CentralPackageTransitivePinningEnabled=true`、`RestorePackagesWithLockFile=true`、`DisableImplicitNuGetFallbackFolder=true`、`TargetFramework=net10.0`、`LangVersion=14.0`；并定义两个自定义开关属性 `MvpHostProductionProject`（默认 `true`）与 `MvpHostLayer`（无默认值，由每个 csproj 自行声明）。本文件同时负责 `Microsoft.CodeAnalysis.BannedApiAnalyzers` 的**全仓接线**（设计 §7.4 把归属定死在本文件，**不在卡 4**——分析器必须逐工程引用才生效，「在所有生产工程生效」只能写在 `Directory.Build.props`），三件事一次写全：① `PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers"`（**不带 `Version`**，`PrivateAssets=all`）；② `AdditionalFiles Include="$(MSBuildThisFileDirectory)eng/banned-public-api.txt"`；③ 为 `Lumio.Server.MvpHost.Platform` 工程开一个**单点例外**，允许其 `IWallClock` 实现文件使用 `System.DateTimeOffset`（工程级 `NoWarn` + 该实现文件内的单文件 pragma 双重收窄），`Platform` 之外的任何工程一律无例外——理由见设计 §6.0：没有这个全仓唯一墙钟出口，本仓产不出一条含合法 `timestamp` 的 logging-event。
- [ ] `mvp-host/Directory.Build.targets` 定义 `ValidateMvpHostBuildProfile` 目标，`BeforeTargets="PrepareForBuild"`，命中即 `<Error>` 硬失败，三条：① `MvpHostProductionProject=true` 的工程不得出现 `xunit`、`nunit`、`mstest`、`Microsoft.NET.Test.Sdk`、`coverlet`、`FsCheck`、`ArchUnitNET` 任一前缀的 `PackageReference`，也不得设 `IsTestProject=true`；② `MvpHostProductionProject=true` 的工程不得 `ProjectReference` 任何路径以 `.Tests.csproj` 或 `.TestKit.csproj` 结尾的工程；③ 所有工程的 `TargetFramework` 必须恰为 `net10.0`、`LangVersion` 必须恰为 `14.0`。
- [ ] `mvp-host/Directory.Packages.props` 一次性声明设计 §7.4 表中的全部 7 个包版本（`xunit.v3` 3.2.2、`xunit.runner.visualstudio` 3.1.5、`Microsoft.NET.Test.Sdk` 18.8.1、`TngTech.ArchUnitNET.xUnitV3` 0.13.3、`FsCheck` 3.3.4、`Microsoft.CodeAnalysis.BannedApiAnalyzers` 5.6.0、`System.Threading.Channels` 10.0.0），后续任何卡都不需要再改本文件。其中 `TngTech.ArchUnitNET.xUnitV3` 供测试工程做**方法调用依赖**级断言——这是设计 §4.3「断言机制纪律」要求的唯一手段（`System.Reflection` 只能看签名与元数据，看不到方法体内部、构造点与调用点）；**后续任何卡都不得新增分析包**。
- [ ] `mvp-host/NuGet.config` 以 `<clear />` 开头，只保留 `nuget.org` 一个源，并配置 `packageSourceMapping`、`auditMode=all`、`auditLevel=low`。
- [ ] `mvp-host/.editorconfig` 存在且**不含** `root = true`（避免将来管辖仓根的 `.rs`）；仓库根不新增 `.editorconfig`。
- [ ] `mvp-host/build.proj` 是 MSBuild 遍历工程，以 glob `src/**/*.csproj`、`tests/**/*.csproj`、`testkit/**/*.csproj` 收集 `ProjectReference`，**不含** `adapters/**`；零匹配时 `dotnet build build.proj` 仍退出 0。
- [ ] `mvp-host/eng/banned-public-api.txt` 恰含四条：`T:System.Net.Sockets.Socket`、`T:System.DateTime`、`T:System.DateTimeOffset`、`M:System.Threading.Thread.Sleep(System.Int32)`，每条带说明后缀。`Task.Delay` **不入表**——它只受「唯一落点在 `Platform` 内单个 internal 文件」的约束（卡 4 的工程内断言 + 评审项），两张卡的口径以设计 §7.4 为准；`T:System.DateTimeOffset` 的唯一例外是 `Platform` 的 `IWallClock` 实现文件，例外**不写在本文件里**，而是声明在 `mvp-host/Directory.Build.props`（见本卡该文件的验收条目）。
- [ ] `mvp-host/eng/verify-all.sh` 与 `.ps1` 自解析脚本目录后 `cd` 到 `mvp-host/`，按序执行：`eng/verify-isolation` → `eng/verify-sdk` → `dotnet restore build.proj --locked-mode` → 对 `src`/`tests`/`testkit` 下每个 `*.csproj` 执行 `dotnet format <proj> --verify-no-changes --no-restore` → `dotnet build build.proj -c Release --no-restore` → 对 `tests` 下每个不以 `.Integration.Tests.csproj` 结尾的 `*.csproj` 执行 `dotnet test <proj> -c Release --no-build`。全部成功时最后一行打印 `MVP_HOST_VERIFY_OK` 并退出 0；任一步失败打印 `MVP_HOST_VERIFY_FAIL <step>` 并非零退出。**零工程状态（本卡落地时）下空 glob 不算失败，同样输出 `MVP_HOST_VERIFY_OK`。**
- [ ] `cd /Users/cui/LumioGames/LumioServer/mvp-host && bash eng/verify-all.sh` 退出码 0，末行 `MVP_HOST_VERIFY_OK`，完整输出写进交回物。
- [ ] `mvp-host/absences.json` 是合法 JSON，顶层含 `"baselineId": "LGE-V1.4-2026-08-27"` 与数组 `absences`；数组**一次性写全**以下 19 条，每条含且仅含 `id` / `clause` / `source` / `reason` / `successor` 五个字段（`reason` 只能取 `载体已提供` / `阶段未到` / `决策门冻结` / `实现方为 P1` 之一；`source` 填该缺席条款的出处路径，必要时带行号，且该路径在仓库中真实存在）。下表逐条给出 `id`（`clause` 摘要；`reason`；`successor`）：`ABS-WORLDSLOT-NATIVE`（NativeReady 空实现穿越；阶段未到；`implement-world-slot-aggregate-epoch-admission-and-quota`）、`ABS-WORLDSLOT-DEFERRED-TRANSITIONS`（Resume / BeginSnapshot / SnapshotComplete / BeginReload / ReloadComplete / BeginMigrate / MigrationHandedOff 七条迁移的触发方缺席；阶段未到；`implement-world-slot-quiesce-migration-and-fault-adjudication`）、`ABS-SESSION-FAULTED-UNREACHABLE`（ServerConnectionSession 的 Faulted 建模但 MVP 期不可达；阶段未到；`implement-session-drain-kick-and-fault-isolation`）、`ABS-RELEASE-EXACTMATCH`（ExactRelease 的 Catalog 消费 / Manifest 校验 / Pool 成员健康；实现方为 P1；`implement-release-catalog-manifest-verification`）、`ABS-RELEASE-MEMBER-HEALTH`（本 Pool 成员状态与健康上报；实现方为 P1；`implement-release-local-member-state-health-and-reporting`）、`ABS-AUDIT-DURABLE-ACK`（Audit durable ack 与背压关闸完整语义；实现方为 P1；`implement-observability-audit-durable-pipeline`）、`ABS-FAILURE-BUNDLE`（Failure Bundle 装配与紧急通道；实现方为 P1；`implement-observability-failure-bundle-and-emergency-path`）、`ABS-PERSISTENCE-SNAPSHOT`（SnapshotCut 只记内存句柄，无 WAL / Checkpoint；实现方为 P1；`implement-persistence-recovery-checkpoint-and-migration-adapter`）、`ABS-MAINTENANCE-CONTROLPLANE`（外部控制面与维护编排缺席，只发 MaintenanceKick 信封；实现方为 P1；`implement-maintenance-command-state-deadline-and-idempotency`）、`ABS-ENVELOPE-POCO`（架构源未生成 C# ReplicationEnvelope POCO，本仓 MvpEnvelopeDocument 为临时替身；载体已提供；`consume-upstream-generated-contract-artifacts`）、`ABS-PERMISSION-VALIDATOR`（ADR-022 要求的生成式 Protocol/Permission Validator 在 C# 侧只有字段名表；载体已提供；`consume-upstream-generated-contract-artifacts`）、`ABS-WIRE-FRAGMENTATION`（transportPolicy.maxFragmentBytes 只作声明值，不实现分片重组；阶段未到；`implement-transport-registry-bounded-ingress-egress`）、`ABS-TRANSPORT-PROFILE-ID`（WebSocket 传输能力字符串是本仓私有 provisional 声明，无注册 Capability ID；决策门冻结；`implement-host-profile-resolution-and-capability-matching`）、`ABS-LENGTH-SEMANTICS`（Envelope length 语义无公共定义，出入站取非对称私有口径；决策门冻结；`needs-new-card`）、`ABS-AUTH-CREDENTIAL-ERRORCODE`（43 个已注册 ErrorCode 中无「凭据无效」语义码，通道认证失败不发 Envelope Error；决策门冻结；`needs-new-card`）、`ABS-CLIENT-UPLINK-COMMAND`（客户端上行 gameplay 命令的 wire 承载，A1-β；决策门冻结；`needs-new-card`）、`ABS-REPLICATION-STATE-PAYLOAD`（公共 `FullSnapshot` / `Delta` 的 typed body 无状态载荷字段，ADR-028 的 Alternatives 明文否决 free-form payload，本仓不自行补，MVP 期客户端观察不到世界内容；决策门冻结；`needs-new-card`）、`ABS-REPLICATION-MAPPING-SET`（`mappingSetHash` 指向 ADR-005 所述、由 LumioGame 拥有而 MVP 期不存在的公共映射集，出站填本仓单点常量 64 个 `0`（provisional），入站只校验 `hash256` 正则；阶段未到；`needs-new-card`）、`ABS-AUTH-CREDENTIAL-CARRIAGE`（通道认证凭据与 nonce 的线承载无公共定义（`docs/architecture/DECISIONS_PENDING.md` D-011），MVP 取 `Sec-WebSocket-Protocol` 子协议位序 `lumio.mvp.v0, <token>, <nonce>`；决策门冻结；`needs-new-card`）。**新增三条的 `source` 字段纪律**（设计 §15）：`Architecture.Tests` 的 `AbsencesManifestTest` 会校验 `source` 指向的路径在仓库中真实存在，因此前两条的 `source` 填 `contract-mirror/schemas/replication-envelope.schema.json`，第三条的 `source` 填 `contract-mirror/MIRROR.md`（其中记录来源仓与 commit），而把 D-011 的坐标写进该条的 `clause` 文本——**不得写一个仓内不存在的路径**。
- [ ] `mvp-host/README.md` 首屏写明四件事：本目录的范围与退场条件（Rust Dedicated Host 主线交付后整目录删除）、**每次 dotnet 调用必须先 `cd mvp-host`** 的硬规程与其理由（`global.json` 只按 cwd 向上查找）、`bash eng/verify-all.sh` 与 `bash eng/verify-isolation.sh` 两条入口命令及其成功哨兵行、以及本目录不得出现 `*.rs` / `Cargo.toml`。
- [ ] `.gitignore` 在原有 5 行之后追加 `[Bb]in/`、`[Oo]bj/`、`.vs/`、`*.user`、`*.suo`、`TestResults/`、`artifacts/`；原有 5 行（`.DS_Store`、`node_modules/`、`*.log`、`.env`、`.env.*`）逐字保留。
- [ ] `.gitattributes` 追加 `*.cs`、`*.csproj`、`*.props`、`*.targets`、`*.slnx`、`*.json` 的 `text eol=lf` 规则；**`*.md text eol=lf` 这一行必须逐字保留**（仓库门在 `grep -q '^\*\.md text eol=lf$'`）。
- [ ] `.github/workflows/repository-policy.yml` 在 `jobs:` 下新增一个与 `readme` 同级的 job `mvp-host`，且 `jobs.readme` 的任何一行、以及顶层 `name` / `on` / `permissions` 均未被修改（用 `git diff` 逐行核对并把 diff 写进交回物）。新 job 内容：`actions/checkout@v4` → 显式安装 .NET SDK（以 `mvp-host/global.json` 为准）→ 其余步骤全部带 `working-directory: mvp-host` 并依次执行 `bash eng/verify-isolation.sh` 与 `bash eng/verify-all.sh`。
- [ ] setup-dotnet 的 action 名与入参在 CI 上首次实测：把该 job 的实际 YAML 与一次真实运行的日志（含 `MVP_HOST_ISOLATION_OK` 与 `MVP_HOST_VERIFY_OK` 两行）写进交回物；若首选 action 入参不成立，交回物必须记录实际采用的写法与失败原因。
- [ ] 本卡未创建任何 `.cs` / `.csproj` / `.sln` / `.slnx` 文件：`find mvp-host -name '*.cs' -o -name '*.csproj' -o -name '*.sln' -o -name '*.slnx'` 无输出。
- [ ] 未占用 51 张 Rust 卡的独占文件：执行 `python3 -c "import json,subprocess;f=set();[f.update(t['files']) for t in json.load(open('docs/LumioServer_Framework_Implementation_Design_2026-08-27/manifests/task-index.json'))];c=set(subprocess.check_output(['git','status','--porcelain']).decode().split()[1::2]);print(sorted(f&c))"`，输出为 `[]`。
- [ ] 仓级收口门槛：`node .spec/tools/spec-lint.mjs` 输出 `spec-lint: OK` 退出码 0；`node --test .spec/tools/spec-lint.test.mjs` 汇总 `fail 0` 退出码 0。
- [ ] 未越界实现任何 `mvp-host/absences.json` 列出的条目。

## 依赖

无

## 接口

Consumes:

- 来自 `origin/main`（本轮实测 `637b464`）：`.github/workflows/repository-policy.yml` 的顶层 `jobs:` 映射下只有 `readme` 一个 key，其「Validate repository boundary documentation」步骤的 16 条断言**已由上游 `9fe0cd7` retarget 到 v1.4 并实测 16/16 通过**；本卡只新增同级 key `mvp-host`，不修改 `jobs.readme` 的任何一行。

Produces:

- 目录常量：C# 构建根固定为仓库根下的 `mvp-host/`；生产工程放 `mvp-host/src/<AssemblyName>/`，测试工程放 `mvp-host/tests/<AssemblyName>/`，测试库放 `mvp-host/testkit/<AssemblyName>/`；工程目录名与 `AssemblyName` 同名，`.csproj` 文件名 = 目录名 + `.csproj`。
- MSBuild 属性契约（每个 csproj 必须自行声明）：`<MvpHostLayer>N</MvpHostLayer>`（整数，值即设计 §4.1 的层号）；测试与测试库工程必须声明 `<MvpHostProductionProject>false</MvpHostProductionProject>`，生产工程不声明（默认 `true`）。
- 包引用契约：所有 `PackageReference` 一律**不带 `Version` 属性**（中央包版本管理已开启），版本只在 `mvp-host/Directory.Packages.props` 中声明，且该文件已含 7 个包的版本，后续卡不再修改它。
- 脚本契约：`bash mvp-host/eng/verify-all.sh` 成功末行 `MVP_HOST_VERIFY_OK` 退出码 0；`bash mvp-host/eng/verify-isolation.sh` 成功打印 `MVP_HOST_ISOLATION_OK` 退出码 0、违规退出码 `34`；`bash mvp-host/eng/verify-sdk.sh` 成功打印 `SDK_OK sdk=<v> runtime=<v>` 退出码 0。
- `mvp-host/absences.json` 的全量 19 条已由本卡写定，下游卡**只读校验、不追加**；下游卡的验收项「未越界实现任何 `mvp-host/absences.json` 列出的条目」以本文件为准。


---

## TD 卡面修订(2026-08-29,总调度)

本卡正文中的三处陈述已在 R-00270 重开修复中按已定裁决改掉,**卡面同步如下**:

1. **「43 个已注册 ErrorCode」→ 作废。** 实测 `StableErrorIds` 长度为 **53**。按已定裁决,**一切计数式论证改为「存在性 + 身份」断言**(BaselineId 相等 + SchemaId 在册 + 逐名核验),**不写任何计数** —— 计数会随 additive 增补必然腐烂,43→53 即为实例。结论(认证失败用 close 1008 不发 Envelope Error)**仍成立**,失效的只是计数式论证。
2. **「64 个 `0`」→ 作废**,见 [`implement-mvp-envelope-wire-and-fixture-gate.md`](implement-mvp-envelope-wire-and-fixture-gate.md) 的同日修订:ADR-045 明文否决 sentinel,实际值为 `a805f7c8…6d2ea7`。
3. **「七条迁移」的计数标签 → 去掉**(同第 1 条口径)。

### 附:一条被误判的缺口,以及它的真正根因

R-00270 的 QA 轮曾判「19 条 `absences.json` source 中 4 条指向不存在的 `contract-mirror/`」。**重开修复实测:那 4 条本来就成立,缺口 = 0。**

根因不是路径写错,而是:[`define-mvp-host-contracts-and-audit-surface.md:42`](define-mvp-host-contracts-and-audit-surface.md)(R-00274 卡面,自 `490fdb1` 起从未改过)**原文写定**「`contract-mirror/` 内路径按 `mvp-host/` 相对解析」,而 R-00271 正是把镜像落在 `mvp-host/contract-mirror/`。QA 按**仓库根**量,故量得缺失 —— **这条解析基准从未落进 `absences.json` 自身**。

**处置**:把基准补进 `absences.json` 顶层 `note`,**不改那 4 个 source 字符串**(改它们反而会同时违背 R-00270 与 R-00274 两张卡面的明文指令,并让 `MIRROR.md:121` 的引用失准)。

**沉淀**:**缺席 / 清单类文件必须把「路径解析基准」写进文件自身** —— 基准只写在另一张卡面上,等于把「怎么读这份文件」藏在了读者不会去看的地方。
