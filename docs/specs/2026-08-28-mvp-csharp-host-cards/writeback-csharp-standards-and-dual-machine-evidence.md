---
status: pending
---

# 在 code-style.md 与 testing.md 增量追加 C# 小节，并回填 Windows 侧 SDK 一手证据

引入 C# 后两份标准文档的现状表述已失真：`code-style.md` 写「当前仓库尚未提交 Cargo 工程」、`testing.md` 写「当前仓库尚未提交 Server 实现工程」。本卡把口径改为「C# MVP 宿主已提交 / Rust workspace 未提交」两条语言域并存，并补齐 C# 侧的 formatter / analyzer / SDK pin / cd 硬规程 / 验证命令族 / 生成物纪律。同时回填「工程基线版本口径双机可满足」中缺失的 Windows 一手证据。

**这两个文件与 51 张 Rust 卡中 wave-0 的 `establish-cargo-workspace-and-rust-standards` 文件集重叠，必须串行，且只增量追加、不改写另一语言的小节。**

设计出处：`docs/specs/2026-08-28-mvp-csharp-host-design.md` §3.3 的唯一真实文件冲突段、§7.1 的待核层、§7.3 的 cwd 硬规程、§7.5 的验证命令、§11 的 G14。

## 涉及范围

- `.spec/knowledge/standards/code-style.md`
- `.spec/knowledge/standards/testing.md`

## 验收标准

- [ ] **先失败证据**：改动前执行 `grep -c "^## C#（MVP 宿主）" .spec/knowledge/standards/code-style.md .spec/knowledge/standards/testing.md`，两条均为 `0`（证明 C# 语言域在两份标准里尚无落点，而 `mvp-host/` 已落地）；改动后重跑，两条均为 `1`。四段输出写进交回物。**注意本卡前提已随上游变化**：`origin/main` 的 `d4e03d4` 已把 Rust 工具链与 lint 纪律写进这两份文档，并删除了原「当前仓库尚未提交 Cargo 工程 / Server 实现工程」两句（实测 `grep -c` 均为 0）。因此本卡**不再需要改写任何现状表述**，只做纯追加。
- [ ] `code-style.md` 在既有「Rust 工具链与 lint 纪律」「骨架阶段入口」小节**之后**追加一个并列的 `## C#（MVP 宿主）` 小节，**既有 Rust 表述一个字符都不改写、不删除**（上游 `d4e03d4` 已完成 Rust 侧现状表述，本卡纯追加）。新小节至少写明四件事：① 本仓存在 C#（`mvp-host/`，MVP 期 Server Host）与 Rust（未来 Dedicated Host 主线）两套语言域及各自适用范围与 `mvp-host/` 的退场条件；② C# 侧的 formatter / analyzer 口径——逐工程 `dotnet format <proj> --verify-no-changes --no-restore`、`EnableNETAnalyzers=true` + `AnalysisLevel=latest-recommended` + `TreatWarningsAsErrors=true`，以及 `mvp-host/eng/banned-public-api.txt` 的四条禁用面（`System.Net.Sockets.Socket`、`System.DateTime`、`System.DateTimeOffset`、`Thread.Sleep`），以及**唯一例外**：`Platform` 的 `IWallClock` 实现允许使用 `System.DateTimeOffset`（全仓唯一墙钟出口，用于产出 `logging-event.timestamp`），该例外声明在 `mvp-host/Directory.Build.props`，`Platform` 之外无例外；再有一条断言机制纪律——「不存在某调用」类断言统一用 `TngTech.ArchUnitNET` 的方法调用依赖断言，`System.Reflection` 只用于签名与元数据级断言，**不使用 IL 字节扫描**，也不引入任何未冻结的分析包；③ C# 命名与文件布局约定——类型与成员 PascalCase、私有字段 `_camelCase`、工程目录名等于 `AssemblyName`、生产在 `mvp-host/src/`、测试在 `mvp-host/tests/`、测试库在 `mvp-host/testkit/`；④ 生成物纪律——架构源 `packages/csharp` 的 6 个 artifact 与 `mvp-host/contract-mirror/` 都是只读消费物，不得手改、只能经 `eng/generate-contracts.sh` 与 `eng/sync-contract-mirror.sh` 更新，本仓不维护第二套手写协议定义。
- [ ] `testing.md` 在既有 Rust 命令族表述**之后**追加一个并列的 `## C#（MVP 宿主）` 小节，**既有 Rust 与通用表述一个字符都不改写、不删除**（上游 `d4e03d4` 已完成 Rust 侧，本卡纯追加）。新小节至少写明三件事：① C# 验证命令族——`cd mvp-host` 后依次 `bash eng/verify-isolation.sh`、`bash eng/verify-sdk.sh`、`bash eng/verify-contract-mirror.sh`、`bash eng/verify-generated-contracts.sh`、`dotnet restore build.proj --locked-mode`、逐工程 `dotnet format --verify-no-changes --no-restore`、`dotnet build build.proj -c Release --no-restore`、逐工程 `dotnet test -c Release --no-build`（排除 `*.Integration.Tests`），一键入口 `bash eng/verify-all.sh`（成功末行 `MVP_HOST_VERIFY_OK`），集成测试显式入口 `bash eng/verify-integration.sh`（成功末行 `MVP_HOST_INTEGRATION_OK`）；其中 `eng/verify-sdk.sh` 的 runtime 判据是**前缀 + major.minor 一致**（SDK 与 `Microsoft.NETCore.App` 的版本前缀都必须是 `10.0.`，且二者 `major.minor` 相等），**不锁补丁号**——补丁号只作为交回物中的观测值；② **cd 硬规程**——每一次 `dotnet` 调用都必须先 `cd mvp-host`，理由是 `global.json` 只按当前工作目录向上查找、不看工程路径，cwd 在仓根时会静默绕过 SDK pin；③ 两条验证链并存时的收口门槛合并口径——仓级两条 `node` 命令恒为门槛，C# 改动另加 `bash mvp-host/eng/verify-all.sh`，Rust workspace 落地后再由 Rust 侧补充其命令族，两条链互不替代。
- [ ] 两份文档各自新增的小节标题都是 `## C#（MVP 宿主）`，并在小节首句写明书面约定：「本小节只描述 C# 语言域；后落地的另一语言只增量追加自己的小节，不得覆盖或改写本小节」。同一句约定也覆盖 Rust 小节（由后落地方遵守）。
- [ ] 两份文档的 frontmatter（`name` / `description` / `metadata.type` / `metadata.status`）一个字符都不改；`description` 保持单行明文且 ≤ 120 字符（spec-lint 第 2 项会校验）。
- [ ] 新增内容中的每个相对链接都指向真实存在的文件（spec-lint 第 4 项会校验 `.spec` 下全部 `.md` 的相对链接）。
- [ ] **Windows 侧一手证据回填**：在交回物中附上 Windows 机器上对本仓 `mvp-host/global.json`（`{"sdk":{"version":"10.0.100","rollForward":"latestFeature","allowPrerelease":false}}`）执行 `dotnet --info` 与 `dotnet --list-sdks` 的**原始输出**，以及在 `mvp-host/` 下执行 `bash eng/verify-sdk.sh`（或 `pwsh eng/verify-sdk.ps1`）得到的 `SDK_OK sdk=<v> runtime=<v>` 行与退出码。**在这份原始输出到手之前，交回物与文档中不得出现「双机可满足已验证」的表述**；若 Windows 侧此时仍不可达，本条以「Windows 侧证据未到，结论仍停在推论层」明确记入交回物并把本卡置为未完成，不得以推论顶替。
- [ ] 与 51 张 Rust 卡的关系写进交回物：本卡的两个文件属 wave-0 的 `establish-cargo-workspace-and-rust-standards` 的 15 个独占文件之二，该 Rust 卡**已于 `origin/main` 的 `d4e03d4` 落地**（不再是 pending）；本卡与它**不得并行**，且本卡落地后该 Rust 卡必须只增量追加 Rust 小节、不得覆盖 C# 小节。核对命令：`python3 -c "import json;d=json.load(open('docs/LumioServer_Framework_Implementation_Design_2026-08-27/manifests/task-index.json'));t=[x for x in d if x['slug']=='establish-cargo-workspace-and-rust-standards'][0];print(t['status'],t['dependencies'],[f for f in t['files'] if f.endswith('.md')])"`，输出必须显示这两个 `.md` 在其 `files` 内。
- [ ] 仓级收口门槛：`node .spec/tools/spec-lint.mjs` → `spec-lint: OK` 退出码 0；`node --test .spec/tools/spec-lint.test.mjs` → `fail 0` 退出码 0。
- [ ] 复跑 `.github/workflows/repository-policy.yml` 的「Validate repository boundary documentation」步骤全部 16 条断言，16/16 退出码 0（实测依据：`sed -n '24,39p' .github/workflows/repository-policy.yml | wc -l` → `16`；该步骤的 16 条断言已由上游 `9fe0cd7` retarget 到 v1.4，本轮在 `637b464` 上实测 16/16 通过）。
- [ ] `git status --porcelain` 只列出 `.spec/knowledge/standards/code-style.md` 与 `.spec/knowledge/standards/testing.md`。
- [ ] **Rust 段落零改动的 diff 证据**（架构仓总调度 2026-08-28 放行条件④）：交付时附 `git diff -- .spec/knowledge/standards/code-style.md .spec/knowledge/standards/testing.md` 的**完整输出**，并逐段说明所有 `-` 行只可能出现在被改写的那两句现状表述（`当前仓库尚未提交 Cargo 工程` / `当前仓库尚未提交 Server 实现工程`）上；除这两句外，Rust 与通用段落必须**零删除行、零修改行**，新增内容全部是 `+` 行且集中在新的 `## C#（MVP 宿主）` 小节内。若 diff 显示任何其他 `-` 行，本卡即为未完成，不得以「等价改写」辩护。
- [ ] 本卡不改动 `mvp-host/` 下任何文件，也不改动 `mvp-host/absences.json`。

## 依赖

`scaffold-mvp-host-build-baseline`, `vendor-architecture-contracts-and-fixture-mirror`, `verify-a1-alpha-cross-process-replication-loop`

## 接口

Consumes:

- 来自 `scaffold-mvp-host-build-baseline`：`mvp-host/global.json` 的确切内容 `{"sdk":{"version":"10.0.100","rollForward":"latestFeature","allowPrerelease":false}}`；脚本契约 `bash eng/verify-all.sh` 成功末行 `MVP_HOST_VERIFY_OK`、`bash eng/verify-isolation.sh` 成功打印 `MVP_HOST_ISOLATION_OK`、`bash eng/verify-sdk.sh` 成功打印 `SDK_OK sdk=<v> runtime=<v>`（其失配判据是前缀与 `major.minor` 一致，**不是补丁号**）；`mvp-host/eng/banned-public-api.txt` 的四条禁用条目，以及 `mvp-host/Directory.Build.props` 中完成的 BannedApiAnalyzers 全仓接线与 `Platform` 的 `IWallClock` 单点例外；工程目录布局（`src` / `tests` / `testkit`）。
- 来自 `origin/main`（`d4e03d4` 与 `9fe0cd7`）：`.spec/knowledge/standards/repository-architecture.md` 与 `.github/workflows/repository-policy.yml` 的 BaselineId 口径均已是 `LGE-V1.4-2026-08-27`——本卡新增内容沿用该值，不复述 V1.2。
- 来自 `vendor-architecture-contracts-and-fixture-mirror`（引用其命令名，不引用其代码）：`bash eng/verify-contract-mirror.sh`（漂移退出码 33）、`bash eng/verify-generated-contracts.sh`（漂移退出码 32）。
- 来自 `verify-a1-alpha-cross-process-replication-loop`（引用其命令名，不引用其代码）：`bash eng/verify-integration.sh`（成功末行 `MVP_HOST_INTEGRATION_OK`）。

Produces:

- `.spec/knowledge/standards/code-style.md` 与 `.spec/knowledge/standards/testing.md` 各含一个 `## C#（MVP 宿主）` 小节，以及首句的书面约定「后落地的另一语言只增量追加自己的小节，不得覆盖或改写本小节」——这是 51 卡 wave-0 的 `establish-cargo-workspace-and-rust-standards` 落地时必须遵守的文本契约。
