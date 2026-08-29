# mvp-host — MVP 期 C# Server Host

本目录是 MVP 期由 C# 承担的 Server Host 最小面：**WebSocket(WSS) transport、auth 存根、session、world-slot**。
它与未来的 Rust Dedicated Host workspace **物理隔离、互不阻塞**，整目录可删。

| 项 | 值 |
|---|---|
| 架构基线 BaselineId | `LGE-V1.4-2026-08-27` |
| 来源需求 | R-00260（RM-00006 / MS-00001，P0） |
| 设计真值 | [`../docs/specs/2026-08-28-mvp-csharp-host-design.md`](../docs/specs/2026-08-28-mvp-csharp-host-design.md) |
| **退场条件** | **Rust Dedicated Host 主线（51 张 Rust 卡）交付后，本目录整体删除。** |

## 范围

**本目录下不得出现 `*.rs` 与 `Cargo.toml`**（由 `eng/verify-isolation.sh` 的第三条不变量在门禁里强制）。

只做设计文档划定的子集。公共契约（Envelope 字段、Schema、Fixture、ErrorCode 词表）的唯一来源是
`LumioGameEngineArchitecture`——**本仓不存在任何一份「LumioServer 定义的协议格式文档」**，
只有 reader/writer，没有 definition。缺席条目逐条登记在 [`absences.json`](absences.json)（19 条，
四元组 `id` / `clause` / `source` / `reason` / `successor`），缺席**不得改变公共接口或状态机的形状**。

## 硬规程：每次 dotnet 调用必须先 `cd mvp-host`

`global.json` **只按当前工作目录向上查找，不看工程路径**。本轮实测：把不可满足的
`{"version":"9.9.999","rollForward":"disable"}` 放在子目录后，cwd 在仓库根时
`dotnet build <子目录>/x.csproj` **完全无视它并构建成功**；cwd 在该子目录时才失败。

因此 cwd 在仓库根时会**静默绕过 SDK pin**——绕过不报错，只是悄悄用了另一个 SDK。
所有验证脚本都自解析脚本目录后再 `cd`，不依赖调用方 cwd；手工调用 `dotnet` 时请自己先 `cd`。

## 入口命令

```bash
cd mvp-host && bash eng/verify-all.sh          # 一键验证，成功末行 MVP_HOST_VERIFY_OK
cd mvp-host && bash eng/verify-isolation.sh    # 隔离门禁，成功 MVP_HOST_ISOLATION_OK / 违规退出码 34
cd mvp-host && bash eng/verify-sdk.sh          # SDK 族校验，成功 SDK_OK sdk=<v> runtime=<v>
```

Windows 用同名 `.ps1`（`pwsh eng/verify-all.ps1`）。

`verify-all` 的步骤顺序：`verify-isolation` → `verify-sdk` → `dotnet restore build.proj --locked-mode`
→ 逐工程 `dotnet format --verify-no-changes --no-restore` → `dotnet build build.proj -c Release --no-restore`
→ 逐工程 `dotnet test -c Release --no-build`（**排除 `*.Integration.Tests`**——集成测试显式触发，
入口是 `bash eng/verify-integration.sh`，成功末行 `MVP_HOST_INTEGRATION_OK`）。

`verify-sdk` 的判据是**版本前缀 `10.0.` + SDK 与 runtime 的 `major.minor` 一致**，
**不锁补丁号**——补丁号只作为交回物里记录的观测值。把 runtime 号写死进门禁，
任一台机器升一个补丁即全线变红。

## 隔离是门禁，不是约定

`eng/verify-isolation.sh` 断言三条结构不变量，违规逐条打印 `MVP_HOST_ISOLATION_VIOLATION <path>` 并以退出码 `34` 结束：

1. 仓库根不存在 `global.json` / `Directory.Build.props` / `Directory.Build.targets` / `Directory.Packages.props` / `NuGet.config`；
2. `modules/` `crates/` `tools/` `benches/` `contracts/` `generated/` `tests/`（存在时）下不存在 `*.csproj` / `*.cs` / `*.slnx`；
3. **`mvp-host/**` 下不存在 `*.rs` 与 `Cargo.toml`。**

构建根文件下沉到本目录一级，是因为 MSBuild 从工程目录逐级向上查找、遇到第一个
`Directory.Build.props` 即停止：放在仓库根会让 `net10.0` / `LangVersion 14.0`
意外管辖未来 Rust 侧任意位置的测试夹具 csproj。`.editorconfig` 同理不放仓根、也不设 `root = true`。

## 工程布局约定

- 生产工程 `src/<AssemblyName>/`，测试工程 `tests/<AssemblyName>/`，测试库 `testkit/<AssemblyName>/`；
  工程目录名 = `AssemblyName`，`.csproj` 文件名 = 目录名 + `.csproj`。
- 每个 csproj **必须自行声明** `<MvpHostLayer>N</MvpHostLayer>`；测试与测试库工程必须声明
  `<MvpHostProductionProject>false</MvpHostProductionProject>`，生产工程不声明（默认 `true`）。
- 所有 `PackageReference` 一律**不带 `Version`**——中央包版本管理已开启，版本只在
  [`Directory.Packages.props`](Directory.Packages.props) 声明（已含 7 个包，后续卡不再修改它）。
- `build.proj` 的 glob **不含 `adapters/`**：Runtime 类型全部关在唯一一个不进构建图的
  Adapter 工程里，「Adapter 缺席仍全绿」因此是机器可判断言。

## 禁用面：`eng/BannedSymbols.banned-public-api.txt` 的文件名不可改

`Microsoft.CodeAnalysis.BannedApiAnalyzers` **只读取文件名为 `BannedSymbols.txt` 或 `BannedSymbols.*.txt`
的 AdditionalFile，其余名字被静默忽略**——不报错、不警告，禁令直接空转。
本轮实测：文件名为 `banned-public-api.txt` 时，生产工程内写 `System.DateTimeOffset.UtcNow`
构建全绿、零 `RS0030`；仅改名为 `BannedSymbols.banned-public-api.txt` 即报 `RS0030`。
分析器没有可改该文件名的 MSBuild 开关，`Link` 元数据也改不了 `AdditionalText.Path`。

四条禁令：`System.Net.Sockets.Socket`、`System.DateTime`、`System.DateTimeOffset`、
`Thread.Sleep(Int32)`。唯一例外是 `Lumio.Server.MvpHost.Platform` 的 `IWallClock` 实现
（全仓唯一墙钟出口），以 `Directory.Build.props` 的工程级 `NoWarn` + 该实现文件内的单文件 pragma 双重收窄。
