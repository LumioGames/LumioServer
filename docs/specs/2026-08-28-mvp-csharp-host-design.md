# MVP C# 宿主实现级设计（LumioServer）

| 项 | 值 |
|---|---|
| 日期 | 2026-08-28 |
| 架构基线 BaselineId | `LGE-V1.4-2026-08-27` |
| 来源需求 | R-00260（RM-00006 / MS-00001，P0） |
| 交付性质 | **实现级设计 + 待拆任务卡**；本文档不含任何实现代码，本轮未创建任何 `.cs` / `.csproj` / `.sln` / `.slnx` 文件 |
| 本文档路径 | `docs/specs/2026-08-28-mvp-csharp-host-design.md` |
| 退场条件 | Rust Dedicated Host 主线（51 张 Rust 卡）交付后，`mvp-host/` 整目录删除 |

**权威顺序（冲突时自上而下）：**

1. `LumioGameEngineArchitecture` 的 `schemas/`、`ids/index.json`、`fixtures/`、`tools/lumio_contract.py`、`docs/architecture/LumioGameEngine_Architecture_v1.4.md`、`.spec/decisions/ADR-*`、`docs/architecture/DECISIONS_PENDING.md` —— 公共契约唯一来源。
2. 本仓 `.spec/rules/system.md`（硬红线）、`.spec/AGENTS.md`（调度与收口门槛）、`.spec/tasks/README.md`（任务卡格式单一权威）。
3. 本仓 `modules/{transport,auth,session,world-slot}/README.md` —— 四模块语义单一真值（Rust 实现的边界契约；MVP C# 只做其覆盖子集）。
4. 本文档 —— 只做「怎么在 C# 里实现上述子集」，不新增任何语义条款。

**引用记号约定（全文单一约定）：** 裸写的 `§N` 一律指**本文档**的小节；引用外部文档的小节必须带前缀，写成「架构源 v1.4 §N」「MVP 计划 §N」。凡本文档出现无前缀的 `§N`，读者可直接在本文件内定位。

> **术语**：本文档正文中文；代码标识符、协议字段名、枚举值、命令一律保留英文原拼写（依据 `.spec/knowledge/standards/code-style.md`）。

---

## 1. 摘要与三条主张

本设计交付 MVP 期由 C# 承担的 Server Host 最小面：**WebSocket(WSS) transport、auth 存根、session、world-slot**，落在仓库新顶层目录 `mvp-host/`，与未来 Rust workspace 物理隔离、互不阻塞。

三条主张，互相支撑，缺一条另两条即退化：

**主张一 · 契约保真：wire 不归本仓。** MVP 的 WebSocket 消息就是 `schemas/replication-envelope.schema.json` 的一个 UTF-8 JSON 实例。本仓不存在任何一份「LumioServer 定义的协议格式文档」。C# 侧只有 reader/writer（`MvpEnvelopeDocument`），没有 definition：header 访问器与 schema `required` 数组机器断言相等，`body` 原样以 `JsonObject` 透出——**刻意不生成任何 per-messageType 的 typed body POCO**，因为那是最容易在半年内硬化成事实标准的东西。类型名刻意让出 `ReplicationEnvelope`（该名被 `Lumio.Gen.LanguageBinding` 预留），并配一条**自过期守卫**测试：生成物落地那天测试自动变红，红灯本身就是删除本仓 DTO 的指令。

**主张二 · 物理隔离靠机制而非纪律。** 全部 .NET 构建根文件下沉 `mvp-host/`，依据是两条实测的 MSBuild/SDK 查找语义（§4.3），并由 CI 结构不变量断言守住。Runtime 类型全部关进唯一一个 `Runtime.Adapter` 工程且**不进构建图**，由架构测试断言「构建图中不存在任何指向 `Lumio.GameRuntime.*` 的边」——「Adapter 缺席仍全绿」是机器可判断言，不是口头承诺。结果：LumioGameRuntime（全局最长杆，11 模块仅 observability 有实现）与 LumioClient（Wave7 未启动）都不阻塞本仓首批卡跑绿。

**主张三 · A1 退出条件驱动，且被阻塞的那一半必须显式化。** 冻结的 8 个 `messageType` 中**没有任何一个能承载 client→server 的 gameplay 输入**（客户端可合法发出的只有 `Handshake` / `BaselineAck` / `DeltaAck` / `ResyncRequest`，全是复制链路控制消息）。因此 A1 的字面退出条件被拆成两半：**A1-α（复制与重连全环，本批次可自动化交付）** 与 **A1-β（客户端上行 gameplay 命令，BLOCKED，回架构源走 ADR）**。这条拆分与它的裁决理由见 §5.6，是本设计最重要的一处正面表态。

---

## 2. 范围：什么必须真实、什么可以存根

| 必须真实（不打折） | 可最小化到存根（形状保留、语义缺席、`absences.json` 登记） |
|---|---|
| WSS/WS 跨进程传输、连接代次、断线检测 | auth verifier（injected exact-byte，落在 WSS 通道认证层） |
| Envelope 结构层 + 语义层双校验 | release 匹配（与固定配置精确比对，不消费 Catalog/Manifest） |
| 架构源 v1.4 §7.1 复制状态机全链路 | persistence（无 WAL / Checkpoint，`SnapshotCut` 只记内存句柄） |
| 同连接内 Resync 与跨连接重连**两条不同路径** | maintenance / control-plane（只发 `MaintenanceKick` 信封，无外部控制面） |
| Admission saga 八步形状 + 恰好一次补偿 | Native 加载（PureHeadless / NoNative，空实现穿越 `NativeReady`） |
| WorldSlotHost 聚合根：epoch / Gate / Quiesce 原子序列 | Runtime（本仓自有 `IWorldSimulationPort` + 参考存根） |
| 有界队列七项合同与背压 | observability durable ack / Failure Bundle（只有最小 Audit 写入面 + 背压关闸） |

**缺席登记纪律（全批次强制）**：每一条缺席写成四元组落 `mvp-host/absences.json` —— ① 缺席的语义条款 ② 出处路径（必要时行号）③ 缺席理由类别（`载体已提供` / `阶段未到` / `决策门冻结` / `实现方为 P1`）④ 承接它的既有 Rust 卡 slug（无对应卡则标 `needs-new-card`）。**缺席不得改变公共接口或状态机的形状**：接口保留、返回稳定拒绝或固定成功，不删步骤、不删状态。「简化」「暂时」不是合法的缺席理由。每张 MVP 实现卡的验收标准含一条「未越界实现任何 `absences.json` 列出的条目」。

---

## 3. 目录与工程边界

### 3.1 顶层目录名：`mvp-host/`

三条理由：
1. **零交集且可机器判定**。本轮实测对 `docs/LumioServer_Framework_Implementation_Design_2026-08-27/manifests/task-index.json` 聚合：51 张卡 349 个唯一文件，顶层前缀集合为 `{modules(282), tests(22), crates(8), tools(7), generated(6), .spec(5), benches(5), contracts(3), .cargo(1), manifests(1), .github(1), docs(1)}` 加 7 个根文件 `Cargo.toml / Cargo.lock / rust-toolchain.toml / rustfmt.toml / clippy.toml / deny.toml / nextest.toml`；`.cs`/`.csproj`/`.sln`/`.slnx` 结果集为**空**。候选名前缀求交，`mvp-host` 返回 FREE。
2. **名字自带退场语义**。`csharp/` 会暗示「本仓所有 C# 永远住这」，而 Rust 期 `coreclr-host` 加载的托管侧将来也可能是 C#，那个名字会被抢占；`src/` / `eng/` / `app/` 属仓级泛用名，未来 Rust 侧或仓级工具可能要用。
3. **内部子目录刻意避开被占用名**：用 `contract-mirror/` 而非 `contracts/`（51 卡占 3 个文件）、`testkit/` 与 `mvp-host/tests/` 而非仓根 `tests/`（占 22 个）、`mvp-host/eng/` 而非仓根 `eng/`。嵌套后本不会冲突，但避免 grep 类守卫误伤。

### 3.2 目录树

```
mvp-host/                                     ★ C# 构建根 = 隔离边界；整目录可删
  README.md                                   目录宪章：范围、退场条件、cd 硬规程、验证命令
  absences.json                               缺席四元组（机器可读）
  global.json                                 {"sdk":{"version":"10.0.100","rollForward":"latestFeature","allowPrerelease":false}}
  Directory.Build.props                       net10.0 / LangVersion 14.0 / 分析器 / 中央包管理 / 锁文件
  Directory.Build.targets                     ValidateMvpHostBuildProfile（BeforeTargets=PrepareForBuild，硬失败）
  Directory.Packages.props                    中央包版本 + 传递固定
  NuGet.config                                <clear/> + nuget.org + packageSourceMapping + audit
  .editorconfig                               下沉；不设 root=true（避免将来管辖 .rs）
  build.proj                                  MSBuild 遍历工程：glob src/**/*.csproj;tests/**/*.csproj;testkit/**/*.csproj
  eng/
    verify-sdk.sh / verify-sdk.ps1            SDK 族 + runtime 双口径；失配非零退出 SDK_MISMATCH
    verify-all.sh / verify-all.ps1            自解析脚本目录后 cd，再依次跑全部 verify + build + test
    generate-contracts.sh / .ps1              从 $LUMIO_ARCHITECTURE_ROOT 生成/拷贝 6 个 C# artifact + 写 manifest
    verify-generated-contracts.sh / .ps1      重新生成到临时目录比对；漂移退出码 32
    sync-contract-mirror.sh / .ps1            同步 schema + fixture 镜像并重写哈希清单
    verify-contract-mirror.sh / .ps1          重算 sha256 比对；漂移退出码 33
    contract-mirror.sha256
    banned-public-api.txt                     Socket / DateTime / Thread.Sleep / 凭据序列化面
  contract-mirror/                            架构源只读镜像（字节拷贝 + 哈希锁）；不得手改
    MIRROR.md                                 来源仓、来源 commit、BaselineId、同步命令、退场条件
                                              （本目录下不放 .csproj，也不放 Directory.Build.props——理由见 §4）
    schemas/replication-envelope.schema.json
    schemas/common.schema.json
    schemas/protocol-permission-gate.schema.json
    schemas/logging-event.schema.json
    fixtures/valid/replication-{handshake,full-snapshot,baseline-ack,delta,
                    delta-ack,resync,maintenance-kick,error}.json                  (8)
    fixtures/valid/protocol-permission-gate-accept.json                            (1)
    fixtures/valid/state-machine-world-slot-host.json                              (1)
    fixtures/valid/logging-auth-reject-audit.json                                  (1)
    fixtures/invalid/replication-{gap-without-resync,missing-snapshot-identity,
                    unregistered-message-type,integrity-value-mismatch}.json       (4)
    fixtures/invalid/protocol-permission-gate-stale-generation.json                (1)
  src/
    Lumio.Server.MvpHost.GeneratedContracts/  架构源 6 个 C# artifact 的 .cs 只读源码拷贝（Generated/ 禁手改，
                                              含目录级 .editorconfig 标 generated_code）+ manifest
    Lumio.Server.MvpHost.Wire/                MvpEnvelopeDocument + 双层校验 + permission gate 判定
    Lumio.Server.MvpHost.Platform/            单调时钟 / Timer 类型化投递 / 有界端口 / 具名线程监督 / 取消树
    Lumio.Server.MvpHost.HostContracts/       跨模块唯一契约面：ids·epochs·typed commands/events·端口·IWorldSimulationPort
    Lumio.Server.MvpHost.Observability/       Audit / Diagnostic 最小写入面（logging-event 形状）
    Lumio.Server.MvpHost.Transport/           载体无关核心：注册表·代次·校验闸·有界队列·限流·故障装饰器
    Lumio.Server.MvpHost.Transport.WebSocket/ IByteCarrier 的 WSS 实现（FrameworkReference AspNetCore.App）
    Lumio.Server.MvpHost.Auth/                token 存根 verifier·防重放·不可变 grant·permission gate 执行体
    Lumio.Server.MvpHost.WorldSlot/           聚合根·epoch·Gate·Quiesce·owner thread·tick 链·FaultAdjudicator
    Lumio.Server.MvpHost.Session/             ServerConnectionSession·admission saga·重连窗口·Drain/Kick·复制编排
    Lumio.Server.MvpHost.Simulation.Reference/ IWorldSimulationPort 参考存根（不透明 KV + 单调 revision）
    Lumio.Server.MvpHost.App/                 可执行组装根 lumio-mvp-host（显式 new，无 DI 容器）
    Lumio.Server.MvpHost.SmokeClient/         可执行自带冒烟客户端（与 Bot 同 wire）
  testkit/
    Lumio.Server.MvpHost.TestKit/             测试专用库：虚拟时钟·fixture 装载·内存 IByteCarrier·断言
  tests/
    Lumio.Server.MvpHost.GeneratedContracts.Tests/
    Lumio.Server.MvpHost.Wire.Tests/
    Lumio.Server.MvpHost.Platform.Tests/
    Lumio.Server.MvpHost.Transport.Tests/
    Lumio.Server.MvpHost.Transport.WebSocket.Tests/
    Lumio.Server.MvpHost.Auth.Tests/
    Lumio.Server.MvpHost.WorldSlot.Tests/
    Lumio.Server.MvpHost.Session.Tests/
    Lumio.Server.MvpHost.App.Tests/           进程级 CLI 契约与组装图断言（子进程启动，无外部服务）
    Lumio.Server.MvpHost.Architecture.Tests/  依赖图·命名·禁用面·队列登记·状态机表交叉校验·自过期守卫
    Lumio.Server.MvpHost.Integration.Tests/   A1-α 跨进程验收
  adapters/
    Lumio.Server.MvpHost.Runtime.Adapter/     ★ 全仓唯一引用 Lumio.GameRuntime.* 的工程；本轮不建、不进 build.proj
```

### 3.3 mvp-host/ 之外被本批次触碰的文件（逐条与 349 集合对照）

| 文件 | 在 349 集内？ | 处置 |
|---|---|---|
| `docs/specs/2026-08-28-mvp-csharp-host-design.md` | **否**（`docs/` 下唯一被占是 `docs/specs/2026-08-27-foundation-measurement-report.md`） | 本文档 |
| `.gitignore` | **否** | 追加 `[Bb]in/ [Oo]bj/ .vs/ *.user *.suo TestResults/ artifacts/`（现文只有 5 行，无任何 .NET 产物条目） |
| `.gitattributes` | **否** | 追加 `*.cs *.csproj *.props *.targets *.slnx *.json` 的 `text eol=lf`；**必须原样保留 `*.md text eol=lf` 那一行**（仓库门在 `grep -q '^\*\.md text eol=lf$'`） |
| `.github/workflows/repository-policy.yml` | **否**（`.github/` 下唯一被占是 `rust-foundation.yml`，归 wave 13 的 `add-repository-dag-queue-source-and-license-gates`） | 卡 1 修 baseline grep；卡 2 新增独立 dotnet job |
| `docs/specs/2026-08-28-mvp-csharp-host-cards/**` | **否**（51 卡本体在 `docs/LumioServer_Framework_Implementation_Design_2026-08-27/.spec/tasks/`，与本目录及仓根 `.spec/tasks/` 都是不同路径） | 首批实现卡草案（13 张 `<slug>.md` + 索引 `README.md`）；开工时由主 loop 按 wave 复制进仓根 `.spec/tasks/<slug>.md` |
| `.spec/knowledge/standards/code-style.md` | **★ 是** | 见下 |
| `.spec/knowledge/standards/testing.md` | **★ 是** | 见下 |

**唯一真实文件冲突及其处置**：`code-style.md` 与 `testing.md` 归 wave 0 的 Rust 卡 `establish-cargo-workspace-and-rust-standards`。**本轮实测该 Rust 卡已落地** —— `origin/main` 的 `d4e03d4`（`feat(workspace): managed-host 合同镜像、xtask 门禁工具与 process 模块脚手架`）已在这两份文档写入 Rust 工具链与 lint 纪律小节，并删除了原「当前仓库尚未提交 Cargo 工程 / Server 实现工程」两句现状表述（实测 `grep -c` 均为 0）。因此冲突形态由「两卡都要改写同一句现状表述」降级为「C# 侧纯追加一个并列小节」：卡 14 只在既有 Rust 小节之后追加 `## C#（MVP 宿主）`，**不改写任何 Rust 段落**，并附 diff 证明零删除行。此外 `Cargo.toml` / `crates/` / `tools/xtask/` / `generated/` / `contracts/` / `.spec/guards/` / `modules/process/` 等 Rust 路径**现已存在于 main**，本批次全部产物落 `mvp-host/**`，与之仍是前缀级零交集。

### 3.4 构建根文件为什么放 `mvp-host/` 一级（两条实测语义 + 反向保险）

- **`Directory.Build.props` 下沉可 100% 遮蔽仓根。** MSBuild 从**工程目录**逐级向上查找，遇到第一个 `Directory.Build.props` 即停止、不再上溯（实测两组对照：嵌套那份存在时只见 `NestedPropsSeen=true` / `RootPropsSeen=""` / `TargetFramework=net10.0`；改名后才见 `RootPropsSeen=true`）。反向更重要：**根 props 会作用于仓内任意位置的 `.csproj`**，若把 C# 基线放仓根，未来 Rust 侧任何测试夹具 csproj 都会被意外套用 net10.0 / LangVersion 14.0。下沉根除这个风险。
- **`global.json` 只按 cwd 向上查找，不看工程路径。** 实测：把不可满足的 `{"version":"9.9.999","rollForward":"disable"}` 放在子目录后，cwd 在仓根时 `dotnet build <子目录>/x.csproj` **完全无视它并构建成功**；cwd 在该子目录时才失败。因此下沉可行，但衍生出 §6.3 的 cd 硬规程。
- **文件名零碰撞。** Rust 根条目 `{Cargo.toml, Cargo.lock, rust-toolchain.toml, rustfmt.toml, clippy.toml, deny.toml, nextest.toml, .cargo/config.toml}` 与 C# 根条目 `{global.json, Directory.Build.props, Directory.Build.targets, Directory.Packages.props, NuGet.config, build.proj, packages.lock.json}` 交集为空；两者又分居仓根与 `mvp-host/`。
- **`.editorconfig` 刻意不放仓根**：LumioClient 与 LumioGameRuntime 的 `.editorconfig` 首行都是 `root = true`，放仓根会把将来的 `.rs` 一并管辖。
- **反向保险（CI 结构不变量，卡 2 落地）**：① 仓根不得出现 `global.json` / `Directory.Build.*` / `NuGet.config`；② `modules|crates|tools|benches|contracts|generated|tests` 下不得出现 `*.csproj` / `*.cs` / `*.slnx`；③ `mvp-host/**` 下不得出现 `*.rs` / `Cargo.toml`。把隔离从约定变成门禁。

---

## 4. 项目图

全部工程单目标 `net10.0` / `LangVersion 14.0`，**无例外**。

**★ 契约生成物的引入方式定死为「源码拷贝」，不是工程引用。** 架构源 `packages/csharp/` 的 6 个 artifact 各是一个独立的 `net8.0` `.csproj`；把它们作为工程引入，会被 `Directory.Build.targets` 的 `ValidateMvpHostBuildProfile`（TFM 必须恰为 `net10.0`）硬失败——本轮实测：在子目录放一个空壳 `Directory.Build.props` **拦不住**父级的 `Directory.Build.targets`（MSBuild 对 `.props` 与 `.targets` 的向上查找各自独立），构建输出 `VALIDATE-RAN TFM=net8.0 RootPropsSeen=` 之后随即 `error : TFM must be net10.0 but was net8.0`，`Build FAILED`。因此四条定死：

1. `contract-mirror/` 下**不放任何 `.csproj`**，也**不放 `Directory.Build.props`**——它只装 schema 与 fixture 的字节镜像。
2. 6 个 artifact 的 `.cs` 源文件按只读拷贝落进 `src/Lumio.Server.MvpHost.GeneratedContracts/Generated/`，随该工程一起以 `net10.0` 编译；该工程零 `ProjectReference`、零 `PackageReference`。
3. 拷贝源规避 `TreatWarningsAsErrors` + 分析器的方式：在 `Generated/` 放一份目录级 `.editorconfig` 声明 `generated_code = true`。
4. 因此**不存在独立的 `Lumio.Gen.*` 程序集**。§5.3 第 5 条自过期守卫的 (b) 必须写成「对 `Lumio.Server.MvpHost.GeneratedContracts` 这一个程序集内 `Lumio.Gen.*` 命名空间的反射断言」；写成「遍历被引用的全部 `Lumio.Gen.*` 程序集」会因集合恒空而**静默失效**。

层级由每个 csproj 自带的 MSBuild 属性 `<MvpHostLayer>N</MvpHostLayer>` 声明，`Architecture.Tests` 断言「被依赖方 layer 严格小于依赖方 layer」——**不使用共享 allowlist 文件**，因此新增工程不会造成卡间文件冲突。

### 4.1 生产工程（13 个）

| Layer | 工程 | 职责 | ProjectReference |
|---|---|---|---|
| 0 | `GeneratedContracts` | 架构源 6 个 C# artifact 只读拷贝 + `GeneratedContractManifest`（架构 commit / BaselineId / schemaEpoch / 各 artifact hash）。零 PackageReference。 | 无 |
| 1 | `Wire` | `MvpEnvelopeDocument`、规范 JSON 编解码、结构层 + 语义层双校验、`MvpProtocolPermissionGate` 六项判定 | GeneratedContracts |
| 1 | `Platform` | 单调时钟、Timer→类型化命令投递、`IBoundedInbox<T>`/`IBoundedOutbox<T>`、具名受监督线程、取消树。**全仓唯一允许出现等待/定时语义的工程** | 无 |
| 2 | `HostContracts` | 跨模块**唯一**契约面：全部 id/epoch 值类型、typed command/event、有界端口接口、`IWorldSimulationPort` | Wire, Platform |
| 3 | `Observability` | Audit（durable 意图）与 Diagnostic 两条有界写入面，事件形状照 `logging-event.schema.json` + `correlation.scope` 规则 | HostContracts |
| 4 | `Transport` | 载体无关：连接注册表（**唯一写入者**）、`ConnectionEpoch`、Envelope 校验闸（分配前拒绝）、ingress/egress 有界队列、限流、可注入故障装饰器、`IByteCarrier` SPI | HostContracts, Observability |
| 4 | `Auth` | token 存根 verifier、防重放窗口、不可变 `PermissionGrant`、gate 执行体。**不引用 Transport / Session** | HostContracts, Observability |
| 4 | `WorldSlot` | `WorldSlotHost` 聚合根、`SlotEpoch`、Admission Gate（**唯一所有者**）、Quiesce 原子序列、Simulation Owner Thread、`IFaultAdjudicator` | HostContracts, Observability |
| 4 | `Simulation.Reference` | `IWorldSimulationPort` 参考存根：不透明 key→value 覆盖表 + 单调 `AuthorityRevision`。**零 ECS / Tick 相位 / Gameplay / Voxel 类型** | HostContracts |
| 5 | `Transport.WebSocket` | `IByteCarrier` 的 WSS 实现：Kestrel 监听、Upgrade 期通道认证、一 WS 消息 = 一 Envelope、Close 帧与空闲超时。`<FrameworkReference Include="Microsoft.AspNetCore.App" />` | Transport |
| 5 | `Session` | `ServerConnectionSession` 注册表与状态机、Admission saga、重连窗口、Drain/Kick、架构源 v1.4 §7.1 复制编排 | HostContracts, Observability |
| 6 | `App` | 可执行 `lumio-mvp-host`。**唯一组装根**：显式 `new` 接线全部模块与队列 | 1–11 |
| 6 | `SmokeClient` | 可执行冒烟客户端，与 Bot 同 wire | Wire, Platform |

**单向无环的机制保证**：`Transport` / `Auth` / `WorldSlot` / `Simulation.Reference` 是同层兄弟且**相互零引用**；`Session` 在 Layer 5 编排它们；transport→session 的 `ConnectionEvent`、auth→session 的 `AuthEvent` 全部经 `HostContracts` 的 `IBoundedOutbox<TEvent>` 接口投递，实例在 `App` 组装期接线——**事件不产生反向边**是机制保证，不是纪律。

### 4.2 测试工程（11 个）+ 测试库（1 个）

`TestKit`（测试专用库，`MvpHostProductionProject=false`，非 `IsTestProject`）引用 Wire / Platform / HostContracts。十一个测试工程按 §3.2 列出，各自单向引用对应生产工程；`Integration.Tests` 以**进程方式**拉起 `App` 与 `SmokeClient`，不作为库调用。

**机器强制（三层）**：

1. `Directory.Build.targets` 的 `ValidateMvpHostBuildProfile`（`BeforeTargets=PrepareForBuild`，硬失败三条，形状照抄 `LumioGameRuntime/Directory.Build.targets` 的 `ValidateRuntimeBuildProfile`）：
   - 生产工程不得出现 `xunit` / `nunit` / `mstest` / `Microsoft.NET.Test.Sdk` / `coverlet` / `FsCheck` / `ArchUnitNET` 的 PackageReference，也不得设 `IsTestProject`；
   - 生产工程不得 `ProjectReference` 任何 `*.Tests` 或 `*.TestKit`；
   - 全部工程 `TargetFramework` 必须恰为 `net10.0`、`LangVersion` 必须恰为 `14.0`。
2. `Architecture.Tests` 运行期断言：`MvpHostLayer` 严格递增且无环；红线边不存在（`Transport ↛ Auth`、`Auth ↛ Transport`、`Auth ↛ Session`、`WorldSlot ↛ {Transport, Auth, Session}`、`Simulation.Reference` 出度仅 HostContracts）；`Simulation.Reference` 的被引用计数在生产侧恰为 1（`App`）；不存在任何 DI 容器 / service locator / 全局 EventBus 类型（禁用类型名单）。
3. **「Adapter 缺席仍全绿」**：`build.proj` 的 glob 不含 `adapters/`；`Architecture.Tests` 断言构建图中不存在任何指向 `Lumio.GameRuntime.*` 的 ProjectReference 或 AssemblyReference。这条是「替换存根不改宿主公共面」的机械保证。

### 4.3 队列登记：每工程自带 `queues.json`

每个持有队列的生产工程目录下放一份 `queues.json`，逐条写满七项合同（所有者 / 生产者 / 消费者 / 顺序保证 / 容量门 / 满载动作 / 关闭语义）；`Architecture.Tests` 聚合全部 `queues.json` 并断言三条。

> **断言机制的全文纪律（`System.Reflection` 看不到方法体）：** 反射只能看**签名与元数据**，看不到方法体内部、构造点或调用点。因此本设计里凡需要「谁调用了谁」的断言，一律用中央包表内的 `TngTech.ArchUnitNET`（§7.4）的**方法调用依赖断言**；凡能用类型/成员签名表达的，用**签名级反射断言**；两者都判不了的（例如「实现内不出现除 `FixedTimeEquals` 之外的比较路径」），降级为**签名级收敛 + 定向单元测试 + 评审项**，并在对应卡面写明是哪一种。**不使用 IL 字节扫描**（理由见 §14 J4）。

1. 每条登记行七项字段齐全，且 `owner` 指向的工程在构建图内存在（纯 JSON + csproj 元数据判定）。
2. **ArchUnitNET 调用依赖断言**：凡对 `PlatformModule.CreateInbox<T>` / `CreateOutbox<T>` 存在方法调用依赖的生产工程，`queues.json` 中至少有一条 `owner` 为该工程的登记行；**签名级断言**：某工程的登记行数不得超过该工程内 `IBoundedInbox<T>` / `IBoundedOutbox<T>` 类型化字段与属性的个数。
3. **ArchUnitNET 调用依赖断言**：全构建图不存在对 `System.Threading.Channels.Channel.CreateUnbounded` 的调用依赖；**签名级断言**：`System.Collections.Concurrent.ConcurrentQueue<>` 不作为任何生产类型的字段或属性类型出现。

**为什么不用一份共享登记表**：`modules/README.md` §4.2 的 Queue Contract Matrix 是 Rust 侧队列登记的唯一落点，但该文件被 Rust 卡 `synchronize-implementation-mapping-docs` 独占、本批次不得触碰；而在 `mvp-host/` 下建一份共享 MVP 登记表又会让每张模块卡互相文件冲突。分散登记 + 机器聚合同时解决两者。两表字段口径一致、语义以 `modules/README.md` §4.2 为准；合并时机是 Rust 卡开工时由其后继卡处理。

---

## 5. wire 与契约策略

### 5.1 现实约束（本轮实测复核）

- `schemas/replication-envelope.schema.json` 的 `required` 是 **12 项**，顶层 `additionalProperties: false`；`messageType` enum 8 值；`reliability` 2 值；`integrity.algorithm` 4 值（oneOf 分支约束 `value`）；`transportPolicy` required 5 项（`maxMessageBytes` 1..1048576、`maxFragmentBytes` 1..65536、`antiReplayWindow` ≥1、`authBinding ∈ {SessionAdmission, ConnectionGeneration}`、`errorClass ∈ {Retryable, Rejectable, Fatal}}`）。
- **`body` 在 JSON Schema 里是空壳**：实测 `properties.body == {"type": "object"}`——无 properties、无 required、无 `additionalProperties`。typed body 的真值只在 `tools/lumio_contract.py:355-364` 的 `_REPLICATION_BODY_REQUIRED`，且判定逻辑是 `missing = [f for f in required if f not in body]`——**只查缺失，从不查多余**。
- **C# 侧不存在 `ReplicationEnvelope` 类型定义**：`Lumio.Gen.LanguageBinding/Bindings.cs` 只有名字映射三元组；`Lumio.Gen.ProtocolPermissionValidator` 只有 15 个字段名字符串，没有任何校验方法；`Lumio.Gen.CanonicalSerializer` 只有 2 个常量。
- 本轮实测 `python3 tools/lumio_contract.py validate` → `Validated 167 fixture(s), 0 failure(s).`。**不得硬编码 fixture 总数做断言**（研究阶段记录为 160，说明架构源在两次观测间已变更）；断言只针对本仓镜像的 **16 条 fixture / 20 个受哈希锁文件**（4 份 schema + 16 条 fixture，逐条归属见 §5.5）。

### 5.2 承载形式

**一个 WebSocket 文本消息 = 一个完整的规范 JSON Envelope。** 字段名、类型、枚举值 100% 照抄架构源 schema，一个不增不减。

选 JSON 而非二进制的三条理由：① 架构源**未发布任何二进制 wire 布局**，选二进制就必须发明布局 = 发明 wire；② 架构源已发布的真值全是 JSON，JSON 承载让 12 条 replication fixture **字节级可比**地成为回归输入；③ LumioClient 的 `EncodedFrame` 只包 `ReadOnlyMemory<byte>`、LocalEmbedded 靠 `Channel` 保消息边界、codec 只是 memcpy——两端对内容都不透明，JSON 文本是最低摩擦的共同承载。

**不实现自定义分片/重组**：`transportPolicy.maxFragmentBytes` 只作为**声明值**登记。WSS 与 LocalEmbedded 都天然保消息边界，「一消息一信封」使两条传输走同一上层，不引入流式解析假设。大小上限从 `transportPolicy.maxMessageBytes` 取（≤1048576；MVP 默认取 fixture 的 65536），并在**分配前**拒绝——累计 `WebSocketReceiveResult.Count` 越限即中止读取并关闭，不先分配缓冲。

### 5.3 C# 表示：`MvpEnvelopeDocument`

```csharp
namespace Lumio.Server.MvpHost.Wire;

// 非公共契约。架构源 Lumio.Gen.ContractTypes 生成 ReplicationEnvelope POCO 落地后即删除本类型。
// 字段集与取值域的唯一真值是 contract-mirror/schemas/replication-envelope.schema.json；
// 语义层唯一真值是架构源 tools/lumio_contract.py。归口：LumioGameEngineArchitecture。
internal sealed class MvpEnvelopeDocument
{
    public static EnvelopeParseResult TryParse(ReadOnlySpan<byte> utf8, out MvpEnvelopeDocument doc);
    public int    ProtocolVersion { get; }   public long   Length { get; }
    public ulong  Sequence { get; }          public string SessionId { get; }
    public string ProductId { get; }         public string GameReleaseId { get; }
    public string MessageType { get; }       public string Reliability { get; }
    public IntegrityView Integrity { get; }  public string TraceId { get; }
    public TransportPolicyView TransportPolicy { get; }
    public JsonObject Body { get; }          // ← 刻意不生成 typed body POCO
    public ReadOnlyMemory<byte> ToUtf8();
}

// —— 上面签名引用到的支撑类型（同一命名空间，均为 Wire 的 public 面）——
public readonly record struct IntegrityView(string Algorithm, string Value);
public readonly record struct TransportPolicyView(
    int MaxMessageBytes, int MaxFragmentBytes, int AntiReplayWindow, string AuthBinding, string ErrorClass);
public enum EnvelopeParseStatus { Ok, StructuralReject, SemanticReject }
public readonly record struct EnvelopeParseResult(
    EnvelopeParseStatus Status, string? StableErrorId, string? Detail);

// Transport 侧只读门面：由 MvpEnvelopeDocument 投影而来，不含 body
public readonly record struct EnvelopeHeaderView(
    int ProtocolVersion, ulong Sequence, string SessionId, string ProductId,
    string GameReleaseId, string MessageType, string Reliability, string TraceId, int WireByteLength);

public static class MvpEnvelopeReader {          // Wire 的 public 读入口（文档本体 internal）
    public static EnvelopeParseResult TryReadHeader(ReadOnlySpan<byte> utf8, out EnvelopeHeaderView header);
    public static EnvelopeParseResult Validate(ReadOnlySpan<byte> utf8);   // 结构层 + 语义层双校验
}

// Wire 的 public 写入口，按方向分组：服务端出站 5 个 + 冒烟客户端出站 4 个 = 9 个 Write 方法（§8.1）。
// 不存在通用的 Write(messageType, body) 重载——那会退化成一个可被下游滥用的分发面。
// 每个方法写出的 body 字段集恰好等于 lumio_contract.py:355-364 的 _REPLICATION_BODY_REQUIRED
// 对应组，不多不少（exact-set，理由见 §5.6）：本仓不向任何 body 添加任何字段。
public readonly record struct EnvelopeWriteContext(
    string SessionId, string ProductId, string GameReleaseId, ulong Sequence, string TraceId,
    string Reliability,
    int MaxMessageBytes, int MaxFragmentBytes, int AntiReplayWindow, string AuthBinding, string ErrorClass);

public static class MvpEnvelopeWriter {
    // —— 服务端出站 5 个 ——
    public static ReadOnlyMemory<byte> WriteServerHandshake(in EnvelopeWriteContext ctx);
    public static ReadOnlyMemory<byte> WriteFullSnapshot(in EnvelopeWriteContext ctx, string snapshotId,
        ulong tickId, ulong authorityRevision);          // reliability 恒写 "Reliable"（:802-803）
    public static ReadOnlyMemory<byte> WriteDelta(in EnvelopeWriteContext ctx, string baseSnapshotId,
        ulong fromRevision, ulong toRevision, ulong confirmationSequence);   // tombstones 恒写 []
    public static ReadOnlyMemory<byte> WriteMaintenanceKick(in EnvelopeWriteContext ctx, string reasonCode);
    public static ReadOnlyMemory<byte> WriteError(in EnvelopeWriteContext ctx, string errorClass, string reasonCode);
    // —— 冒烟客户端出站 4 个：只供 SmokeClient 使用；Session 工程对这四个零调用依赖（机器断言，§8.1）——
    public static ReadOnlyMemory<byte> WriteClientHandshake(in EnvelopeWriteContext ctx);
    public static ReadOnlyMemory<byte> WriteBaselineAck(in EnvelopeWriteContext ctx, string snapshotId,
        ulong confirmedRevision);
    public static ReadOnlyMemory<byte> WriteDeltaAck(in EnvelopeWriteContext ctx, ulong confirmationSequence,
        ulong toRevision);
    public static ReadOnlyMemory<byte> WriteResyncRequest(in EnvelopeWriteContext ctx, string resyncReason);
}

// 本仓单点常量组。全部 provisional，取值来源逐条注明；不构成任何公共主张。
public static class MvpWireConstants {
    public const int    ProtocolVersion     = 1;
    public const string Reliability         = "Reliable";        // 出站恒取；FullSnapshot 强制（:802-803）
    // —— transportPolicy 的 5 个必填子字段（顶层 required 12 项之一），取值来自镜像 fixture 实测 ——
    public const int    MaxMessageBytes     = 65536;
    public const int    MaxFragmentBytes    = 4096;
    public const int    AntiReplayWindow    = 1024;
    public const string AuthBinding         = "SessionAdmission";
    public const string TransportErrorClass = "Rejectable";
    // —— 无公共映射集时 mappingSetHash 的 MVP 口径（§5.7a）；hash256 = ^[0-9a-f]{64}$ ——
    public const string MappingSetHash      =
        "0000000000000000000000000000000000000000000000000000000000000000";
    // —— integrity：MVP 只产出「不作完整性主张」的合法 oneOf 分支（§5.7）——
    public const string IntegrityAlgorithm  = "None";
    public const string IntegrityValue      = "none";
}
```

`WriteFullSnapshot` 按传入的单一 `authorityRevision` **机械填充** `sessionRevisionVector` 的 7 个字段（`tickId` 取传入值，`gameRevision` / `voxelWorldRevision` / `replicationRevision` 取 `authorityRevision`，`configRevision` 取 `0`，`schemaEpoch` 取生成物 manifest，`chunkRevisionSet` 为单键 `{"c:0:0:0": authorityRevision}`，该 key 匹配 §5.4 的 canonical `_CHUNK_KEY` 正则）——这是冻结 schema 强制的信封字段，不是宿主自造的体素模型（§6.5）。

**四处出站取值的来源（此前无声明，现逐条定死，全部进卡 5 的验收）：**

- **`transportPolicy` 的 5 个子字段。** `WriteServerHandshake` 改为与其余 8 个 writer 一样收 `EnvelopeWriteContext`——此前它是唯一不收 ctx 的 writer，导致服务端首帧的 `transportPolicy`（顶层 12 项 required 之一、含 5 个子字段）**无任何取值来源**。值由 `App` 组装期从 `MvpWireConstants` 装配进 ctx。**`authBinding` 裁决取 `SessionAdmission`**，理由两条：① MVP 每条 Active 消息的权限判定，六项比对全部来自 admission 派生的上下文（§5.8），`connectionGeneration` 在其中只是**一项被比对的字段**、不是绑定主体；② 镜像的 8 条正向 fixture 一律取 `SessionAdmission`。
- **`reliability`。** 出站恒取 `MvpWireConstants.Reliability`（`"Reliable"`）；`WriteFullSnapshot` 忽略 ctx 取值、恒写 `"Reliable"`，因为公共契约对它有硬约束（§5.4 语义层）。
- **`Delta.body.tombstones`。** `WriteDelta` **不收该入参，恒写空数组 `[]`**。理由：`tombstones` 表达实体墓碑，而 MVP 参考存根的世界模型是不透明 key→value 覆盖表、根本不存在实体生命周期概念（§6.5），因此没有墓碑可发；写空数组既满足 `_REPLICATION_BODY_REQUIRED` 的必填要求（`:359`），又避免为它生成一个 typed body POCO（违反主张一）。
- **`mappingSetHash`。** 出站恒取 `MvpWireConstants.MappingSetHash`，口径与理由见 §5.7a。

`WriteResyncRequest` / `WriteClientHandshake` / `WriteBaselineAck` / `WriteDeltaAck` 四个方法**只供 `SmokeClient` 的客户端侧使用，服务端不调用**——`ResyncRequest` 在公共契约里由检测到 gap 的**副本方**发出（架构源 v1.4 §7.1 的 `DeltaAck / GapDetected -> ResyncRequest -> FullSnapshot or ResyncPatch`），服务端只接收（§8.1）。卡 11 有一条 ArchUnitNET 调用依赖断言：`Session` 工程对这四个方法零调用依赖。

`Transport` 消费的是只读门面 `EnvelopeHeaderView`（`Wire` 的 public 面，经 `MvpEnvelopeReader.TryReadHeader` 取得），`MvpEnvelopeDocument` 本身 `internal` + `InternalsVisibleTo` 只开给本仓测试工程。`EnvelopeParseStatus` 的 `StructuralReject` / `SemanticReject` 二值使 §5.5 的「哪条反例由哪一层拦下」成为可断言的返回值，而不是靠日志文本判定。

**它凭什么不算「第二套手写公共协议」——五条，每条机器可判：**

1. **没有本仓授权的格式文档。** 字节的定义在架构源的 schema 与 fixture 里；本文档只说「我们发的是它的实例」。
2. **字段名不是我们写的。** `Architecture.Tests` 从镜像 schema 读出 `required` 12 项与全部 `enum`，反射断言 `MvpEnvelopeDocument` 的 header 属性名集合（camelCase 化后）**恰好相等**、枚举取值集合**恰好相等**。多一个少一个即红。
3. **没有 typed body 类型。** `body` 是 `JsonObject`，不存在 `DeltaBody` / `FullSnapshotBody` 之类可被下游抄走的 C# 形状。这是刻意的——typed body POCO 是最容易硬化成事实标准的东西。
4. **词表来自生成物，有真实编译期耦合。** 错误码经 `Lumio.Gen.ContractTypes.Catalog.StableErrorIds`（43 值）断言；gate 字段名经 `Lumio.Gen.ProtocolPermissionValidator.ActivePermissionFields.Names`（15 项）断言；`schemaEpoch` 取自生成物 manifest；`Catalog.SchemaIds` 断言 `replication-envelope` 在册。生成器落地时这些耦合点会**编译期断掉**，不会静默漂移。
5. **自过期守卫（`ContractArtifactDebtTest`）。** 断言 (a) `Lumio.Gen.LanguageBinding.Bindings` 仍把 `replication-envelope` 映射到 `ReplicationEnvelope`，且 (b) **`Lumio.Server.MvpHost.GeneratedContracts` 程序集内 `Lumio.Gen.*` 命名空间下反射不到名为 `ReplicationEnvelope` 的类型**——契约生成物是源码拷贝、不是独立程序集（§4），写成「遍历被引用的全部 `Lumio.Gen.*` 程序集」会因集合恒空而静默失效。生成器落地那天 (b) 变红——**红灯本身就是删除本仓 DTO 的指令**，技术债从此有到期日。同一模式复制一份给 `ActivePermissionFields`：一旦该命名空间出现可执行校验方法而不只是字段名表，对应测试变红，手写 gate 必须让位。

### 5.4 双层校验（两层都必须有）

**结构层**：对镜像 schema 做 JSON Schema 校验。校验器手写在 `Wire` 工程内、零第三方 NuGet（保持零依赖性质），**支持范围硬性限定**为四份镜像 schema 实际用到的构造：`type / required / enum / const / pattern / minimum / maximum / minLength / maxLength / additionalProperties / oneOf / allOf / $ref`；超出即抛出并报错，不追求通用性。

**语义层**：逐条转写 `tools/lumio_contract.py`，每条带源行号注释：

- `_REPLICATION_BODY_REQUIRED`（`:355-364`）8 组：`Handshake(role)`；`FullSnapshot(snapshotId, tickId, sessionRevisionVector, schemaEpoch, mappingSetHash)`；`BaselineAck(snapshotId, confirmedRevision)`；`Delta(baseSnapshotId, fromRevision, toRevision, mappingSetHash, confirmationSequence, tombstones)`；`DeltaAck(confirmationSequence, toRevision)`；`ResyncRequest(resyncReason)`；`MaintenanceKick(reasonCode)`；`Error(errorClass, reasonCode)`。
- `_SESSION_REVISION_FIELDS`（`:365-373`）7 项全必填：`tickId, gameRevision, voxelWorldRevision, chunkRevisionSet, replicationRevision, configRevision, schemaEpoch`；`chunkRevisionSet` 的 key 必须匹配 `_CHUNK_KEY`（`:401`）的 canonical 正则**原文**——`^c:(0|-?[1-9][0-9]{0,9}):(0|-?[1-9][0-9]{0,9}):(0|-?[1-9][0-9]{0,9})$`，逐字转写、**不得写宽**。本轮实测该正则拒绝 `c:00:0:0`（前导零）与超 10 位分量，接受 `c:0:0:0` 与 `c:-1:2:3`；D-013 的确认原文把 **ChunkId format** 列为「改动需要新 ADR 与 BaselineId」的项，写成 `^c:-?\d+:-?\d+:-?\d+$` 就是在本仓放宽一条冻结的公共格式约束——而镜像的 16 条 fixture 里没有任何 chunk-key 反例，本设计声明的两道门都覆盖不到它。
- `Delta.toRevision >= fromRevision`；`Delta.body.gapDetected` 为真 ⟹ 必须同时带 `resyncReason`。
- **`messageType == "FullSnapshot"` ⟹ `reliability == "Reliable"`**（`:802-803`；本轮实测 `semantic_errors(Unreliable FullSnapshot)` → `['FullSnapshot must use Reliable delivery']`）。这是公共契约里**唯一**一条 `messageType` × `reliability` 的交叉约束。镜像的 8 条正向 fixture 全是 `Reliable`、4 条反向没有 reliability 用例，**fixture 回归门拦不住它**，只能由语义层逐条转写覆盖；出站侧由 `WriteFullSnapshot` 恒写 `"Reliable"` 保证（§5.3）。
- `Error.body.errorClass ∈ {Retryable, Rejectable, Fatal}`；`messageType` 必须在 8 值注册集内。
- `_INTEGRITY_VALUE_RULES`（`:402-407`）四分支正则：`None→^none$`、`CRC32C→^[0-9a-f]{8}$`、`SHA256→^[0-9a-f]{64}$`、`AEAD→^[A-Za-z0-9+/=_-]{24,256}$`。
- `common.schema.json` 的 `$defs`：`id` ≤128 允许冒号、`productId` ≤32 不含冒号、`releaseId` ≤64、`hash256` 小写 64 位十六进制、`netEntityId` 32 位十六进制。
- **本仓私有的更严两条**：① `Handshake` 的 body 字段集必须**恰好等于** `{role}`，多一个字段即 `Rejectable`（理由见 §6.2）；② **出站 exact-set**——本仓写出的每一条 Envelope，其 `body` 的字段集必须**恰好等于** `_REPLICATION_BODY_REQUIRED` 的对应组，不多不少（理由见 §5.6；本轮实测镜像 8 条正向 fixture 的 body 字段集**逐条恰好等于**对应组，因此该断言与金标准门相容）。两条都只作用于**本仓出站**，不改变入站对他方报文的判定——入站仍按公共 required 语义只查缺失、不查多余。

**语义层不是加分项，是强制项。** 硬证据：4 条反例 fixture 中，`replication-unregistered-message-type` 与 `replication-integrity-value-mismatch` 靠 JSON Schema 就能拦（enum + oneOf 正则）；而 `replication-gap-without-resync` 与 `replication-missing-snapshot-identity` 在 JSON Schema 层**完全合法**，只有语义层能拦。**只做 JSON Schema 校验的实现必然漏掉一半反例。**

### 5.5 fixture 当真值：镜像 + 哈希锁

`mvp-host/contract-mirror/` 是架构源 `schemas/` + `fixtures/` 的字节级只读镜像，配 `contract-mirror.sha256` 与记录来源 commit / BaselineId 的 `MIRROR.md`；`eng/sync-contract-mirror.sh` 从 `$LUMIO_ARCHITECTURE_ROOT` 拷贝并重写哈希，`eng/verify-contract-mirror.sh` 无条件校验本地哈希（漂移退出码 33），当 `$LUMIO_ARCHITECTURE_ROOT` 存在时额外比对源仓。生成物同理 vendored 进 `src/Lumio.Server.MvpHost.GeneratedContracts/`，`verify-generated-contracts.sh` 漂移退出码 32——照抄 `LumioGameRuntime/src/Lumio.GameRuntime.GeneratedContracts` 与 `eng/generate-contracts.sh` 的已验证范式。

**取「本仓携带 + 哈希锁」而非「环境变量指向兄弟仓」**：本仓根没有 `schemas/` / `fixtures/` / `ids/` 镜像（只镜像架构正文 `.md` 与一个 `.baseline.sha256`），CI 里也没有跨仓路径；靠环境变量指路会让 CI 上等于没有这道门，违背「今天就能独立跑绿」。

**回归门（每次 CI）——镜像共 16 条 fixture，逐条有归属**：8 条正向 replication fixture 必须**全部通过**（解析→校验→重新序列化→再校验，字段集与值语义相等；**不断言字节相等**，canonical ordering 未公开）；4 条反向 replication fixture 必须**全部被拒**，且额外断言「哪条由哪一层拦下」——防止实现退化成只做 schema 校验；2 条 gate fixture 驱动六项判定回归；1 条 world-slot 状态机 fixture 驱动 `states` / `transitions` / `terminalStates` / `anyActiveTo` 断言（§6.4）；1 条 `logging-auth-reject-audit.json` 驱动 Audit 事件形状断言（§6.2，落卡 6）。8 + 4 + 2 + 1 + 1 = **16**。

**哈希锁覆盖 20 个文件** = 4 份 schema + 16 条 fixture，即**镜像自架构源的全部文件**。`contract-mirror/MIRROR.md` 是本仓手写、架构源没有对应文件，因此**不进哈希清单**；「目录下不存在未登记文件」这条断言必须把它写成唯一白名单项，否则该断言与字节级镜像哈希两条互斥、落地即恒红（卡 3）。

**额外的基线漂移哨兵**：断言镜像内 `Lumio.Gen.ContractTypes.Catalog.BaselineId == "LGE-V1.4-2026-08-27"`。架构源升版时该断言先红，逼出重新同步——否则镜像可能长期停在旧基线而哈希门仍绿。

### 5.6 ★ 核心裁决：**不扩展 `body`**，以及由此导致的 A1 拆分

**问题。** A1 需要两件公共 wire 面**根本没有**的东西：
- **服务端→客户端的状态载荷**：实测 `fixtures/valid/replication-full-snapshot.json` 与 `replication-delta.json` 的 body 只含标识、版本向量、`mappingSetHash`、`tombstones`——**没有任何承载实际世界状态的字段**。一个不携带状态的 FullSnapshot 在语义上是空的。
- **客户端→服务端的 gameplay 命令**：8 个冻结 `messageType` 中没有任何一个表示客户端命令；客户端能合法发出的只有 `Handshake` / `BaselineAck` / `DeltaAck` / `ResyncRequest`。

**裁决：两个方向一律拒绝。本仓不向任何 `body` 添加任何字段。**

**(A) 拒绝：在 `FullSnapshot.body` / `Delta.body` 携带任何私有状态载荷字段（包括曾被本设计提出的 `mvpAuthorityPayload`）。**

本条是对本设计上一版裁决的**推翻**，四条理由：

1. **一份 Accepted 的 ADR 直接否决了这个做法。** `.spec/decisions/ADR-028-replication-typed-bodies.md`（**Status: Accepted**，Baseline `LGE-V1.3-2026-08-27`，已进入 V1.4 基线，Owner `LumioGameRuntime`）的 Decision 为 8 个 messageType 各定义 required typed body；其 **Alternatives 一节原文**：

   > `Keeping a free-form payload was rejected because two implementations can pass the gate and disagree on Snapshot identity.`

   它否决的就是 free-form payload，而否决理由（两个实现都能过门却对 Snapshot 身份理解不一致）**恰好就是**上一版方案造成的失效模式：LumioServer 与 LumioClient 需要带外约定同一段不透明字节。上一版的全部论证建立在「schema 顶层 `additionalProperties:false` 而 `body` 开放 = 刻意的『信封冻结、body 可扩展』结构」上；该推断与这份直接对口的 ADR 相反。`tools/lumio_contract.py:355-364` 的 `_REPLICATION_BODY_REQUIRED` 与 ADR-028 逐字一致；它只查缺失不查多余，是**机器门的能力边界，不是设计许可**。
2. **仓库红线明文禁止。** `.spec/knowledge/standards/repository-architecture.md`：「冲突时不得在 Host 内自行改写公共 Envelope/Release/Capability；先在架构源完成 ADR、Schema、Fixture 和新 Baseline」。在已有 Accepted ADR 明确否决的前提下自行扩展冻结 typed body，等于绕过架构源变更流程既成事实化一个公共 wire 字段。
3. **本设计自订的停手规则适用于它自己。** §5.9 BLOCKED 触发条件第一条就是「需要新增或修改公共**字段**……停手，不得先实现后补」，而一个挂在冻结 typed body 上的新字段正是它。
4. **非对称不成立。** 上一版用「server→client 允许 / client→server 拒绝」的非对称裁决，两边其实同属一个模式：公共面缺位 → 自行补一个。两者均应拒绝。

落地后果（全部机器断言）：

- `MvpEnvelopeWriter.WriteFullSnapshot` / `WriteDelta` 的签名**不包含任何状态载荷入参**；`MvpWireConstants` 不定义任何私有 body 字段名常量（§5.3）。
- 断言由「只允许一个私有字段名、只出现在两个 messageType」换成**更强的一条**：`Architecture.Tests` 断言本仓出站的每一条 Envelope，其 `body` 字段集**恰好等于** `_REPLICATION_BODY_REQUIRED` 的对应组（exact-set，不多不少）——即**本仓不向任何 body 添加任何字段**。这比原断言强：原断言只管一个名字，新断言封死整个集合。
- 本仓因此**不能**在 MVP 期把世界状态发给客户端。这直接决定了 §9 的 A1 拆分：**A1-α 只能证明协议与生命周期闭环，不能证明『Bot 看到方块被挖』**；后者整体归入 A1-β（BLOCKED）。
- 登记为 BLOCKED **B4**（§12.1）：请架构源为 `FullSnapshot` / `Delta` 冻结一个公共状态载荷字段。B4 不再是「并行上报的诉求」，而是 **A1-β 的硬前置**（与 B8 并列）；`absences.json` 新增 `ABS-REPLICATION-STATE-PAYLOAD`（reason=`决策门冻结`，successor=`needs-new-card`）。

**(B) 拒绝：任何客户端→服务端的 gameplay 命令承载。** 不往 `DeltaAck.body` / `BaselineAck.body` 塞命令载荷，不新增 `messageType`。

理由：D-009 原文「The V1 wire surface is limited to the replication envelope MessageTypes; the server `protocol-dispatch` boundary stays blocked and **no repository may invent a dispatch wire format**」。承载客户端发起的 gameplay 命令，需要的是一个**不存在的 message type**；把它搭在确认消息上，正是「发明一种分发客户端消息的方式」。schema 的 `body` 开放使它机器可通过——**但机器拦不住不等于设计允许**。这是本设计明确不做的事，且不接受「先跑通再说」。

**(C) 对照：为什么子协议 token 承载不适用上面两条（原则性区别，必须写清）。** §6.2 定义的 `Sec-WebSocket-Protocol: lumio.mvp.v0, <opaqueTokenB64Url>, <opaqueNonceB64Url>` **保留**，技术选择不变。它与被删除的 body 扩展有两条原则性区别：

| | body 私有状态载荷字段（已删除） | 子协议 token/nonce 承载（保留） |
|---|---|---|
| 落在哪里 | 受 **Accepted ADR-028** 治理的**冻结 typed body 之内** | 完全落在**任何冻结公共产物之外**——WebSocket 握手子协议不在任何 Lumio schema / ADR / fixture / ID Registry 里 |
| 有无授权 | 无。任何文件都未授权扩展 replication envelope 的 body | **MVP 章程显式授权**：MVP 计划 §2.2 「正式 Auth 线格式 → D-011 悬决，**MVP 用存根**」；§4 轨道 A 的 A1 行同样列出「Handshake/Auth 存根」 |
| 失效模式 | ADR-028 已写明：两个实现都能过门却对 Snapshot 身份不一致 | 不适用：token 在 WebSocket 建立前就被消费并丢弃，**永不进入任何 Envelope 字段**，不参与任何公共语义的身份判定 |
| 本设计的防线 | —— | §5.4 把「Handshake body 字段集恰好等于 `{role}`」写成机器断言，堆死了 D-011 最容易踩的违规点 |

但**登记不得缺失**：子协议承载同属「公共面缺位期的私有约定」，适用与 `length`（§5.7）同一条退场纪律。它已补齐：`absences.json` 的 `ABS-AUTH-CREDENTIAL-CARRIAGE`、§11 的 G18、§12.1 的 B10。

**后果（必须被排期正视）**：A1 的字面退出条件「Bot 跨进程挖方块」被拆成 A1-α 与 A1-β（§9）。A1-α 用一条**带外的、仅回环的、开关门控的测试控制面**推进服务端权威状态，以证明复制状态机向前推进——测试控制面不是 wire、不经过任何 Envelope，因而不触碰任何冻结面。**但它不能代替状态载荷**：客户端无法在 `Delta` 里看到任何世界内容，只能看到 revision 严格前进。A1-β 在架构源冻结公共状态载荷字段（B4）**与**客户端输入承载（B8）之前 **BLOCKED**。

### 5.7 `length` 与 `integrity`：不作任何 MVP 语义主张

- **`length`** 的语义在架构源**没有任何定义或校验**：schema 只约束 `integer ≥ 0`，`lumio_contract.py` 的 replication 分支完全不检查它，8 条正向 fixture 无论 body 大小**一律写死 256**。裁决为**非对称口径**：
  - **入站**只校验 `integer >= 0`，**不做任何交叉核对**——否则镜像的 8 条 fixture（全写 256）会被自己的实现拒掉，金标准门自相矛盾。这条由 fixture 回归门本身机器保证。
  - **出站**写 `body` 对象规范 JSON 序列化后的 UTF-8 字节数（选 body 而非整信封，因为整信封长度会自指）。
  - 文档与 `absences.json` 明写「公共契约未定义，此为 MVP 双端私有约定，不构成对 `length` 语义的公共主张」，并列为与 LumioClient 必须双向确认的条目 + 架构源澄清项 B5。
- **`integrity`**：MVP **只产出** `{"algorithm":"None","value":"none"}`——schema 合法的 oneOf 分支，表示「不作完整性主张」，不需要定义任何哈希原像；通道完整性由 WSS/TLS 提供。**接收侧仍按 4 分支正则全量校验**，因此 fixture 里的 SHA256 dummy 能被正常接受。若将来需要 SHA256，必须先由架构源定义哈希覆盖哪一段字节——BLOCKED 触发条件之一。

### 5.7a `mappingSetHash`：无公共映射集时的 MVP 口径

`mappingSetHash` 是 `_REPLICATION_BODY_REQUIRED` 中 `FullSnapshot` 与 `Delta` 的必填字段（`:355-364`），取值域 `common.schema.json#/$defs/hash256` = `^[0-9a-f]{64}$`（实测），镜像 fixture 用占位值 64 个 `a`。它指向一套**LumioGame 拥有、MVP 期并不存在**的公共映射集：ADR-005 的 Decision 段写明「Each Mapping declares source/target field, Role, Owner, AOI, reliability, quantization, prediction and lifecycle behavior」，Contract 段写明「Mapping schemas and generated tests are owned by Game but consumed by Runtime/Client」，架构源另有已冻结的 `schemas/replication-mapping.schema.json`。本设计**不定义**任何映射集。

**裁决——与 `length` 同一处置模式（非对称口径 + 登记）：**

- **出站**固定填 `MvpWireConstants.MappingSetHash`，取值为 64 个 `0`（匹配 `hash256`，且刻意与 fixture 的 64 个 `a` 区分）。它是**本仓单点常量、provisional**，含义仅为「本 MVP 宿主不声明任何映射集」，**不是任何真实映射集的摘要，不构成对该字段语义的公共主张**。
- **入站**只按 `hash256` 正则校验取值域，**不与任何本地映射集做交叉核对**——本仓没有映射集可比对，任何交叉核对都是在发明语义。
- 登记三处：`absences.json` 的 `ABS-REPLICATION-MAPPING-SET`（source 指 `contract-mirror/schemas/replication-envelope.schema.json`，reason=`阶段未到`，successor=`needs-new-card`）、§11 的 G17、§12.1 的 B9。
- 机器断言（卡 5）：出站 `FullSnapshot` / `Delta` 的 `mappingSetHash` 恒等于该单点常量，且匹配 `^[0-9a-f]{64}$`；全仓不存在第二个 `mappingSetHash` 取值来源。

### 5.8 `ProtocolPermissionValidator` 缺位的受控降级

ADR-022 与 v1.4 §7.3 都要求 Active 消息「必须经过架构源工具链生成的 Protocol/Permission Validator」，而 C# 侧的该 artifact 是不可执行的字段名表。MVP 只能自己实现六项比对——这在字面上就是 ADR-022 否决的 `Hand-written per-repo validators were rejected for drift`。**这是一处受控降级，四重护栏：**

1. **严格照抄** `tools/lumio_contract.py:1169-1192` 的判定（本轮实测 `sed -n '1169p' tools/lumio_contract.py` → `    elif schema_id == "protocol-permission-gate":`；`1005-1028` 是 Root-ABI 表与 artifact-index 的校验，与 gate 无关）：`matched = sessionId==admittedSessionId && productId==admittedProductId && gameReleaseId==admittedGameReleaseId && role==admittedRole && claims ⊆ admittedClaims && connectionGeneration==admittedConnectionGeneration`；`Accept` 必须 matched 为真、不得有 admission 外的 claim、**不得携带 rejectReason**；`Reject` 必须有 rejectReason，且**代次不等时 rejectReason 必须是 `StaleConnectionGeneration`**。不增删任何一项判据——**本仓不得扩展这个字段集**。
2. **真实编译期耦合**：实现类构造期直接消费 `Lumio.Gen.ProtocolPermissionValidator.ActivePermissionFields.Names` 做字段名断言。
3. **fixture 回归**：`protocol-permission-gate-accept.json` 判 Accept；`protocol-permission-gate-stale-generation.json` 判 Reject 且 `rejectReason == "StaleConnectionGeneration"`。
4. 在「未触碰声明」与 `absences.json` 里写明：本实现是 ADR-022 生成器缺位期的**临时替身**，不对外发布、不作为公共契约、生成器落地即替换。

判定体的签名（属 `Wire` 工程 Layer 1，因此输入是自持值类型而非 `HostContracts` 类型）：

```csharp
namespace Lumio.Server.MvpHost.Wire;

// 13 个判定字段来自 lumio_contract.py:1169-1192 的比对逻辑；
// schema 另有的 antiReplay.connectionScopeOwner / sessionScopeOwner 是常量字面量
// （"ConnectionLayer" / "ClientReplicaSession"），不参与判定，由 fixture 测试单独断言。
public readonly record struct MvpPermissionGateRequest(
    string SessionId, string ProductId, string GameReleaseId, string MessageId, string Role,
    ImmutableArray<string> Claims, ulong ConnectionGeneration,
    string AdmittedSessionId, string AdmittedProductId, string AdmittedGameReleaseId,
    string AdmittedRole, ImmutableArray<string> AdmittedClaims, ulong AdmittedConnectionGeneration);

public enum MvpPermissionVerdict { Accept, Reject }

// RejectReason 只能取 protocol-permission-gate.schema.json 的 7 值之一；Accept 时必须为 null。
public readonly record struct MvpPermissionGateResult(MvpPermissionVerdict Verdict, string? RejectReason);

public static class MvpProtocolPermissionGate {
    public static MvpPermissionGateResult Evaluate(in MvpPermissionGateRequest request);
    public static ImmutableArray<string> ActiveFieldNames { get; }   // 直接取自 ActivePermissionFields.Names
}
```

`Role` 与 `Claims` 是**准入上下文，不得作为每条消息的 wire 字段**（ADR-022 明确否决）。反重放所有权二分保持：**连接级**（帧/通道序列）归 Transport / Connection 层，**会话级**（准入后的会话消息序列）归 `ServerConnectionSession` 所有者，**不合并成一套**。

### 5.9 BLOCKED 上报路径

**触发条件（任一命中即停手，不得先实现后补）**：需要新增或修改**任何冻结公共产物**上的字段——公共**字段** / **messageType** / **ErrorCode** / **Capability ID** / **Schema** / **状态机迁移** / **BaselineId**；需要**定义一份公共凭据线格式（schema / 编码 / 算法 / 轮换 / nonce 派生）**；需要 MessageId 命名空间或 RPC 分发。

**边界澄清（此前措辞自相矛盾，现分开）：** 上面第二条禁的是「**定义公共凭据 schema**」，不是「在冻结公共产物之外用一个私有通道搬运一段不透明字节」。判据两问，两问都为「否」才不触发 BLOCKED：

| 问 | 定义公共凭据 schema（BLOCKED） | 子协议承载不透明存根 token（MVP 章程许可，§5.6-C） |
|---|---|---|
| 它落在任何冻结公共产物（schema / ADR / fixture / ID Registry / Envelope 字段）之内吗？ | **是** | **否**——WebSocket 握手子协议不在其中任何一处 |
| 它需要对方仓理解字节的**内部结构**才能互通吗？ | **是**（凭据格式、算法、轮换、nonce 派生） | **否**——本设计不定义 blob 内部格式，只规定 D-011 已冻结的**行为契约**：准入前必须先过防重放 |

**不触发 BLOCKED 不等于免登记。** 任何这类私有承载必须同时做到三件事，缺一即视为违规：① `absences.json` 落四元组；② §11 记 known gap；③ §12.1 提架构源诉求（请求冻结，或明确判归 Host 私有）。子协议 token 承载按此登记为 `ABS-AUTH-CREDENTIAL-CARRIAGE` / G18 / B10。

**路径（v1.4 §17 变更规则）**：停止实现 → 在 `mvp-host/absences.json` 与本文档 §10 登记 → 向 `LumioGameEngineArchitecture` 提出诉求，走 `ADR → Schema → fixtures → 新 BaselineId → 七仓镜像同步` → 回本仓同步镜像与实现。**本仓不得**新增/修改架构源的 schema、ID Registry、fixture、ADR 或 BaselineId。

**WebSocket 传输能力字符串的判定：不阻塞实现，但阻塞公共化。** `TransportProfile` 在整个架构仓只出现在 §10 的正交轴散文里——无 schema、无 ID Registry 命名空间、无 fixture、无验证器分支；`host-capability.schema.json` 的 `capabilities` 是自由 id 字符串数组且不与 Capability 命名空间交叉校验（其正向 fixture 自己就用了两个未注册字符串）；D-004 明文 `Adapter-only choice does not change baseline; envelope/codec changes do`。因此 MVP 把传输能力字符串定义为**本仓私有的 Host Profile 声明**，集中在一处常量并注明 `provisional, replace with registered ID after R-00258`；**禁止 MVP 因 WebSocket 而新增任何 ErrorCode**。一旦要把它当公共 ID 用，立即停下等 R-00258。

---

## 6. 最小面

命名总纪律：沿用公共 PascalCase 与 Rust 侧已冻结名（`ServerConnectionSession`、`WorldSlotHost`、`SlotEpoch`、`ConnectionEpoch`、`PermissionGrant`、`StaleEpoch`、`SessionLocalProven` / `SlotStateUnproven` / `ProcessFault`、`MaintenanceKick`、`SnapshotCut`），使两套实现的测试断言可互查。错误码一律取自 `Lumio.Gen.ContractTypes.Catalog.StableErrorIds`（43 值），**MVP 不发明任何新错误码**。SRV-D-001..017 的数值只作为 MVP 配置默认值出现并标注 `provisional`，不写成公共常量、Schema、ABI 或性能承诺。

### 6.0 Platform（host-runtime 等价最小面）

```csharp
public readonly record struct MonotonicInstant(long Ticks);
public interface IMonotonicClock { MonotonicInstant Now { get; } }

// 全仓唯一允许触碰 System.DateTimeOffset 的出口，与 IMonotonicClock 严格分域：
// 单调时钟用于超时/窗口/间隔；墙钟只用于产出 logging-event 的 timestamp 字段。
// 返回值必须匹配 common.schema.json#/$defs/timestamp：^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,9})?Z$
public interface IWallClock { string UtcIso8601Now(); }

public readonly record struct TimerId(ulong Value);
public interface ITimerService {
    TimerId Schedule<TCommand>(MonotonicInstant dueAt, IBoundedInbox<TCommand> target, in TCommand command);
    bool Cancel(TimerId id);
}   // 定时语义一律经 Timer 以类型化命令投递；禁止注册任意闭包回调

public enum EnqueueStatus { Accepted, Full, Closed }
public readonly record struct EnqueueResult(EnqueueStatus Status, string? StableErrorId);
public readonly record struct QueueBudget(int MaxItems, long MaxBytes);

public interface IBoundedInbox<T> {
    QueueBudget Budget { get; }
    EnqueueResult TryEnqueue(in T item);      // 满载返回显式状态，绝不阻塞
    bool TryDequeue(out T item);
    int Count { get; }
    void Close();
}
public interface IBoundedOutbox<T> { EnqueueResult TryPublish(in T item); }

public interface IThreadBody { ThreadStepResult Step(CancellationToken ct); }
public interface INamedThreadSupervisor {
    ThreadHandle Start(string name, IThreadBody body);
    bool TryDrainEvent(out SupervisionEvent evt);
}
```

**存在的理由**：Rust 侧所有模块纪律（「任何模块不得自建 sleep/轮询线程」「定时语义全部经 Timer 以命令投递实现」「全部线程经 host-runtime 受监督创建并具名」）建立在 `modules/host-runtime` 之上。没有等价物，重连窗口（SRV-D-004）、防重放窗口（SRV-D-005）、ack 超时（SRV-D-015）会散落成 `Task.Delay` 与自建轮询，替换时是结构性返工。

纪律（`eng/banned-public-api.txt` + `Microsoft.CodeAnalysis.BannedApiAnalyzers` 强制，分析器接线落在 `mvp-host/Directory.Build.props`，见 §7.4）：禁 `System.DateTime` / `System.DateTimeOffset`、禁 `Thread.Sleep`；`Task.Delay` 只允许出现在 `Platform` 内部。**唯一例外**：`Platform` 内 `IWallClock` 的实现文件可使用 `System.DateTimeOffset`，该例外以工程级 `NoWarn` + 单文件 pragma 表达并在源码注释指出它是全仓唯一墙钟出口；`Platform` 之外的任何工程一律无例外。**为什么必须有这个出口**：`logging-event.schema.json` 的 `required` 含 `timestamp`（ISO-8601 UTC 墙钟正则）与 `eventId`，`additionalProperties:false`——没有墙钟出口，本仓根本产不出一条合法的 logging-event，`Observability` 的全部审计断言都不可满足（§6.6）。**`IWallClock` 不得用于任何超时、窗口、间隔或顺序判定**，由评审项与「`IWallClock` 只被 `Observability` 引用」的 ArchUnitNET 依赖断言共同守住。有界队列基于 `Channel.CreateBounded` 包在 internal 类里，容量来自显式 budget struct，入队对 payload 做防御性拷贝（照抄 Runtime `DurableEvidenceRouter` 的 `record with { Payload = payload.ToArray() }`）。

### 6.1 transport（WebSocket / WSS）

```csharp
public readonly record struct TransportConnectionId(ulong Value);
public readonly record struct ConnectionEpoch(ulong Value);        // 每次 Bind/Unbind 递增
public readonly record struct PermissionGrantRef(ulong Value);      // transport 侧完全不透明
// EnvelopeHeaderView 的定义在 §5.3（属 Wire 工程，Layer 1）；此处只消费，不重复定义。
public readonly record struct ValidatedEnvelopeBytes(ReadOnlyMemory<byte> Bytes, EnvelopeHeaderView Header);
public readonly record struct OutboundEnvelopeBytes(ReadOnlyMemory<byte> Bytes);

public interface ITransportService { BindEndpointResult BindEndpoint(in TransportEndpointOptions options); }
public readonly record struct TransportEndpointOptions(
    string UriPrefix, bool RequireTls, int MaxMessageBytes, int MaxConnections,
    string ProductId, string GameReleaseId);

public abstract record ConnectionCommand {
    public sealed record Bind(TransportConnectionId Id, ConnectionEpoch Epoch, PermissionGrantRef Grant, ServerSessionId Session) : ConnectionCommand;
    public sealed record Unbind(TransportConnectionId Id, ConnectionEpoch Epoch) : ConnectionCommand;
    public sealed record Close(TransportConnectionId Id, ConnectionEpoch Epoch, ConnectionCloseReason Reason) : ConnectionCommand;
    public sealed record SetDrain(TransportConnectionId Id, ConnectionEpoch Epoch, bool Draining) : ConnectionCommand;
    public sealed record EnqueueControlEnvelope(TransportConnectionId Id, ConnectionEpoch Epoch, OutboundEnvelopeBytes Envelope) : ConnectionCommand;
}
public enum ConnectionCloseReason { OwnerRequest, Disconnect, Fault, PolicyReject, MaintenanceKick }

public abstract record ConnectionEvent {
    Accepted / HandshakeEnvelope / IngressReady / Backpressured / Closed / Faulted
}

public interface ITransportControlPort { EnqueueResult TrySend(in ConnectionCommand command); }
public interface ITransportEventPort  { bool TryReceive(out ConnectionEvent evt); }
public interface IIngressReader { int Drain(TransportConnectionId c, int maxItems, long maxBytes, Span<ValidatedEnvelopeBytes> destination); }
public interface IEgressWriter  { EnqueueResult TryEnqueue(TransportConnectionId c, ConnectionEpoch e, in OutboundEnvelopeBytes envelope); }

public interface IByteCarrier {            // WSS / 内存环回 只替换本接口
    ValueTask<CarrierAccept>  AcceptAsync(CancellationToken ct);
    ValueTask<CarrierReceive> ReceiveAsync(TransportConnectionId c, Memory<byte> buffer, CancellationToken ct);
    bool TrySend(TransportConnectionId c, ReadOnlyMemory<byte> bytes);
    bool Close(TransportConnectionId c, ConnectionCloseReason reason);
}

public enum TransportFaultAction { Pass, Drop, Duplicate, Delay, Disconnect }   // 与 LumioClient 同名同序
public readonly record struct TransportFaultContext(int Seed, ulong Sequence, bool IsIngress, string MessageType);
public interface ITransportFaultPolicy { TransportFaultAction Decide(in TransportFaultContext ctx); }
```

**状态机**（逐字取自 `modules/transport/README.md`）：`Accepted → EnvelopeValidated → Bound → Active → Draining → Closed`；任一状态因可致命错误 `→ Closed(fault)`。触发事件：`Accepted` = WS upgrade 完成且通道认证通过；`EnvelopeValidated` = 首帧过结构校验；`Bound` = session 的 `Bind` 命令被应用；`Active` = 首个 ingress 入队；`Draining` = `SetDrain(true)`；`Closed` = Close 命令 / WS Close 帧 / 空闲超时 / 致命错误。**每次 Bind/Unbind 递增 `ConnectionEpoch`，携旧 epoch 的命令一律拒绝并回 `StaleConnectionGeneration`。**

`ConnectionEpoch` 与客户端的 `ConnectionGeneration` 是**两个独立计数器**（前者是服务端绑定计数、后者是客户端重连计数），MVP 不做映射。

**断线检测三源**：WebSocket Close 帧、`ReceiveAsync` 抛出、空闲截止（provisional 15 秒，经 `ITimerService` 投递 `Close` 命令，**不自建轮询线程**）。

**错误分类**：

| 类 | 场景 | 动作 / 稳定错误码 |
|---|---|---|
| 可重试 | 瞬时 IO、发送窗口暂满 | 退避重试；Unreliable 满载丢弃并计数 |
| 可拒绝 | 畸形 Envelope、超 `maxMessageBytes`、未过权限过滤、限流超限、旧 epoch 命令 | `MessagePermissionDenied` / `StaleConnectionGeneration` / `StaleEpoch` / `QueueFull`；**只断该连接，不上升为 Slot 或进程故障** |
| 可致命 | 监听绑定失败、TLS 材料缺失、Reactor 资源耗尽 | 上报 process，**进程拒绝启动 / 退出，不降级** |

降级只有三种且全部可 Metrics 观测：可靠积压降速、Unreliable 丢弃、Diagnostic 采样。**超长/畸形/完整性失败必须在分配前拒绝。**

**有界队列（七项合同）**：

| 队列 | 所有者 | 生产者→消费者 | 顺序 | 容量（provisional） | 满载动作 | 关闭语义 |
|---|---|---|---|---|---|---|
| `MvpIngressQueue`（per-connection） | transport | 该连接单一接收循环 → Slot Owner Thread | 严格 FIFO，SPSC | SRV-D-001：256 条 / 256 KiB | Unreliable 丢弃并计数；**Reliable 以 `QueueFull` 断开连接**（不静默丢可靠消息） | Gate 关闭后停收；Quiesce 按序列处置余量 |
| `MvpEgressQueue`（per-connection） | transport | Owner Thread → 发送循环 | 严格 FIFO，SPSC | SRV-D-002：512 条 / 1 MiB | 可靠积压先降速，持续超阈断开 | 断开前 flush ≤ 1 秒 |
| `MvpConnectionCommandInbox` | transport | session → 连接命令循环 | FIFO per connection epoch | SRV-D-015：64 条，ack 超时 5 秒 | 回 `QueueFull` ack 给 session | Closed 后只收 Close 并 ack |
| `MvpTransportEventOutbox` | **session（消费者拥有）** | transport → session | FIFO | 256 条 | `Closed`/`Faulted` 走**保留槽**，终态永不丢弃；非终态满载则关闭该连接并写 diagnostic | 保留槽必达 |

**故障注入点**：`ITransportFaultPolicy` 在两处各挂一次——解码后 / ingress 入队前，egress 出队后 / 写 socket 前。刻意与 LumioClient 的 `FaultDecoratingTransport` 位置对称，使双端故障脚本共用同一 `TransportFaultContext{Seed, Sequence}` 口径。**在组装期注入**（修正 LumioClient 硬编码 `PassThroughFaultPolicy` 的缺陷）；生产 Profile 固定 `PassThroughFaultPolicy`，注入实现只存在于 `TestKit`。

**模块红线**：`Transport` **绝不依赖 Auth**（Rust 侧原文：「transport 绝不依赖 auth；`PermissionGrantRef` 是 transport 自有 opaque value」）；网络线程不得调用 Gameplay，只有 world-slot 在 Tick Barrier 消费 ingress。

### 6.2 auth 存根（简单 token，不触 D-011）

**Host 私有 vs 绝不定义成公共 wire：**

| 本仓可自由定义（Host 私有，永不跨 wire） | 绝不在本仓定义（动它 = 解冻 D-011/D-009 或改 Baseline） |
|---|---|
| `ServerConnectionSession` 注册表（连接身份、接纳结果、重连保留、Slot 关联、连接 epoch） | replication Envelope 的 12 个字段与全部枚举 |
| 防重放窗口与 nonce 记录 | `transportPolicy.authBinding` 的两个值 |
| 已验证身份缓存、不可变授权对象 | **Handshake body 的字段集（冻结为仅 `role`）** |
| 凭据验证材料的装载方式 | 任何凭据/票据/nonce/rotation 的**线格式** |
| 通道认证（WSS 建连阶段）如何携带与校验 MVP token | ErrorCode 词表（43 个已注册值） |
| 审计事件 `fields` 的自定义内容 | protocol-permission-gate 的 15 字段与 7 个 rejectReason；`ClientReplicaSession` 状态机 |

**token 落点（定死）：只在 WSS 通道认证层。** v1.4 §7.3 与 D-012 都把「通道认证」与「完整 Handshake」并列为两个独立步骤，MVP token 属前者。承载形式：WebSocket 升级请求的
`Sec-WebSocket-Protocol: lumio.mvp.v0, <opaqueTokenB64Url>, <opaqueNonceB64Url>`，服务端接受时回选 `lumio.mvp.v0`。

选子协议而非 `Authorization` 头的理由：**浏览器 WebSocket API 不能设自定义头但能设子协议**，而 MVP 的最终形态是浏览器体素客户端（A2），这个选择让将来切浏览器时不需要改认证承载。两个串在 WebSocket 握手完成前就被消费并丢弃，**永不进入任何 Envelope 字段**；名字里的 `mvp` 与 `v0` 是刻意的退场标记。本设计**不定义**凭据 blob 的内部格式、算法、轮换或 nonce 派生——只规定 D-011 已冻结的行为契约：**准入前必须先过防重放**。

**该承载的定性与登记（不可省略）：** 它是「公共面缺位期的私有约定」，与 §5.7 的 `length` 口径同一处置模式，适用同一条**退场纪律**——架构源一旦冻结通道认证的凭据承载方式，本仓即改用公共形态并删除子协议位序约定，名字里的 `mvp` 与 `v0` 就是这条纪律的可见标记。它与被删除的 body 私有字段的**原则性区别**见 §5.6-C 的对照表（一句话：token 落在**任何冻结公共产物之外**且 MVP 章程显式授权 auth 存根；body 字段落在**受 Accepted ADR-028 治理的冻结 typed body 之内**且无任何授权）。三处登记齐备：`absences.json` 的 `ABS-AUTH-CREDENTIAL-CARRIAGE`（clause = 通道认证凭据与 nonce 的线承载无公共定义，MVP 取 `Sec-WebSocket-Protocol` 子协议位序；source = `docs/architecture/DECISIONS_PENDING.md` D-011；reason = `决策门冻结`；successor = `needs-new-card`）、§11 的 **G18**、§12.1 的 **B10**。卡 10 与卡 12 的验收各有一条引用该 ABS id。

**最容易踩的违规点，设计必须正面挡住。** schema 的 `body` 无 `additionalProperties: false`，验证器只检查 `Handshake` body 存在 `role`——**往 Handshake body 里塞 token/nonce 会同时通过 JSON Schema 与 `lumio_contract.py` 全部校验，机器拦不住。** 处置：§5.4 的私有更严规则把「`Handshake` body 字段集必须恰好等于 `{role}`」写成机器断言，把 D-011 违规从纪律降级为编译进测试的门禁。

```csharp
public readonly record struct AuthRequestId(ulong Value);
public readonly record struct PrincipalId(string Value);
public readonly record struct GrantEpoch(ulong Value);

public sealed class OpaqueCredentialInput : IDisposable { }   // 禁 ToString/Equals/序列化；不进日志/fixture/卡片
public readonly record struct VerificationContext(string ProductId, string GameReleaseId, string Nonce, MonotonicInstant ReceivedAt);
public enum CredentialVerdict { Accepted, Rejected }
public readonly record struct CredentialVerification(CredentialVerdict Verdict, PrincipalId Principal, string? AuditReason);

public interface ICredentialVerifier {   // adapter SPI —— D-011 的合法落点；不成为 wire 标准
    CredentialVerification Verify(OpaqueCredentialInput credential, in VerificationContext context);
}
// MVP 唯一实现：InjectedExactByteCredentialVerifier —— 启动期载入共享密钥，常量时间 exact-byte 比对

public enum AntiReplayVerdict { Ok, Replayed, OutOfWindow }
public interface IAntiReplayWindow { AntiReplayVerdict Check(PrincipalId principal, string nonce, MonotonicInstant receivedAt); }

public sealed record PermissionGrant(                     // 不可变；派生后不可修改
    PrincipalId Principal, string Role, ImmutableArray<string> Claims,
    ImmutableArray<string> AllowedMessageTypes, GrantEpoch Epoch, MonotonicInstant ExpiresAt);

public interface IAuthorizationService {
    AuthenticateOutcome Authenticate(in AuthenticateCommand command);   // 在 session 编排路径上同步执行
    PermissionGrant     Authorize(PrincipalId principal, in SessionScope scope);
    AckResult           EvaluateMessagePermission(in MvpPermissionGateRequest request);  // gate 执行体
    bool                AdmissionMustStop { get; }   // Audit 背压时为真：编排层据此停止接纳新连接
}
```

后两个成员的存在理由：§4.1 把「gate 执行体」划归 `Auth`，而调用方是 `Session`；两者同为 Layer 4/5 且**相互零引用**，因此调用只能经 `HostContracts`（Layer 2）的这个接口，实例在 `App` 组装期接线。`AdmissionMustStop` 是 §6.2「Audit 队列背压时认证结果不得静默放行」这条安全红线的机器化出口——它让「停止接纳」成为编排层必须读的一个值，而不是一句纪律。

Rust 侧设计已给出这个存根的合法落点：`modules/auth/src/adapters/injected.rs`，其职责原文是「仅测试/集成用 exact-byte verifier，**不成为 wire 标准**」。MVP 只是把同一个位置在 C# 侧实现，不新开面。

**auth 无自有运行期状态机**（身份缓存条目生命周期 `Validated → Active → Expired/Revoked`）；**无自有线程**——认证在 session 编排路径上同步执行，既不在 WS 接收循环、也不在 Simulation Owner Thread 上。

**防重放与 admission 绑定**：窗口 provisional SRV-D-005 = 30 秒 + 单调 nonce，键为 `(PrincipalId, nonce)`；命中 → `SessionAntiReplay` 拒绝并写审计；连续命中发类型化信号 `ReplayStorm`，按 SRV-D-006 把该来源配额减半。**凭据无效不消耗防重放窗口配额。** 顺序固定：WSS 升级 → 通道认证（verifier + 防重放）→ 接纳连接 → 收 Handshake Envelope → session Admission saga。授权对象在接纳时派生，**重连必须重新派生**（SRV-D-013）；撤销走连接 epoch 递增。

**错误语义（安全红线）**：

| 类 | 场景 | 动作 |
|---|---|---|
| **可重试** | **无**——认证裁决从不重试 | — |
| 可拒绝 | 凭据无效、票据过期、重放、权限不足、Release 不匹配 | 见下「错误码诚实处置」 |
| 可致命 | 验证材料损坏或缺失（启动期发现） | **进程拒绝启动，不降级放行** |

**不存在任何认证降级路径**：「跳过认证」在生产 Profile 中不可表达——`InjectedExactByteCredentialVerifier` 只在 `TestKit` 与显式 dev Profile 下可被组装，且组装点有一条 `Architecture.Tests` 断言。**Audit 队列背压时认证结果不得静默放行**，由编排层停止接纳新连接（`MvpAuditQueue` 达阈即请求 world-slot 关闸）。

**★ 错误码诚实处置（本轮实测发现）**：`ids/index.json` 的 43 个 ErrorCode 中**没有任何一个表示「凭据无效」**（全表已核对：`RevisionConflict, MaintenanceKick, ReleaseMismatch, NativeAbiMismatch, StaleEpoch, FencingTokenStale, ManifestMalformed, ManifestUnsupportedVersion, ManifestDigestMismatch, ArtifactMissing, ArtifactDigestMismatch, SignatureMissing, SignatureInvalid, TrustRootUnknown, TrustPolicyRejected, KeyRevoked, EvidenceMissing, EvidenceDigestMismatch, TargetProfileMismatch, CapabilityMissing, SymbolMissing, SymbolCollision, PackageIdentityConflict, WorkerPoolDuplicate, LoaderTimeout, LoaderCancelled, LoaderOutOfMemory, PartialLoadRolledBack, InvalidHandle, HandleDoubleRelease, MessagePermissionDenied, StaleConnectionGeneration, ChunkUnavailable, TargetRevisionUnavailable, BudgetExceeded, QueueFull, CoordinateOutOfBounds, DirtyChunkNotDurable, SnapshotBaseMismatch, SessionMismatch, RoleMismatch, ClaimNotGranted, SessionAntiReplay`）。因此 MVP **不为通道认证失败发任何 Envelope `Error`**——在 HTTP 升级阶段以 WebSocket close `1008` 拒绝，并写一条 Audit 事件。只有存在已注册码的场景才发 Envelope `Error`：`SessionAntiReplay` / `ReleaseMismatch` / `RoleMismatch` / `ClaimNotGranted` / `MessagePermissionDenied` / `StaleConnectionGeneration` / `SessionMismatch` / `StaleEpoch` / `QueueFull` / `MaintenanceKick`。登记为 BLOCKED B3。

**审计**：拒绝事件照抄 `fixtures/valid/logging-auth-reject-audit.json` 的形状——`category=Audit`、`severity=Warn`、`correlation.scope="Release"`、带 `releasePoolId`、**不伪造 `sessionId`**（session 尚未创建）、`durability="Durable"`、`redaction="Applied"`。`correlation.scope` 的 REQUIRED / FORBIDDEN 两张表（ADR-011）在 `Observability` 内机器强制：Release scope 出现 `sessionId` 即断言失败。**凭据/token 不得进日志、fixture、任务卡或 prompt。**

**队列**：`MvpAuthRequestQueue`（所有者 auth，生产者 session，FIFO per connection epoch，容量 32，满载返回 `AuthBusy`（映射 `QueueFull`）由 session 决定重试/关闭）；`MvpAuthEventQueue`（**所有者 session**，生产者 auth runner，FIFO per request id，容量 64，满载写 diagnostic emergency，**不得丢成功 ack**）。

### 6.3 session

```csharp
public readonly record struct ServerSessionId(string Value);
public readonly record struct SessionEpoch(ulong Value);
public readonly record struct AdmissionAttemptId(ulong Value);
public readonly record struct ReplicationContextHandle(ulong Value);   // opaque handle

public enum ServerConnectionSessionState { Admitted, Syncing, Active, ReconnectWindow, Expired, Closed, Kicked, Faulted }

/// 该类型名称固定；禁止别名为 ClientReplicaSession，禁止与其做状态映射（ADR-001）。
public sealed class ServerConnectionSession {
    public ServerSessionId SessionId { get; }
    public SessionEpoch SessionEpoch { get; }
    public ServerConnectionSessionState State { get; }
    public SessionBinding? Binding { get; }                    // TransportConnectionId + ConnectionEpoch + PermissionGrantRef + SlotAssociation
    public ReplicationContextHandle? ReplicationContext { get; }
    public string ProductId { get; }                            // 准入时固定，此后不可变
    public string GameReleaseId { get; }                        // 准入时固定，此后不可变
}

public abstract record SessionCommand { ConnectionCandidate / DependencyResult / BeginDrain / Kick / TimerFired / SlotFaulted }
public abstract record SessionEvent   { Admitted / Rejected / Disconnected / Reconnected / Drained / Kicked / Faulted }
public interface IAdmissionReducer {   // 纯状态机，只生成下一条 typed effect，不做 IO
    AdmissionStep Advance(in ServerConnectionSessionState state, in SessionCommand input);
}
public interface ISessionAdminPort {   // 测试控制面（仅回环 + 需 --enable-test-control + 每次写 Audit）
    AckResult BeginDrain(MonotonicInstant graceDeadline);
    AckResult Kick(ServerSessionId sessionId, string registeredReasonCode);
    AckResult InjectWorldMutation(ServerSessionId onBehalfOf, ReadOnlyMemory<byte> opaqueCommand);
    // ↑ A1-α 用（§9）。实现把不透明字节投进 HostContracts 的 IWorldMutationSink（§6.5），
    //   不构造任何 Envelope；Session 因此不需要引用 WorldSlot / Simulation.Reference。
}
```

**状态机**：`Admitted → Syncing → Active`；`Active → ReconnectWindow`；`ReconnectWindow → Syncing`（重连成功）/ `→ Expired`（窗口超时）；任一状态 `→ Closed`（正常关闭）/ `→ Kicked`（维护/管理）/ `→ Faulted`（`SessionLocalProven` 隔离）。迁移**只能由 session 发起**，Runtime/Gameplay 回调不能改变它；`Syncing` 只表示等待 Runtime 完成事件，**不复制 Runtime 内部状态**。

MVP 落地：`Admitted / Syncing / Active / ReconnectWindow / Expired / Closed / Kicked` **全部实到**（`Kicked` 必须实到——它是 A1 服务端主动断连的唯一触发入口）。`Faulted` **建模但 MVP 期不可达**（参考存根恒不产生 `SessionLocalProven` 见证），在 `absences.json` 登记，**不得从状态机删除**。

**ADR-001 合规（机器断言）**：`Architecture.Tests` 断言 `Lumio.Server.MvpHost.Session` 程序集内不存在名称含 `ClientReplicaSession` 的类型或成员。唯一允许出现该字面量的地方是 `protocol-permission-gate` 的 `antiReplay.sessionScopeOwner` 常量值（公共 schema 要求）。

**Admission saga（八步形状全保留；缺席方以稳定拒绝或固定配置比对填补，不删步）**：

1. transport 首包结构校验（长度 / `protocolVersion` / 完整性 / 大小上限）——已在 Transport 完成，以 `ConnectionCandidate` 送达。
2. session **先读** Host Admission Gate（`world-slot` 所有，session 只读）；关闭即稳定拒绝并附剩余宽限信息。
3. auth：通道认证结果核对 + 防重放判定 + `Authorize` 派生**不可变** `PermissionGrant`；失败写 Audit（Release 作用域，不伪造 `sessionId`）。
4. **ExactRelease 匹配**：Envelope 的 `productId` + `gameReleaseId` 与宿主配置的**单一**二元组精确比对；不匹配 → `Error{errorClass:"Rejectable", reasonCode:"ReleaseMismatch"}` 后关闭。**D-007 默认拒绝 N/N-1**，不实现任何降级匹配。*absence-filler*：完整语义（Catalog 消费 / Manifest 校验 / Pool 成员健康）缺席，承接卡 `implement-release-catalog-manifest-verification`、`implement-release-local-member-state-health-and-reporting`。
5. world-slot 容量裁决：`ReserveAdmission` → `CommitAdmission`（`BindSession`）。
6. 固定 `productId + gameReleaseId`，创建 `ServerConnectionSession` + 不透明 `ReplicationContextHandle`（**只存 opaque handle**）。
7. `BindConnection`：把不可变授权对象（`PermissionGrantRef`）经类型化命令交 transport，等显式 ack。
8. 复制序列：`FullSnapshot` → 等 `BaselineAck` → `Delta` 流。

**saga 硬约束**：每步 effect 带 `AdmissionAttemptId` + connection/session/slot epoch 与**显式 ack**；任一点失败**恰好执行一次**补偿（`AbortAdmission` / `Unbind` / grant 撤销 / 删除会话记录）；只有 auth grant 成功、ExactRelease 通过、slot commit、transport bind **全部 ack** 后才进入 `Active`；session **不写** ConnectionRegistry、**不写** Admission Gate，只发类型化命令并等 ack；所有旧 connection/session epoch 的迟到 completion 一律拒绝。

**重连窗口（D-012 硬约束）**：SRV-D-004 provisional 120 秒，MVP 测试 Profile 覆写 10 秒（标注 provisional override 并登记）。`Closed` 事件 → `Active → ReconnectWindow`，**只保留 `ServerConnectionSession` 元数据 + `ReplicationContextHandle`，不保留任何认证状态**，经 `ITimerService` 排一条 `TimerFired`。重连时**新连接代次必须重做通道认证 + 完整 Handshake**，经 auth 重校验并**重新派生**授权对象，然后 `ReconnectWindow → Syncing` 并下发**全新 FullSnapshot**（MVP 不保留有效 Baseline）。**不实现任何 Session Resume Token 快捷路径。** 同连接内 Resync 走 `Gap → ResyncRequest → FullSnapshot`，**不重新握手**——两条路径在实现与测试上严格分开。窗口到期与重连的竞争在 `MvpSessionControlInbox` 上**串行裁决**，以先到达的类型化命令为准，输者收稳定错误。

**Drain / Kick**：`BeginDrain` → 请求 world-slot 关闭 Gate → 停止接纳 → 逐 session 通报 → 上报进度。`Kick` → 下发 `MaintenanceKick` Envelope（`body.reasonCode = "MaintenanceKick"`）→ transport `Close(MaintenanceKick)`。`ISessionAdminPort` 仅绑定回环、需显式 `--enable-test-control` 开关、生产 Profile 的配置 schema 中**不可表达**（依据 `.spec/rules/system.md`「dev-only 开关 / 调试后门不得在生产开启」），且每次调用写 Audit。

**错误分类**：可重试 = `AuthBusy`（映射 `QueueFull`）/ `AggregateBusy`（映射 `QueueFull`）类瞬时忙（saga 重投，受 attempt 预算约束）。**两者都是模块内部枚举成员，不是 `StableErrorId`**：`AuthBusy` 是 `Auth` 的 `AuthQueueAdmission` 成员、`AggregateBusy` 是 `WorldSlot` 的入队结果成员；一旦需要对外表达（进 `AckResult.StableErrorId` 或 `Error.body.reasonCode`），一律映射为已注册的 `QueueFull`。`ids/index.json` 的 43 个 ErrorCode 中两者均不在册（实测），**MVP 不发明任何新错误码**这条红线因此不被破坏；由 `Architecture.Tests` 的全构建图断言 `AllStableErrorIdsAreRegisteredTest` 覆盖所有工程的 `StableErrorId` 常量与出站 `reasonCode`（卡 6）。可拒绝 = Gate 关闭 / 认证失败 / `ReleaseMismatch` / `RoleMismatch` / `ClaimNotGranted` / 容量不足 / 旧 epoch（`StaleEpoch`）。可致命 = 注册表不变量破坏（重复 sessionId、绑定悬挂）/ reducer 进入不可达状态 → 进程退出。

**队列**：`MvpSessionControlInbox`（所有者 session，生产者 transport/auth/world-slot/timer，FIFO per session/connection epoch，容量 256，**握手前满载 → 关闭该连接；活动 session 满载 → 隔离该 session**，shutdown 后只处理 close/ack 并拒绝新 admission）；`MvpSessionEventOutbox`（所有者为下游消费者，关键终态**保留槽**，无法交付则隔离该 session 并发 diagnostic）。

### 6.4 world-slot

```csharp
public readonly record struct WorldSlotId(ulong Value);
public readonly record struct SlotEpoch(ulong Value);
public readonly record struct SlotReservationId(ulong Value);
public readonly record struct SnapshotCutRef(ulong Value);
public enum AdmissionGateState { Open, Closed }

public enum WorldSlotHostState {   // 13 态，逐字取自 fixtures/valid/state-machine-world-slot-host.json
    Allocated, Bootstrapping, NativeReady, ManagedReady, LoadingSession, Running,
    Quiescing, Snapshotting, Reloading, Migrating, Stopping, Destroyed, Faulted }

public abstract record WorldSlotCommand { ReserveAdmission / CommitAdmission / AbortAdmission / Quiesce / Stop / TickPermit / DependencyAck }
public abstract record WorldSlotEvent   { AdmissionReserved / AdmissionRejected / SessionAssociated / TickCompleted /
                                          Quiesced / GateStateChanged / FaultAdjudicated / ReadyToStop }

public interface IWorldSlotHost {
    AllocateResult   Allocate(in SlotBudget budget);
    AckResult        BindSession(SlotReservationId reservation, ServerSessionId session, SlotEpoch epoch);
    AckResult        Quiesce(string reason, SlotEpoch epoch);      // 返回带 epoch 的进度 ack 流
    SnapshotCutRef   FixSnapshotCut(SlotEpoch epoch);
    AckResult        Destroy(SlotEpoch epoch);
    AdmissionGateState Gate { get; }                                // ★ 唯一所有者；session 只读
    QuotaView        Capacity { get; }
    AckResult        ReportFault(string registeredErrorCode, HostFaultClass faultClass, SlotEpoch epoch);
}
public interface IFaultAdjudicator { FaultAdjudication Classify(HostFaultClass? witness); }  // null → SlotStateUnproven
```

**状态机真值取 fixture，不取生成表（口径冲突的裁决）**：
- `Lumio.Gen.ContractTypes.StateTransitionTable` 中 WorldSlotHost 只有 **15 条前向迁移，不含任何 Faulted 边**。
- `fixtures/valid/state-machine-world-slot-host.json`（本轮实测已 cat 全文）含 **13 states**、`terminalStates: ["Destroyed","Faulted"]`、`anyActiveTo: ["Faulted"]`，并附注记「Faulted is fail-stop per ADR-027: the slot never resumes; recovery is a new slot restoring from durable records.」
- **裁决：fixture 是状态机真值；`StateTransitionTable` 只作前向迁移的交叉校验。** 以生成表为唯一真值做表驱动，会漏掉 Faulted 语义，直接违反 world-slot「缺见证一律 `SlotStateUnproven`」的裁决义务。

**C# 侧必须是两份独立的集合，不是一份。** 本轮实测 fixture：`transitions` 恰 15 条且**不含任何 `to == "Faulted"` 的边**；13 个 state 里 `terminalStates` 是 `["Destroyed","Faulted"]`，因此活动态 11 个，`anyActiveTo: ["Faulted"]` 展开后是**另外 11 条边**。若 C# 只声明一份迁移集合，「== fixture 的 15 条」与「每个活动态都有到 Faulted 的边」两条断言不可能同时为真——实现者为了让前者通过就会把 Faulted 边从模型里删掉，正好丢掉本节特意保下来的 fail-stop 语义。定死为：

- **前向迁移表** `ForwardTransitions`：15 条 `(from, to, event)`，与 fixture `transitions` 逐条相等，**不含 Faulted 边**。
- **`anyActiveTo` 规则** `AnyActiveToFaulted`：一条覆盖全部 11 个活动态的规则（不是 11 条硬编码边），语义为「任一非终态 → `Faulted`」。

`WorldSlot.Tests` 在运行时装载镜像 fixture，做六条机器断言：(a) C# 枚举 13 个成员名 == fixture `states` 数组；(b) C# 的**前向迁移表** == fixture `transitions` 的 15 条（逐条 `(from, to, event)` 相等，两侧都不含 Faulted 边）；(c) `Destroyed` / `Faulted` 无出边；(d) C# 的 **`anyActiveTo` 规则**覆盖 fixture `anyActiveTo` 展开后的每一个活动态 → `Faulted`（11 个活动态逐个可达 `Faulted`），且该规则**独立于**前向迁移表；(e) C# 前向迁移表与 `StateTransitionTable.All` 过滤 WorldSlotHost 后的 15 条逐条一致；(f) `initialState == "Allocated"`。

**MVP 实际驱动的 8 条前向迁移 + 1 条终态边**：`BeginBootstrap`、`NativeLoaded`、`ManagedLoaded`、`LoadSession`、`SessionLoaded`、`Quiesce`、`Stop`、`TeardownComplete`，加 `anyActiveTo: Faulted`。

**MVP 合法「暂不进入」的 7 条**（定义保留，**不删不改**）：`Resume`、`BeginSnapshot`、`SnapshotComplete`、`BeginReload`、`ReloadComplete`、`BeginMigrate`、`MigrationHandedOff`。判据是「MVP 未实现的是这些迁移的**触发方**（maintenance-agent / persistence-host / control-plane-adapter），而非状态机本身」。

**`NativeReady` 不可跳过**：它是通往 `ManagedReady` 的唯一中间态。MVP 走 PureHeadless / NoNative 无 Loader 路径，以「无 Native 可加载」的**显式空实现**穿过并在 `absences.json` 声明，不得删除该状态。

**`Faulted` 是 fail-stop 终态（ADR-027）**：**不得设计任何从 `Faulted` 回到活动态的迁移**；MVP 遇 `Faulted` 直接终止 Slot 并退出进程，重启即新 Slot；恢复只能表达为「新 Slot 从 durable records 重建」。（注：本仓 `modules/world-slot/README.md` 是 v1.2 表述「Slot 转 Faulted，从最近有效 Snapshot 恢复」，与 v1.4 的 ADR-027 冲突——见 §10 known gap G7，本轮不改 README。）

**聚合根五项收权**：① Admission Gate 唯一所有者，开/关只能由本模块发起并广播 `GateStateChanged`；② 生命周期 epoch，旧 epoch 命令/ack 一律 `StaleEpoch` 拒绝；③ Quiesce/Drain/Snapshot/Stop 原子序列；④ pacing 启停（pacing 不接受任何其他模块的暂停/恢复指令）；⑤ FaultClass 裁决。其他子组件可持内部状态但**不得发起聚合迁移**。

**Quiesce 原子序列**：单一序列，顺序固定 **关闭 Gate → 排空/记录在途 → 固定 SnapshotCut → 暂停 pacing → 停止**，逐步回带 epoch 的 ack：`AdmissionClosed → Drained → SnapshotCut → Stopped`。**任一步失败进入 `Faulted`，不留半完成状态。** 序列内部保证 Gate 先于停 Tick，顺序不依赖外部编排纪律。MVP 澄清：「固定 SnapshotCut」只在内存记录 `SnapshotCutRef`，**不进入 `Snapshotting` 状态**（那需要缺席的 persistence-host）。

**Owner thread 与 Tick 链**：每 Slot 一条具名线程 `worldslot-{id}`（经 `Platform` 受监督创建），是**唯一**触碰仿真状态的线程。固定链：
`TimerFired → pacing decision → TickPermit(SPSC, cap 1) → Owner Thread → 有界 ingress drain（每 tick 上限 64 条 / 64 KiB）→ IWorldSimulationPort.RunTick → egress typed effects`。

**故障分级判定顺序**：`SessionLocalProven` → 向 session 下发该 Session 的隔离终结命令、Slot 继续服务；`SlotStateUnproven` → Slot 转 `Faulted`（fail-stop，进程退出）；`ProcessFault` → 转交 process。**缺 FaultClass 见证的捕获故障一律按 `SlotStateUnproven` 从严处理**（`Classify(null)` 即返回它）。**Host 永不从「异常是否被捕获」推断故障域。** **连接级故障（畸形 / 超限 / 限流 / 认证失败）只断该连接，不上升为 Slot 或进程故障**——这符合故障域分层，不是绕过，也是 MVP 期唯一避免「任何故障都杀 Slot」的合法出口。MVP 现实：参考存根恒返回 `HostFaultClass.None`（一个**正向**的「本 tick 无故障」见证，不是缺席）；抛异常或该字段为 `null` 时走 `null → SlotStateUnproven` 路径。`HostTickOutcome.FaultClass` 因此是**可空**枚举（§6.5）——非空枚举的 `default` 是 `None`，会让「忘了填见证」静默变成「证明无故障」，恰好绕过本段的从严红线。

**错误分类**：可重试 = `AggregateBusy`（收件箱满，映射 `QueueFull`，调用方退避重投）。可拒绝 = Gate 关闭、配额不足、旧 epoch（`StaleEpoch`）、`Destroyed` 后的命令。可致命 = Quiesce 序列任一步失败 / Owner Thread 异常终止 → `Faulted` → 进程退出。

**队列**：

| 队列 | 所有者 | 生产者→消费者 | 顺序 | 容量 | 满载 | 关闭 |
|---|---|---|---|---|---|---|
| `MvpWorldSlotAggregateInbox` | world-slot | session/timer/pacing → 聚合命令循环 | FIFO by (slot epoch, sequence) | 64 + **2 个保留槽给 Quiesce/Stop** | 回 `AggregateBusy`（映射 `QueueFull`） | stop 后只收终态查询与迟到 ack，其余拒绝 |
| `MvpTickPermitQueue` | world-slot | pacing timer → Owner Thread | 严格 FIFO SPSC | 1 | pacing 记 overrun，**不堆积 catch-up** | 停止 pacing 后关闭 |
| `MvpSlotEventOutbox` | 下游 consumer | world-slot → session / process | FIFO | 256 | 终态保留槽 | 保留槽必达 |

### 6.5 宿主↔Runtime 端口 `IWorldSimulationPort`

```csharp
public readonly record struct HostSessionId(string Value);
public readonly record struct HostWorldSlotId(ulong Value);
public readonly record struct LogicalTickToken(ulong Value);
public readonly record struct WireFrame(ReadOnlyMemory<byte> Bytes);

public enum HostSimulationState { Created, Initialized, Ready, Running, Paused, Draining, Snapshotted, Disposed, Faulted }
// ids/index.json 的 FaultClass 命名空间恰 3 值（实测）：SessionLocalProven / SlotStateUnproven / ProcessFault。
// None 是**本仓私有的第 4 值**，只表示「本 tick 有正向见证且无故障」，绝不跨 wire、绝不进任何 reasonCode。
public enum HostFaultClass      { None, SessionLocalProven, SlotStateUnproven, ProcessFault }
public enum HostTickStatus      { Completed, Rejected, Faulted }

public readonly record struct HostTickRequest(LogicalTickToken Tick, ReadOnlyMemory<WireFrame> Ingress, ulong DeterministicSeed);
// FaultClass 必须可空：null = 「无见证」，None = 「有正向见证且无故障」。两者在类型上必须可区分——
// 非空枚举的 default 是 0 == None，会让「忘了填」静默变成「证明无故障」，绕过 ADR-006
// 「A caught failure without a FaultClass attestation defaults to SlotStateUnproven」这条从严红线。
public readonly record struct HostTickOutcome(
    HostTickStatus Status, LogicalTickToken Tick, ReadOnlyMemory<byte> StateHash,
    ulong AuthorityRevision, ReadOnlyMemory<WireFrame> Egress, HostFaultClass? FaultClass, string? StableErrorId);

public interface IWorldSimulationPort : IDisposable {
    HostSimulationState State { get; }
    HostLifecycleResult Initialize(in HostSessionInit init);
    HostLifecycleResult Ready();
    HostTickOutcome     RunTick(in HostTickRequest request);   // ★ 每逻辑 Tick 只此一次跨界调用
    HostLifecycleResult Drain();
    HostLifecycleResult Snapshot(out ReadOnlyMemory<byte> opaqueSnapshot);
}
```

**★ 带外世界变更入口的裁决：`IWorldSimulationPort` 不加成员，改由 `HostContracts` 定义 `IWorldMutationSink`。**

A1-α 需要一条带外通道把测试控制面注入的不透明变更送进仿真（§9.1 步骤 8）。三条既有约束叠加后，`Session` 在编译期拿不到 `ReferenceWorldSimulation` 的具体类型：`Session` 的 `ProjectReference` 恰为 `HostContracts` + `Observability`（不引用 `WorldSlot` / `Simulation.Reference`）；`Simulation.Reference` 在生产侧的被引用计数**恰为 1**（只有 `App`）；`IWorldSimulationPort` 的成员只有 6 个。裁决：

```csharp
namespace Lumio.Server.MvpHost.HostContracts;   // Layer 2

// 带外世界变更汇聚端口。不经任何 Envelope、不经 RunTick 的 Ingress，只在 --enable-test-control 下被装配。
// 实现方（Simulation.Reference）把变更放进自己的有界队列，由 Owner Thread 在下一次 RunTick 内应用——
// 因此本端口**不违反**「每逻辑 Tick 恰好一次跨界调用」，也不破坏「Owner Thread 是唯一触碰仿真状态的线程」。
public interface IWorldMutationSink { EnqueueResult TryEnqueueOpaqueMutation(ReadOnlyMemory<byte> opaqueCommand); }
```

- **依赖方向**：`HostContracts`（Layer 2）定义接口 → `Simulation.Reference`（Layer 4，已引用 `HostContracts`）由 `ReferenceWorldSimulation` 实现它 → `App`（Layer 6）持有具体类型并把它作为 `IWorldMutationSink` 注入 `SessionRegistry.Create` → `Session`（Layer 5）只见 `HostContracts` 的接口。四条既有约束**一条都不用改**：`IWorldSimulationPort` 仍是 6 个成员、`RunTick` 仍只有一个重载、`Simulation.Reference` 生产侧被引用计数仍恰为 1、`Session` 的 `ProjectReference` 仍恰为两个。
- **为什么不选「给 `IWorldSimulationPort` 加一个成员」**：那要给「四条不可协商约束」开一个例外，且把一个只在 dev 开关下存在的入口焊进宿主与 Runtime 之间的冻结端口——将来换成真 Runtime 时它是纯负债。
- **队列登记**：`Simulation.Reference` 目录下新增 `queues.json`，登记 `MvpWorldMutationInbox`（所有者 `Simulation.Reference`，生产者测试控制面线程，消费者 Slot Owner Thread，FIFO，容量 provisional 32，满载回 `EnqueueStatus.Full` 且 `StableErrorId = "QueueFull"`，关闭后拒绝新变更）。
- **`--enable-test-control` 未给出时**，`App` 根本不构造该 sink，`SessionRegistry.Create` 收到 `null` 并使 `ISessionAdminPort` 在组装图中不存在（§9.3 三重门控之一）。

**四条不可协商约束（全部机器断言）**：
1. **每逻辑 Tick 恰好一次跨界调用**；`RunTick` 只能有一个重载。
2. **签名里零 Runtime 类型**：标识用本仓 `readonly record struct` 包 ulong，载荷用不透明 `WireFrame`，`StateHash` 用不透明 `ReadOnlyMemory<byte>`，配置与快照用不透明 blob。这是「替换存根不改宿主公共面」的唯一机械保证。
3. **方法名禁用词**：不得含 `Phase` / `Clock` / `Revision` / `Commit`（与 Runtime Task 21 Step 1 同款反射测试）。
4. **生命周期九态逐字镜像** Runtime 的 `SimulationSessionState`（`Created / Initialized / Ready / Running / Paused / Draining / Snapshotted / Disposed / Faulted`），只由宿主发起迁移，**不新增、不重命名、不合并**；存根也不得定义额外状态。

**RT-D-001 不表态**：Runtime 的宿主面有两份互斥草案（`IRuntimeSession.RunTick(in TickInput)` vs `ISimulationSession.RunTick(in HostTickRequestView) + Pause/Resume/Drain(in SessionEpoch)`），决策门未批准。本仓不引用任一草案作为公共面，差异全部吸收在 `Runtime.Adapter` 工程里。

**`HostRevisionVector` 与体素无关性**：公共 Envelope 的 `FullSnapshot.body.sessionRevisionVector` 强制 7 个字段齐全且 `chunkRevisionSet` 的 key 匹配 `_CHUNK_KEY`（`lumio_contract.py:401`）的 canonical 正则 `^c:(0|-?[1-9][0-9]{0,9}):(0|-?[1-9][0-9]{0,9}):(0|-?[1-9][0-9]{0,9})$`。宿主**必须**产出它，但只作数据搬运——由 `Session` 的复制编排层从单一 `AuthorityRevision` 计数器填充（存根发一个固定 key `c:0:0:0`）。**这是冻结 schema 强制的信封字段，不是宿主自造的体素模型**；`Simulation.Reference` 对此一无所知。

**参考存根的边界**：严格是 Port 的替身语义，**不定义** chunk / block / revision（作为领域概念）/ entity / component / ability / voxel / tick-phase 任何类型（由 `Architecture.Tests` 的禁用词表机器断言）。满足 A1 的最小模型 = **不透明 key→value 覆盖表 + 单调 `AuthorityRevision`**，键值含义完全由客户端编码决定；确定性由 `DeterministicSeed` 驱动，无 wall clock。

**组装纪律（逐条照抄 Runtime `observability` 已验证范式）**：静态工厂 `Module.Create(ports)` 显式 `new`；`Services` 只读属性包；每 WorldSlot/Session 独立实例；`Dispose` 反序释放；Port 调用前双检（生命周期状态 + `view.IsWellFormed`），不合法**返回带 stable error id 的结果而非抛异常**；error id 必须存在于 `Lumio.Gen.ContractTypes.Catalog.StableErrorIds`；**无通用 DI 容器 / service locator / 全局 EventBus / Common-Utils-Globals 工程**。

### 6.6 支撑类型签名

§6.0–§6.5 的接口签名里出现、但未在那些代码块内展开的支撑类型集中定义在此。除 `ThreadStepResult` / `ThreadHandle` / `SupervisionEvent` 属 `Platform`（Layer 1）外，其余全部属 `HostContracts`（Layer 2）。所有 `StableErrorId` 字段的取值必须存在于 `Lumio.Gen.ContractTypes.Catalog.StableErrorIds`（43 值），`null` 表示无错误。

```csharp
// ── Platform（Layer 1）
public readonly record struct ThreadStepResult(bool Continue, string? StableErrorId);
public readonly record struct ThreadHandle(string Name, int ManagedThreadId);
public readonly record struct SupervisionEvent(string ThreadName, bool Faulted, string? StableErrorId);

// ── HostContracts（Layer 2）：通用 ack
public readonly record struct AckResult(bool Accepted, string? StableErrorId);

// ── transport
public readonly record struct BindEndpointResult(bool Bound, string BoundUri, string? StableErrorId);
public readonly record struct CarrierAccept(
    bool Accepted, TransportConnectionId ConnectionId, ImmutableArray<string> RequestedSubprotocols);
public readonly record struct CarrierReceive(bool Received, int ByteCount, bool EndOfMessage, bool Closed);

// ── auth
public readonly record struct SessionScope(
    ServerSessionId SessionId, string ProductId, string GameReleaseId, string Role);
public readonly record struct AuthenticateCommand(
    AuthRequestId RequestId, TransportConnectionId ConnectionId, ConnectionEpoch ConnectionEpoch,
    OpaqueCredentialInput Credential, VerificationContext Context);
public readonly record struct AuthenticateOutcome(
    CredentialVerdict Verdict, PrincipalId Principal, AntiReplayVerdict AntiReplay,
    string? StableErrorId, string? AuditReason);

// ── session
public readonly record struct SessionBinding(
    TransportConnectionId ConnectionId, ConnectionEpoch ConnectionEpoch,
    PermissionGrantRef Grant, WorldSlotId Slot, SlotEpoch SlotEpoch);
// 八步 saga 的 effect 枚举，逐条对应 §6.3 的 1..8 步，外加补偿与拒绝两个终止效果
public enum AdmissionEffectKind {
    None, ReadGate, Authenticate, MatchExactRelease, ReserveSlot, CommitSlot,
    CreateSession, BindConnection, StartReplication, Compensate, Reject }
public readonly record struct AdmissionStep(
    AdmissionEffectKind Effect, AdmissionAttemptId Attempt,
    ServerConnectionSessionState NextState, string? StableErrorId);

// ── world-slot
public readonly record struct SlotBudget(
    int MaxSessions, int MaxIngressItemsPerTick, long MaxIngressBytesPerTick);
public readonly record struct AllocateResult(
    bool Allocated, WorldSlotId SlotId, SlotEpoch Epoch, string? StableErrorId);
public readonly record struct QuotaView(int MaxSessions, int BoundSessions);
public readonly record struct FaultAdjudication(
    HostFaultClass FaultClass, bool SlotMustFailStop, bool SessionMustIsolate);

// ── IWorldSimulationPort 与带外变更端口（§6.5）
public interface IWorldMutationSink { EnqueueResult TryEnqueueOpaqueMutation(ReadOnlyMemory<byte> opaqueCommand); }
public readonly record struct HostSessionInit(
    HostSessionId Session, HostWorldSlotId Slot, ReadOnlyMemory<byte> OpaqueConfig, ulong DeterministicSeed);
public readonly record struct HostLifecycleResult(
    bool Accepted, HostSimulationState State, string? StableErrorId);
```

```csharp
// ── Platform 的具体构造入口（组装根显式 new 时使用；§6.5 的「静态工厂 Module.Create」范式）
public static class PlatformModule {
    public static IMonotonicClock        CreateClock();
    public static IWallClock             CreateWallClock();   // 全仓唯一墙钟出口（§6.0）
    public static ITimerService          CreateTimerService(IMonotonicClock clock);
    public static IBoundedInbox<T>       CreateInbox<T>(in QueueBudget budget);
    public static IBoundedOutbox<T>      CreateOutbox<T>(IBoundedInbox<T> target);
    public static INamedThreadSupervisor CreateThreadSupervisor();
}

// ── Observability（Layer 3）：Audit 与 Diagnostic 两条有界写入面
public readonly record struct CorrelationView(
    string Scope, string ProductId, string GameReleaseId, string? ReleasePoolId, string? SessionId,
    string? WorldId, ulong? TickId, string? TxnId, string TraceId, string ProducerId, ulong EventSeq);
// EventId / Timestamp 是 logging-event.schema.json 的 required 成员（实测 required 7 项、
// additionalProperties:false），缺任一项即产不出合法事件。EventId 匹配 common #/$defs/id
// （^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$），由 Observability 内部按 "event-{producerId}-{eventSeq}" 生成；
// Timestamp 匹配 common #/$defs/timestamp，唯一来源是 Platform 的 IWallClock（§6.0）。
public readonly record struct AuditRecord(string EventId, string Timestamp,
    CorrelationView Correlation, string Category, string Severity,
    string Durability, string Redaction, string Message, string? ReasonCode);
public readonly record struct DiagnosticRecord(string EventId, string Timestamp,
    CorrelationView Correlation, string Category, string Severity, string Message);

public interface IAuditWriter {
    EnqueueResult WriteReleaseScopedReject(string releasePoolId, string productId, string gameReleaseId,
        string traceId, string producerId, ulong eventSeq, string reasonCode);   // 不带 sessionId（ADR-011）
    EnqueueResult WriteSessionScoped(ServerSessionId sessionId, string productId, string gameReleaseId,
        string traceId, string producerId, ulong eventSeq, string message);
}
public interface IDiagnosticWriter { EnqueueResult Write(string category, string severity, string message); }

public sealed class ObservabilityServices {
    public IAuditWriter      Audit { get; }
    public IDiagnosticWriter Diagnostics { get; }
    public IHostTraceSink    Trace { get; }                  // 服务端 trace（§9.3）；生产 Profile 是 NullHostTraceSink
    public bool              IsAuditBackpressured { get; }   // 达阈即请求 world-slot 关闸（§6.2 安全红线）
}

// 服务端只写观测面（A1-α 的判定面之一，§9.1 / §9.3）。只写、无查询方法，因此不构成可被误用的状态查询面。
// 生产 Profile 注入 NullHostTraceSink（全部方法空实现）；只有 --enable-test-control + --audit-trace-file
// 同时给出时，App 才注入按 §9.3 行格式落盘的 JsonLinesHostTraceSink。
// IAuditWriter 的实现在每次成功写入后自动镜像调用 Trace.Audit(record)，因此 Auth 侧无需显式调用。
public interface IHostTraceSink {
    void Audit(in AuditRecord record);
    void Ack(string effect, ulong? admissionAttemptId, ulong? slotEpoch, ulong? connectionEpoch);
    void State(string? sessionId, string? sessionState, ulong? authorityRevision, ulong? slotEpoch, ulong? grantEpoch);
}
public static class ObservabilityModule {
    // IWallClock 只在这里被消费：两个 writer 内部填 EventId 与 Timestamp，调用方签名不变。
    // 全仓「IWallClock 只被 Observability 引用」由 ArchUnitNET 依赖断言守住（§6.0）。
    public static ObservabilityServices Create(IBoundedInbox<AuditRecord> auditInbox,
                                               IBoundedInbox<DiagnosticRecord> diagnosticInbox,
                                               IWallClock wallClock, IHostTraceSink trace);
}
```

`FaultAdjudication` 的两个 bool 是 §6.4 判定顺序的直接编码：`SessionLocalProven → (false, true)`；`SlotStateUnproven → (true, false)`；`ProcessFault → (true, false)` 且由 `App` 转交进程；`None → (false, false)`；**`null`（无见证）→ `(FaultClass = SlotStateUnproven, true, false)`**（ADR-006 的从严默认）。`FaultAdjudication.FaultClass` 本身**非空**——`Classify` 的职责就是把「无见证」折叠成一个确定的分类。

---

## 7. 工程基线

### 7.1 SDK pin

`mvp-host/global.json`：

```json
{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature", "allowPrerelease": false } }
```

**已实测层（本机 macOS 26.5，**宿主人格 `RID: osx-x64`——Rosetta 下的 x64 SDK，机器本身是 arm64（`uname -m` → `arm64`）；下列全部数字均为该人格下的观测值**，`dotnet --version` = `10.0.400`，`--list-sdks` 仅 `10.0.400 [/usr/local/Cellar/dotnet/10.0.400/libexec/sdk]`，`--list-runtimes` = `Microsoft.AspNetCore.App 10.0.11` + `Microsoft.NETCore.App 10.0.11`）**，10 组解析矩阵结论：

- **`version` 是下限（floor），不是通配前缀**。失败组：`{10.0.500, latestFeature}` exit=155、`{10.0.401, latestFeature}` exit=155（同 band 内 patch 也是下限）。
- **`rollForward: disable` 是字面精确匹配**。失败组：`{10.0.11, disable}`、`{10.0.100, disable}`、`{10.0.100, latestPatch}` 全部 exit=155；只有 `{10.0.400, disable}` 成功（仅因本机恰好装 10.0.400）。
- 成功组（均解析为 10.0.400）：`{10.0.100, latestFeature}`、`{10.0.100, latestMinor}`、`{10.0.100, latestMajor}`、`{9.0.100, latestMajor}`、`{10.0.400, disable/latestFeature/latestPatch}`。

**双机字面满足论证**：`version` 取「两台机器中最低的 .NET 10 feature band」作下限。macOS 侧 10.0.400（band 4xx，一手实测）；Windows 侧记录为 10.0.111（band 1xx，**来源是架构源七仓评审的转述，二手**）。`10.0.100 ≤ 10.0.111` 且 `10.0.100 ≤ 10.0.400`；`latestFeature` 允许在 10.0 内向上跨 feature band 但**不跨 major.minor**（不会静默滚到 .NET 11）；`allowPrerelease: false` 阻断预览 SDK 漂移。

**明确不抄的两个反面样板**：`LumioClient/global.json` = `{10.0.400, disable}` 且 `eng/verify-toolchain.sh` 硬 `grep -q '10.0.400'`——Windows 只有 10.0.111 时必失败；`LumioGameRuntime` 曾 pin `{10.0.11, disable}`（**10.0.11 是 runtime 版本号，微软没有同号 SDK**，任何机器都不可满足），现已修为 `{10.0.100, latestFeature}`——本设计与其同口径。

**SDK 族与 runtime 分两个口径**：`global.json` 只锁 SDK 版本族；runtime 由 `eng/verify-sdk.{sh,ps1}` 独立校验，**判据是前缀与 major.minor 一致，不是补丁号**（`expected_sdk_prefix=10.0.`、`expected_runtime_prefix=10.0.`，另断言 `Microsoft.NETCore.App` 的 `major.minor` 与 SDK 的 `major.minor` 相等），失配非零退出并同时打印 expected 与 actual。**确切补丁号只作为交回物里记录的观测值，不作门禁**——本机实测 `dotnet --list-runtimes` = `Microsoft.NETCore.App 10.0.11`（随 SDK 10.0.400 一同安装），说明该值完全由 SDK 补丁决定；把 `10.0.11` 写死成门禁，就是重犯下面「明确不抄的两个反面样板」里 `grep -q '10.0.400'` 的同一种错误：任一台机器升一个 runtime 补丁、或 Windows 侧 runtime 号不同，`verify-sdk` 即红，而后续 13 张卡每一条验收都以 `bash eng/verify-all.sh` 为前置，整条链会因与代码无关的环境补丁停摆。**不把 runtime 号写进 `global.json`**（参考实测：`cd /Users/cui/LumioGames/LumioGameRuntime && bash eng/verify-sdk.sh` → `SDK_OK sdk=10.0.400 runtime=10.0.11` exit=0）。

**待核层（诚实标注）**：Windows 侧对同一份 `global.json` 的 `dotnet --info` / `--list-sdks` 输出。本轮为单机环境——**在回填前不得声称「双机可满足」已验证**；首批排一张证据卡（卡 14）要求推送原始输出后回填。在此之前 `10.0.100` 是已知最低 band 的最保守选择。

### 7.2 TFM / LangVersion / 质量开关

- **全部工程单目标 `net10.0`**（生产与测试一致，不做 `netstandard2.1` 多目标），`LangVersion 14.0`。
- 依据（实测三条）：① 架构源 6 个 gen 工程全是 `net8.0`、零 PackageReference、自带零工程基线文件；net10.0 工程 `ProjectReference` 它们 → `Build succeeded, 0 Warning(s), 0 Error(s)`，`netstandard2.1` → **硬失败** `error NU1201: Project Lumio.Gen.ContractTypes is not compatible with netstandard2.1. Project Lumio.Gen.ContractTypes supports: net8.0`（且 `ContractTypes.cs` 用了 file-scoped namespace 与 `record struct`，需 C# 10+，而 LumioClient 的 LangVersion 是 9.0）；② ASP.NET Core 共享框架只能经 `FrameworkReference` 在 net10.0 上使用；③ net10.0 消费 `netstandard2.1` 是合法单向关系（LumioClient 的 net10.0 bot host 正是这样引用 9 个 netstandard2.1 生产工程），反向不成立。
- **不把任何自有程序集降级为 `netstandard2.1` 去被 Client 引用。** MVP 计划 §5 第 4 条的 `netstandard2.1` 收窄纪律只约束**双端共享程序集**，不适用于本宿主进程（§3 拓扑里它是独立 Server 进程，不进浏览器 WASM）。

`mvp-host/Directory.Build.props`（全部已实测有效）：`Nullable=enable`、`ImplicitUsings=disable`、`TreatWarningsAsErrors=true`、`EnableNETAnalyzers=true`、`AnalysisLevel=latest-recommended`、`EnforceCodeStyleInBuild=false`、`Deterministic=true`、`ContinuousIntegrationBuild=true`（CI）、`ManagePackageVersionsCentrally=true`、`CentralPackageTransitivePinningEnabled=true`、`RestorePackagesWithLockFile=true`、`DisableImplicitNuGetFallbackFolder=true`、`GenerateDocumentationFile=true`（生产工程）。实测：这套会把 `CA1051` 变成构建错误（exit=1）；`dotnet format --verify-no-changes --no-restore` 在有偏差时 exit=2；锁文件生成后 `dotnet restore --locked-mode` 成功。

### 7.3 ★ cwd 硬规程

实测：`global.json` **只按当前工作目录向上查找，不看工程路径**——把不可满足的 `{9.9.999, disable}` 放在子目录后，cwd 在仓根时 `dotnet build <子目录>/x.csproj` **完全无视它并构建成功**。

因此：**CI、eng 脚本、开发者文档中的每一次 dotnet 调用都必须先 `cd mvp-host`**；`eng/verify-*.sh` 自行解析脚本所在目录后 `cd`，不依赖调用方 cwd（`LumioGameRuntime/eng/verify-sdk.sh` 的 `cd repo_root` 注释防的正是这个坑）。这条写进 `mvp-host/README.md` 首屏、`.spec/knowledge/standards/testing.md` 的 C# 小节，并由 CI job 的显式 `working-directory: mvp-host` 落实。

### 7.4 NuGet 与测试栈

`mvp-host/NuGet.config`：`<clear/>` 后只留 nuget.org + `packageSourceMapping` + `auditMode=all` / `auditLevel=low`（后两项照 `LumioGameRuntime/NuGet.config`）。每工程提交 `packages.lock.json`，CI 必跑 `dotnet restore --locked-mode`。

**测试栈定死二选一：取 LumioClient 一侧的 VSTest 路线**（版本号全部来自实读 `LumioClient/Directory.Packages.props`）：

| 包 | 版本 |
|---|---|
| `xunit.v3` | 3.2.2 |
| `xunit.runner.visualstudio` | 3.1.5 |
| `Microsoft.NET.Test.Sdk` | 18.8.1 |
| `TngTech.ArchUnitNET.xUnitV3` | 0.13.3 |
| `FsCheck` | 3.3.4 |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | 5.6.0 |
| `System.Threading.Channels` | 10.0.0 |

**理由**：A1 回环的对手方进程就是 LumioClient 的 bot host，运行器与命令口径对齐能显著降低跨进程调试成本；该组合已实测在 net10.0 + SDK 10.0.400 上还原成功并产出锁文件。**反转触发条件（必须记录）**：当 `Runtime.Adapter` 进入构建图、宿主真正链接 Runtime 产物时，重新裁决是否迁到 LumioGameRuntime 的 MTP 路线（`xunit.v3 4.0.0` + `Microsoft.Testing.Platform 2.3.3` + `coverlet.MTP 10.0.1` + `CsCheck 4.7.0`）；**在此之前不得两套 runner 混进同一构建**（两者连 xunit.v3 主版本都不同）。

**WSS 零第三方依赖（实测）**：`Microsoft.NET.Sdk.Web` + net10.0 + `app.UseWebSockets()` 在**零 PackageReference** 下构建成功（`TreatWarningsAsErrors=true` 亦无告警），`obj/project.assets.json` 的 `libraries` 为空数组 `[]`、`frameworks` 仅 `net10.0`——能力全部来自已安装的 `Microsoft.AspNetCore.App 10.0.11` 共享框架，许可证 / SBOM 负担为零。另实测：普通 `Microsoft.NET.Sdk` **类库** + `<FrameworkReference Include="Microsoft.AspNetCore.App" />` 亦可编译 `ctx.WebSockets.AcceptWebSocketAsync(subprotocol)`，因此 `Transport.WebSocket` 是**库**而非 Web 应用，`Transport` 核心保持零框架引用、可无 Web 宿主单测。

`eng/banned-public-api.txt` 恰四条：`T:System.Net.Sockets.Socket`（socket 只能藏在 transport adapter 后，与 LumioClient 同规则）、`T:System.DateTime`、`T:System.DateTimeOffset`、`M:System.Threading.Thread.Sleep(System.Int32)`。

**分析器接线的归属定死在 `mvp-host/Directory.Build.props`（卡 2），不在 `Platform` 卡。** `Microsoft.CodeAnalysis.BannedApiAnalyzers` 必须逐工程引用才生效，而「在**所有**生产工程生效」只能写在 `Directory.Build.props`——该文件归卡 2 独占。因此卡 2 一次性写全三件事：① `PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers"`（不带 `Version`）；② `AdditionalFiles Include="$(MSBuildThisFileDirectory)eng/banned-public-api.txt"`；③ `Platform` 工程的 `IWallClock` 实现对 `T:System.DateTimeOffset` 的单点例外（§6.0）。卡 4 只负责「把 `Task.Delay` 收敛到 `Platform` 内单个 internal 文件」与本工程内的证据；**全仓探针证据**（在 `Platform` 之外的工程写一次 `DateTime.UtcNow` 后 `dotnet build` 报 `RS0030`）下沉到卡 7——卡 4 落地时 `Platform` 之外只有同 wave 并行的卡 3 的工程，往里塞探针即破坏 wave 2 的文件集互斥。注意 `Task.Delay` **不在** `banned-public-api.txt` 的四条内（它只受「唯一落点」的评审与卡 4 的工程内断言约束），两张卡的口径以本段为准。

**「不存在某调用」类断言统一用 `TngTech.ArchUnitNET`。** 它已在上表的中央包版本里，测试工程可引用它做**方法调用依赖**级断言（这是 `System.Reflection` 做不到的，见 §4.3 的断言机制纪律）。**不引入任何未冻结的分析包**——中央包表 7 个包由卡 2 一次写定，后续卡不再修改。

**ws:// 与 wss:// 分档**：本机已有有效 HTTPS 开发证书（CN=localhost，`IsHttpsDevelopmentCertificate: true`），但 `dotnet dev-certs https --check` 提示仍需 `--trust`；Windows 侧状态未知。因此 CI 与本地回环集成测试走 `ws://127.0.0.1`，由 `transport.allowInsecureLoopback` 显式开关控制、**默认 false**，且当 Host Profile 不是 `LocalSplitProcess` / `LocalEmbedded` 时**拒绝启用**（生产 Profile 的配置 schema 中不可表达）。`wss://` + 真实证书是**独立后续卡**；证书信任步骤写进 `mvp-host/README.md` 与 verify 脚本，**不进 CI 必经路径**。

### 7.5 可执行验证命令（收口门槛）

```bash
# ── 仓级收口门槛（.spec/AGENTS.md 定义；本轮实测当前 main 全绿）
cd /Users/cui/LumioGames/LumioServer
node .spec/tools/spec-lint.mjs               # 实测：spec-lint: OK ; rc=0
node --test .spec/tools/spec-lint.test.mjs   # 实测：tests 13 / pass 13 / fail 0

# ── MVP 宿主侧（必须先 cd，否则 SDK pin 静默失效）
cd /Users/cui/LumioGames/LumioServer/mvp-host
bash eng/verify-isolation.sh                 # §3.4 三条结构不变量；违规 rc=34 并打印违规路径
bash eng/verify-sdk.sh                       # SDK_OK sdk=10.0.4xx runtime=10.0.11 ; 失配非零
bash eng/verify-contract-mirror.sh           # 镜像哈希；漂移 rc=33
bash eng/verify-generated-contracts.sh       # 生成物重生成比对；漂移 rc=32
dotnet restore build.proj --locked-mode
for p in $(find src tests testkit -name '*.csproj'); do dotnet format "$p" --verify-no-changes --no-restore || exit 1; done
dotnet build build.proj -c Release --no-restore
for p in $(find src tests testkit -name '*.csproj' ! -name '*.Integration.Tests.csproj'); do dotnet test "$p" -c Release --no-build || exit 1; done
# 一键：bash eng/verify-all.sh   （Windows：pwsh eng/verify-all.ps1）
#   成功时最后一行恒为 MVP_HOST_VERIFY_OK 且 rc=0；任一步失败打印 MVP_HOST_VERIFY_FAIL <step> 并非零退出。
#   零工程状态（卡 2 刚落地、尚无 csproj）下空 glob 不算失败，同样输出 MVP_HOST_VERIFY_OK。

# ── 集成测试（testing.md「集成测试显式触发，不进默认验证命令」）
bash eng/verify-integration.sh               # 成功末行 MVP_HOST_INTEGRATION_OK ; rc=0
```

**为什么 `format` 与 `test` 都用逐工程循环**：`dotnet format` 的 workspace 参数只接受 `.csproj` / `.sln` / `.slnx`，而本设计的构建入口是 MSBuild 遍历工程 `build.proj`（选它是为了让新增工程只靠 glob 生效、不产生卡间共享文件冲突，见 §4.1）；逐工程循环是确定可行且不引入共享 solution 文件的写法。`test` 循环排除 `*.Integration.Tests`，因为 `.spec/knowledge/standards/testing.md` 规定集成测试显式触发、不进默认验证命令。

### 7.6 CI

`.github/workflows/repository-policy.yml` 当前**只有一个 `readme` job、无任何 .NET SDK 安装步骤**（实测 grep `setup-dotnet|dotnet` 无命中，Node 靠 runner 预装）。卡 2 新增一个**独立 dotnet job**（不塞进现有 `readme` job——现有 job 因 baseline grep 已红，塞进去会被连坐、reviewer 无法区分红灯来源）：

1. `actions/checkout@v4`；
2. 显式安装 .NET SDK（以 `mvp-host/global.json` 为准）；
3. 全部步骤 `working-directory: mvp-host`，依次跑 §7.5 的宿主侧命令；
4. §3.4 的三条隔离结构不变量断言。

> `setup-dotnet` 的具体 action 与入参本轮**未实测**，标注为卡 2 需在 CI 上首次验证并在交回物中回填证据的项。`dotnet test` 是否可直接跑 MSBuild 遍历工程同样未实测，上面用 `find` 循环作为确定可行的写法。

---

## 8. 互操作契约

### 8.1 与 LumioClient（connection / handshake / bot）

**方向性事实（划界依据）**：LumioClient README 首条声明拥有 `Connection、Handshake、Endpoint、断线、重连、Transport ACK、Baseline ACK、Gap 和 Resync`；LumioServer README 首条声明拥有 `进程、监听 Endpoint、认证、Connection、Session Admission、重连窗口、限流和背压`。即**拨号侧归 Client，监听侧归 Server，认证裁决归 Server**。

| 类型 / 契约 | 拥有者 | 本仓可否定义 |
|---|---|---|
| `IClientConnection`、`EncodedFrame`、`ConnectionGeneration`、`ConnectionEvent(Kind)`、`ConnectionCloseReason`、`IClientConnectionFactory`、`ClientConnectionCreateRequest/Result`、`LocalEmbeddedLoopback` | **LumioClient** | 否——不定义、不镜像、不重写 |
| `IClientHandshake`、`HandshakePhase`、`HandshakeRejectReason`、`IHandshakeFrameClassifier`、`HandshakeOpaqueFrameRole` | **LumioClient** | 否 |
| `ISessionMessageKindMap`、`SessionMessageKind`、`IClientSession`、`ClientSessionDependencies`（14 端口）、`IHeadlessBotHost`、`IBotScenarioDriver` | **LumioClient** | 否 |
| `ITransportFaultPolicy` / `TransportFaultAction` | 两侧**同名同序各自持有** | 是——本仓自有副本，靠名值一致而非共享程序集 |
| replication/gate/logging schema、8 个 `messageType`、43 个 ErrorCode、3 个 FaultClass、world-slot 状态机 fixture | **LumioGameEngineArchitecture** | 否——两仓都是只读消费方 |
| MVP 私有约定（一 WS 消息 = 一 Envelope、`length` 口径、`mappingSetHash` 口径、握手分类规则、子协议 token 承载） | **双端私有约定**，由 LumioServer 起草 | 是；**不是公共 wire**，全部落在冻结公共产物之外并逐条登记 `absences.json`（§5.9） |
| **`FullSnapshot` / `Delta` 的 body 字段集** | **LumioGameEngineArchitecture**（ADR-028 冻结 typed body） | **否**——本仓不向任何 body 添加任何字段（§5.6-A）；状态载荷等 B4 |

**握手时序：服务器必须先说话（硬约束，不可二选）。** 实测 `HandshakeSession.Begin` 只重置内部标志位、无任何发送动作；`ClientSession.StartGeneration` 也只 `connection.Start()` + `handshakeOrch.Begin(...)`——**客户端从不主动写第一帧**。任何「客户端先发 ClientHello 带 token」的方案在现状下**必然死锁在 `Negotiating`**。

```
Client                                             Server
  |-- WSS upgrade  Sec-WebSocket-Protocol: lumio.mvp.v0, <token>, <nonce> ---->|
  |                                     [通道认证 + 防重放；失败 → close 1008 + Audit]
  |<-- Envelope{ messageType:"Handshake", body:{ role:"Server" } } ------------|  ← ServerHello
  |          [客户端注入的 classifier：messageType=="Handshake" → ServerHello]  |
  |          [能力协商必须客户端本地同步可判定]                                  |
  |-- Envelope{ messageType:"Handshake", body:{ role:"Client" } } ----------->|  ← 跨仓卡 CC-3
  |                                     [Admission saga 八步]
  |<-- Envelope{ FullSnapshot, body 字段集恰为 _REPLICATION_BODY_REQUIRED 的 5 项 } --|
  |-- Envelope{ BaselineAck, body:{ snapshotId, confirmedRevision } } ------->|
  |<-- Envelope{ Delta, body 字段集恰为 _REPLICATION_BODY_REQUIRED 的 6 项 } ---------|
  |-- Envelope{ DeltaAck, body:{ confirmationSequence, toRevision } } ------->|
```

**MVP 帧分类映射表（LumioServer 定规则，LumioClient 写实现）**——只看 Envelope 的 `messageType`，**不引入任何新字节格式**：

| Envelope `messageType` | `HandshakeOpaqueFrameRole` | `SessionMessageKind` |
|---|---|---|
| `Handshake` | `ServerHello` | —（在 `Negotiating` 态由 classifier 消费） |
| `Error` | `HandshakeReject` | `Unknown` |
| `FullSnapshot` | `Unclassified` | `FullSnapshot` |
| `Delta` | `Unclassified` | `Delta` |
| `ResyncRequest` | —— | ——（**客户端出站方向，客户端不接收**：公共契约里 `ResyncRequest` 由检测到 gap 的副本方发出，架构源 v1.4 §7.1 的链路是 `DeltaAck / GapDetected -> ResyncRequest -> FullSnapshot or ResyncPatch`。客户端本地检出 gap 后自行发出它并等服务端回 `FullSnapshot`；服务端**从不下发**该类型） |
| `MaintenanceKick` | `Unclassified` | `Unknown`（连接随即关闭） |
| `BaselineAck` / `DeltaAck` | — | —（客户端不接收） |

**MVP 期服务端只下发 5 个**：`Handshake` / `FullSnapshot` / `Delta` / `Error` / `MaintenanceKick`。**`ResyncRequest` 不在其中**——它是客户端出站类型（见上表），本仓上一版把它列进服务端下发集并映射为 `SessionMessageKind.Gap`，等于让服务端告诉客户端「你有 gap」，与架构源 v1.4 §7.1 的方向相悖；schema 层拦不住这种方向重解释，唯一的拦截点是设计与卡面。`MvpEnvelopeWriter.WriteResyncRequest` 保留，但**只供 `SmokeClient` 使用**，卡 11 有一条 ArchUnitNET 断言：`Session` 工程对 `WriteResyncRequest` / `WriteClientHandshake` / `WriteBaselineAck` / `WriteDeltaAck` 四个方法零调用依赖。

**客户端出站 4 个**：`Handshake{role:"Client"}` / `BaselineAck` / `DeltaAck` / `ResyncRequest`——正好对应 §5.3 的冒烟客户端 4 个 writer。分类器与映射器的**实现**落在 LumioClient 的 `modules/bot/host` 组装根（跨仓卡 CC-4），不落本仓；本仓在 `SmokeClient` 内部实现对称逻辑，并把映射表作为接口契约发布。`SmokeClient` 构造上行信封**必须**全部经 `MvpEnvelopeWriter`——卡 12 有一条断言：`SmokeClient` 程序集内零 `System.Text.Json.JsonSerializer` 使用、零手写 Envelope 构造，否则就多出一条绕开 writer 与 fixture 金标准门的第二条公共信封构造路径。

**能力协商必须客户端本地同步可判定。** 实测 `HandshakeSession.HandleFrame` 收到 ServerHello 后调用 `_capabilities.QueryAsync(...)`，但**只有 `pending.IsCompleted` 为真时才把结果入队**，异步完成的 `ValueTask` 结果被静默丢弃 → 握手永久停在 `AwaitingCapability`。因此 MVP **不设计任何需要服务器回包才能完成的能力协商往返**。该缺陷记为 LumioClient 侧已知问题（CC-6），**不作为 Server 卡前置**。

**断连触发权归服务端。** 客户端 Fault Decorator 不可注入（`OwnerConnection` 硬编码 `PassThroughFaultPolicy`）、`LocalEmbeddedLoopback.TryDisconnectClient` 是进程内专用且构造函数 `internal`。A1 的断连场景由 `ISessionAdminPort.Kick(sessionId, "MaintenanceKick")` 驱动。客户端 `ClientSession.HandleDisconnect` 已按「取新代次 → 释放 → `Reconnecting` → `StartGeneration(next)`」实现，符合 D-012，无需改动。

**大小上限由服务端兜底。** 客户端 `ConnectionStateMachine.CanSend` 除空帧外无任何长度校验，`maxMessageBytes` 红线只能由服务端在分配前拒绝。**egress 限流兜底**：客户端 `ConnectionEventQueue` 容量固定 32、每 Tick 只 drain 16，`TryDeliverInbound` 返回 false 会中止本轮 pump → 高频 Snapshot 下可能静默丢帧。服务端以 SRV-D-006 provisional 64 msg/s、突发 128，并对每 tick 每连接 egress 批量设上限 8 条兜住。

**必须双向对齐的三个常量**（列为跨仓确认项）：`productId = "A"`；`gameReleaseId = "A-1.1.0"`；`protocolVersion = 1`。**`gameReleaseId` 取三段式的理由**：fixture 内部不一致（replication 系列用 `"A-1.1.0"`，gate 系列用 `"A-1.1"`，两者都合法但没有规范形态说明），而 Admission 要做 ExactRelease 精确匹配（D-007），若两端各按一份 fixture 取样会在一个**都合法**的值上失败。取复制链路的三段式（fixture 更多），写进单点配置常量。记为架构源观察项 B7。

**LumioClient 侧硬约束（设计与拆卡都不得违反）**：其 `.github/workflows/repository-policy.yml` 硬断言 `modules/` 恰好 11 个子目录且每个 README 含 `LGE-V1.2-2026-08-27`——**不得提出在 LumioClient 新建模块**；客户端改动只能落在既有 `modules/connection` 与 `modules/bot/host`（后者是 Composition Root：`BotShortcutTests` 断言 `Lumio.Client.Bot` 不得引用 connection/handshake/replica/prediction）。其 `eng/banned-public-api.txt` 禁 `System.Net.Sockets.Socket`（`ClientWebSocket` 不在禁表内）。其生产库是 `netstandard2.1` / `LangVersion 9.0`，**无法引用架构源生成包**（实测 NU1201）——**本设计不要求它改 TFM**（那属对方 Wave7 范围）；跨边界只传 Envelope 字节与已注册错误码字符串。

### 8.2 与 LumioGameRuntime

**MVP 期消费面 = 空。** Runtime 11 模块中只有 `observability` 有实现；`IRuntimeSession` / `ISimulationSession` / `ReferenceHost` / `ReferenceVoxelPort` 在仓库里都不存在。

| 类型 | 拥有者 | MVP 状态 |
|---|---|---|
| `IWorldSimulationPort`、`HostTickRequest/Outcome`、`WireFrame`、`HostSessionId`、`HostWorldSlotId`、`LogicalTickToken`、`HostSimulationState`、`HostFaultClass` | **LumioServer** | 本设计定义；签名中零 Runtime 类型 |
| `IRuntimeSession` / `ISimulationSession`、`TickInput` / `HostTickRequestView`、`TickRunResult`、`SimulationSessionState`、`InputEnvelopeView` | **LumioGameRuntime** | 不存在或未冻结；MVP 不消费 |
| `IRuntimeEventPort` / `IMetricPort` / `ITracePort` / `IDurableEvidencePort` 与其 `*View` | **LumioGameRuntime** | 已有真实代码；将来由 Adapter 实现，MVP 不引用 |
| `ITxnJournalPort`、`IVoxelAuthorityPort`、`IWorldStorageAdapter` | **LumioGameRuntime** | 零代码 / `internal`；宿主拿不到也不实现 |
| `ReferenceHost`、`ReferenceVoxelPort`、`ReferenceScenario` | **LumioGameRuntime（`modules/testing`）+ 架构仓契约侧** | **本仓一行不写** |

**Adapter 到位时的契约**：唯一引用 `Lumio.GameRuntime.*` 的工程是 `adapters/Lumio.Server.MvpHost.Runtime.Adapter`（本轮不建）。**宿主提供给 Runtime 的 Port 只有 observability 一族**（签名照抄 Runtime `Contracts/*.cs`），实现全部落在 Adapter 内。**不提供 clock port**——Runtime 从不读 Wall Clock（README 明写「Runtime 不声明 Tick Clock 的驱动所有权」，Task 21 反射测试禁方法名含 `Clock`）。§6.0 的 `IWallClock` **不是**给 Runtime 的 port：它是宿主内部产出 `logging-event.timestamp` 的唯一出口，只被 `Observability` 引用（ArchUnitNET 依赖断言），既不出现在 `IWorldSimulationPort` 的签名里，也不进 Adapter 的提供面。**不提供 storage adapter**——`IWorldStorageAdapter` 是 ecs 模块 `internal`。Runtime 的 `TickRunResult` 当前**不携带 `FaultClass`**，Adapter 因此把 `HostTickOutcome.FaultClass` 填 `null`（「无见证」），由 `IFaultAdjudicator.Classify(null)` 折叠为 `SlotStateUnproven` 从严处理——这正是 §6.5 把该字段做成可空的原因。

**`ReferenceVoxelPort` 的跨仓未决冲突（原样上报，本仓不解决）**：MVP 计划把它当作 MVP 期服务器权威体素的用户可见路径（生产路径），Runtime 设计却把它放进 `modules/testing`（test-only）并用 `ProductionDependencyGuard` 与 Foundation 退出条件**禁止任何生产工程引用**。MVP C# 宿主一旦要在生产路径上跑权威体素，就需要一条非法依赖。归口 Runtime / 架构侧。**本仓不实现、不复制、不定义任何 voxel 类型**；宿主 Port 保持体素无关，以保证 B2 汇合后由 ReferencePort 切 Rust Native 时宿主公共面**零改动**。宿主唯一可做的是在 Host Profile 里记录「本 Slot 的体素权威后端由 Runtime 组合提供」这一事实位以供取证。

---

## 9. 验收场景（A1）

MVP 计划 §4 轨道 A 的 A1 退出条件原文：「Bot 客户端跨进程联机挖方块；断连重连走 Full Resync 恢复」，形态是 `LocalSplitProcess` 两个 C# 进程。按 §5.6 的裁决，拆成两个场景。

### 9.1 A1-α：跨进程协议与生命周期全环（本批次可自动化交付）

**执行者**：`Lumio.Server.MvpHost.Integration.Tests`，以**独立进程**拉起 `Lumio.Server.MvpHost.App` 与 `Lumio.Server.MvpHost.SmokeClient`（真实 `ws://127.0.0.1:<port>` 套接字，不是进程内环回）。

**判定面（两个文件，缺一不可）**：客户端侧 `--trace-file` + **服务端侧 `--audit-trace-file`**（§9.3）。上一版有 6 步要求观测服务端内部状态（Audit 字段、saga 八步 ack、`AuthorityRevision`、会话状态、`SlotEpoch`、Quiesce ack 顺序），而跨进程可观测面只有三条**只写** POST 路由、一行 `MVP_HOST_READY`、退出码与客户端 trace——那 6 步没有任何合法判定手段。补齐方式是给 `App` 增一个**只写文件的服务端 trace**，而不是加读路由：读路由会把服务端内部状态做成一个可被误用的查询面，而 trace 文件与客户端 trace 同一格式口径、天然只读、且不改变任何已有路由。**两个 trace 都不解析日志文本。**

**场景步骤与判定条件**（每步均为机器可判断言；「C」= 客户端 trace，「S」= 服务端 trace）：

| # | 步骤 | 判定条件 | 判定面 |
|---|---|---|---|
| 1 | SmokeClient 以 `Sec-WebSocket-Protocol: lumio.mvp.v0, <token>, <nonce>` 发起 upgrade | 服务端回选 `lumio.mvp.v0`，HTTP 101 | C |
| 2 | 错误 token 的第二次 upgrade | close `1008`；**未发任何 Envelope**；服务端 trace 有一条 `kind:"audit"` 记录，`category="Audit"`、`severity="Warn"`、`scope="Release"`、`releasePoolId` 非空、**`sessionId` 为 `null`** | C + S |
| 3 | 重放同一 `nonce` | 拒绝；服务端 trace 有一条 `reasonCode="SessionAntiReplay"` 的 audit 行；且**该次拒绝不消耗**下一次合法握手的窗口配额（随后一次合法握手成功） | C + S |
| 4 | 服务端首帧 | `messageType=="Handshake"` 且 `body` 的 key 集合**恰好** `{role}`，`role=="Server"` | C |
| 5 | 客户端回 `Handshake{role:"Client"}` | 通过 Admission saga 八步；服务端 trace 依次出现 8 条 `kind:"ack"` 行，`effect` 按 `ReadGate → Authenticate → MatchExactRelease → ReserveSlot → CommitSlot → CreateSession → BindConnection → StartReplication` 有序，每条带同一 `admissionAttemptId` 与对应 epoch；末尾一条 `kind:"state"` 行 `sessionState="Active"` | S |
| 6 | 服务端下发 `FullSnapshot` | 过双层校验；`reliability=="Reliable"`；`body` 的 key 集合**恰好**等于 `{snapshotId, tickId, sessionRevisionVector, schemaEpoch, mappingSetHash}`；`sessionRevisionVector` 全 7 字段且 `chunkRevisionSet` 的 key 匹配 canonical `_CHUNK_KEY` 正则；`mappingSetHash` 等于 `MvpWireConstants.MappingSetHash` | C |
| 7 | 客户端回 `BaselineAck{snapshotId, confirmedRevision}` | 服务端接受；**Transport ACK 与 Baseline ACK 分离**（断言两者在实现上是不同路径） | C + S |
| 8 | 经 `POST /test-control/inject-world-mutation` 注入一次世界变更（**带外测试控制面，不经任何 Envelope**） | 服务端 trace 出现 `kind:"state"` 行，`authorityRevision` 由 `N` 变为 `N+1`（注入后的第一个 tick 内应用，§6.5） | S |
| 9 | 服务端下发 `Delta` | `body` 的 key 集合**恰好**等于 `{baseSnapshotId, fromRevision, toRevision, mappingSetHash, confirmationSequence, tombstones}` 且 `tombstones == []`；`toRevision > fromRevision` 且 `toRevision` 等于步骤 8 的 `N+1`；`baseSnapshotId` 等于步骤 6 的 `snapshotId`；`sessionRevisionVector` 口径的 revision **严格前进** | C + S |
| 10 | 客户端回 `DeltaAck{confirmationSequence, toRevision}` | 服务端接受 | C |
| 11 | 客户端故意跳过一个 `Delta`（模拟 gap，同连接内），**由客户端**发出 `ResyncRequest{resyncReason}` | 服务端接收后下发新 `FullSnapshot`；**握手计数不变**（同连接内 Resync 不重新握手）；客户端 trace 中该 `ResyncRequest` 的 `direction` 为 `"out"` | C + S |
| 12 | 服务端 `Kick(sessionId, "MaintenanceKick")` | 客户端先收 `MaintenanceKick` Envelope，随后连接关闭；服务端 trace 出现 `kind:"state"` 行 `sessionState="ReconnectWindow"` | C + S |
| 13 | 客户端以**新连接代次**重连 | **重做通道认证**（新 nonce）+ **完整 Handshake**；服务端 trace 的 `grantEpoch` 递增；**不存在任何 Resume Token 路径**（客户端 trace 中无任何跳过握手的步骤） | C + S |
| 14 | 重连后的复制（Full Resync） | 下发**全新 `FullSnapshot`** → `BaselineAck` → 回到 `Active`；该 `FullSnapshot` 的 `sessionRevisionVector` 携带的 revision **严格大于**断连前最后一条 `Delta` 的 `toRevision`（这是本设计能证明的最强命题；世界内容一致性归 A1-β） | C + S |
| 15 | 让另一个会话在 `ReconnectWindow` 内不重连 | 窗口到期 → `Expired`；窗口到期与重连的竞争由 `MvpSessionControlInbox` 串行裁决，输者收稳定错误，最终状态唯一 | C + S |
| 16 | 拒绝路径 | `productId`/`gameReleaseId` 不匹配 → `Error{Rejectable, "ReleaseMismatch"}`；旧连接代次的消息 → `StaleConnectionGeneration`；超 `maxMessageBytes` → 分配前拒绝并断连 | C |
| 17 | `Quiesce` | 服务端 trace 的四条 `kind:"ack"` 行按 `AdmissionClosed → Drained → SnapshotCut → Stopped` **有序**出现，每条带 `slotEpoch`；Gate 在停 Tick 之前关闭 | S |

**A1-α 证明了什么**：跨进程 WSS 传输、通道认证与防重放、Admission saga 八步、架构源 v1.4 §7.1 复制状态机全链路、同连接 Resync 与跨连接 Full Resync 两条不同路径、服务端主动断连、连接代次与 Slot epoch 的隔离、Quiesce 原子序列、错误码正确性。一句话：**协议与生命周期闭环，跨进程可验证**。

**A1-α 没有证明什么（诚实边界，两条）**：
1. **世界变更的来源不是客户端上行 wire**，而是带外测试控制面。
2. **客户端看不到任何世界内容**。按 §5.6-A，本仓不向 `body` 添加任何字段，而公共 `FullSnapshot` / `Delta` 的 body 里没有状态载荷字段——客户端能观察到的只有 revision 严格前进，**不是「Bot 看到方块被挖」**。那件事整体属 A1-β。

> **这正是卡面验收③要的「集成卡雏形」。** A1-α 是雏形本身：它把两个真进程、真套接字、真 Envelope 的链路跑通并留下机器可判的两份 trace。**不得**把 A1-β 的任何断言伪装成本批次可完成。

### 9.2 A1-β：世界状态可观察 + 客户端上行 gameplay 命令 —— **BLOCKED**

A1 的字面退出条件「Bot 客户端跨进程联机挖方块」**整体**落在这里。它需要两件公共面缺位的东西，**两件都必须先由架构源冻结**：

| 缺什么 | 现状（实测） | 前置诉求 |
|---|---|---|
| **服务端→客户端的世界状态载荷** | `FullSnapshot` / `Delta` 的 typed body 由 ADR-028（Accepted）冻结，其 Alternatives 明文否决 free-form payload；本仓因此不向 body 添加任何字段（§5.6-A）。没有它，客户端**看不到方块被挖** | **B4**（P0） |
| **客户端→服务端的 gameplay 命令承载** | 8 个冻结 `messageType` 中没有任何一个能承载 client→server 命令；D-009 原文禁止任何仓库发明 dispatch wire format（§5.6-B）。没有它，Bot **挖不了方块** | **B8**（P0） |

**B4 是 A1-β 的硬前置，与 B8 并列。** 上一版把 B4 写成「并行上报的诉求」，那是在假设本仓可以先用私有字段顶上；该假设已被 §5.6-A 推翻，B4 因此升级为前置。

**上报诉求**：① 请架构源为 `FullSnapshot` / `Delta` 冻结一个公共状态载荷字段（新增 typed body 必填/可选字段并出 fixture）；② 请架构源为 V1 冻结一个客户端输入承载方式——新增一个 `messageType`（如 `ClientCommand`）并定义其 typed body 必填字段，或明确 V1 的客户端输入承载规则。两条都走 `ADR → Schema → fixtures → 新 BaselineId → 七仓镜像同步`。

**解冻后的落地成本（已预留）**：本设计使解冻当天只需四步——① 同步 `contract-mirror`；② 在 `Wire` 的语义层与出站 exact-set 集合里加上新字段/新 messageType；③ `Session` 的复制编排把 `IWorldSimulationPort` 的 egress `WireFrame` 填进那个公共状态字段；④ `Session` 把上行 messageType 的 body 载荷转成一个 `WireFrame` 投进 `MvpIngressQueue`。`IWorldSimulationPort` 已按不透明 `WireFrame` 收发 ingress/egress，**宿主公共面零改动**。此外还需 LumioClient 的 CC-8（输入面表达「挖哪个方块」）与 CC-9（解码公共状态载荷并更新 bot 可观察世界状态）落卡。

**排期含义**：A1 的字面退出条件在 **B4 + B8 + CC-8 + CC-9** 全部落地前**不成立**。这不是本设计可以自行消解的，必须由总调度在架构源与 LumioClient 两侧排期。本批次交付的是「解冻当天即可闭环」的服务端全部能力，以及一条今天就能跑绿的跨进程协议闭环（A1-α）。

### 9.3 进程 CLI 与测试控制面承载

A1-α 要求 `Integration.Tests` 以**独立进程**拉起 `App` 与 `SmokeClient`，因此两者的命令行面与退出码是集成卡的验收依据，必须在此定死。

**`lumio-mvp-host`（`Lumio.Server.MvpHost.App`）**

| 参数 | 含义 |
|---|---|
| `--listen <uri>` | 监听端点，形如 `ws://127.0.0.1:0`（`0` = 由 OS 选端口）。`ws://` 必须同时给 `--allow-insecure-loopback`，否则拒绝启动 |
| `--allow-insecure-loopback` | 对应 §7.4 的 `transport.allowInsecureLoopback`，默认 false；`--host-profile` 不是 `LocalSplitProcess` / `LocalEmbedded` 时给它即拒绝启动 |
| `--host-profile <name>` | `LocalSplitProcess` / `LocalEmbedded`；缺省 `LocalSplitProcess` |
| `--product-id <id>` / `--game-release-id <id>` | ExactRelease 比对的单一二元组，缺省 `A` / `A-1.1.0`（§8.1） |
| `--shared-secret-file <path>` | `InjectedExactByteCredentialVerifier` 的比对材料路径。**凭据只经文件载入，绝不经命令行或环境变量**；文件缺失或不可读 = 可致命错误，进程拒绝启动 |
| `--reconnect-window-seconds <n>` | SRV-D-004 的 provisional 覆写，缺省 120 |
| `--enable-test-control` | 打开 `ISessionAdminPort`；不给则该端口在组装图中根本不存在 |
| `--test-control-listen <uri>` | 形如 `http://127.0.0.1:0`；仅当 `--enable-test-control` 同时给出时有效，且 host 必须是 `127.0.0.1` 或 `::1`，否则拒绝启动 |
| `--audit-trace-file <path>` | **服务端只读观测面**（A1-α 的判定面之一，§9.1）。把 AuditRecord 与关键 ack / 状态变更按**每行一个 JSON 对象**追加写入该文件。**仅当 `--enable-test-control` 同时给出时有效**，否则拒绝启动——它与三条测试控制路由受同一套三重门控，生产 Profile 的配置 schema 中不可表达 |

启动完成后向 **stdout 打印且仅打印一行** `MVP_HOST_READY listen=<实际 uri> testControl=<实际 uri 或 ->`，供父进程解析实际端口；此后 stdout 不再用于结构化输出。退出码：`0` = 收到 SIGTERM / SIGINT 后走完 Quiesce 五步正常退出；`64` = 参数非法；`70` = 可致命错误（验证材料缺失、监听绑定失败、Slot 进入 `Faulted` 的 fail-stop 退出）。

**测试控制面承载（`ISessionAdminPort` 的跨进程形式）**：`--test-control-listen` 上的 loopback-only HTTP 端点，三条路由与 §6.3 的三个方法一一对应——`POST /test-control/begin-drain`、`POST /test-control/kick`、`POST /test-control/inject-world-mutation`，请求与响应体是 JSON 对象，响应形如 `{"accepted":true,"stableErrorId":null}`。它**不经过任何 Envelope、不复用 WS 监听端口、不参与任何复制或权限语义**，因此不是 wire，也不触碰 D-009 / D-011（裁决理由见 §5.6-B 与 J2）。三重门控：① 只在 `--enable-test-control` 下装配；② 只绑回环地址；③ 每次调用写一条 Audit 事件。`--audit-trace-file` 受同一套门控（① 与 ③ 适用，②替换为「只写本地文件、不开任何监听」）。生产 Profile 的配置 schema 中**不可表达**这四个开关（`.spec/rules/system.md`「dev-only 开关 / 调试后门不得在生产开启」）。

**服务端 trace 文件的行格式（`--audit-trace-file`）**：每行一个 JSON 对象，**字段集固定为下列 17 个键，每行必须全部出现**（不适用时取 `null`），使判定完全靠字段读取、不靠日志文本解析：

```
{"seq":<int>,"kind":"audit"|"ack"|"state","eventId":<string|null>,"timestamp":<string|null>,
 "category":<string|null>,"severity":<string|null>,"scope":<string|null>,"releasePoolId":<string|null>,
 "sessionId":<string|null>,"reasonCode":<string|null>,"admissionAttemptId":<int|null>,
 "effect":<string|null>,"sessionState":<string|null>,"authorityRevision":<int|null>,
 "slotEpoch":<int|null>,"connectionEpoch":<int|null>,"grantEpoch":<int|null>}
```

- `kind:"audit"` 行由 `Observability` 的 Audit 写入面镜像产出，`eventId` / `timestamp` / `category` / `severity` / `scope` / `releasePoolId` / `sessionId` / `reasonCode` 取自 `AuditRecord`（§6.6）——因此它同时是「Audit 事件形状正确」的跨进程证据。
- `kind:"ack"` 行由 saga 与 Quiesce 序列产出，`effect` 取 `AdmissionEffectKind` 或 `AdmissionClosed` / `Drained` / `SnapshotCut` / `Stopped` 之一，配 `admissionAttemptId` / `slotEpoch` / `connectionEpoch`。
- `kind:"state"` 行在 `ServerConnectionSessionState`、`AuthorityRevision`、`GrantEpoch` 变化时产出。
- `seq` 全局单调递增，**顺序断言只依据 `seq`**，不依赖文件系统时间。
- 写入面是 `Observability` 的 `IHostTraceSink`（§6.6）：**只写、无任何查询方法**，因此不构成可被误用的状态查询面。生产 Profile 注入 `NullHostTraceSink`（全部方法空实现）；只有 `--enable-test-control` 与 `--audit-trace-file` 同时给出时，`App` 才注入落盘实现 `JsonLinesHostTraceSink`。`IAuditWriter` 的实现在每次成功写入后自动镜像调用 `Trace.Audit(record)`，因此 `Auth` 侧无需显式调用；`Session` 与 `WorldSlot` 显式调用 `Trace.Ack(...)` / `Trace.State(...)`。**它不新增任何读路由，也不改变任何已有 HTTP 路由。**

**`lumio-mvp-smoke-client`（`Lumio.Server.MvpHost.SmokeClient`）**

| 参数 | 含义 |
|---|---|
| `--endpoint <uri>` | `ws://127.0.0.1:<port>` |
| `--token-file <path>` / `--nonce <value>` | 子协议第 2、3 段的来源（§6.2）；token 只经文件载入 |
| `--product-id <id>` / `--game-release-id <id>` | 与宿主一致的二元组 |
| `--scenario <name>` | `a1-alpha` / `bad-token` / `replay-nonce` / `oversize-message` / `stale-generation` / `release-mismatch` / `gap-resync` / `reconnect` |
| `--trace-file <path>` | 把每条收发 Envelope 与每步判定结果按**每行一个 JSON 对象**写入该文件，字段固定为 `{"step":<int>,"direction":"in"|"out","messageType":<string|null>,"assertion":<string>,"passed":<bool>,"detail":<string|null>}` |

退出码：`0` = 场景全部断言通过；`64` = 参数非法；`65` = 场景断言失败（trace 文件中至少一条 `passed:false`）；`70` = 传输层致命错误。`Integration.Tests` 的判定依据是 trace 文件内容与退出码，不解析日志文本。

---

## 10. 未触碰声明

本节逐条对应 R-00260 验收第 4 条，供 reviewer 逐条核对。

**(a) 未触碰 protocol-dispatch（D-009）冻结面。**
- 本仓未创建、未修改 `modules/protocol-dispatch/` 下任何文件；未创建其 crate、C# 工程、API、handler registry 或测试替身。
- 未引入任何 MessageId 命名空间、RPC envelope、方法号、路由表、correlation/cancel/deadline 语义或生成式分发。
- MVP 的消息处理是对 8 个已冻结 `messageType` 的**直接分派**（switch），不抽象出通用分发层。
- **拒绝**了在 `BaselineAck.body` / `DeltaAck.body` 承载客户端 gameplay 命令的做法，尽管 schema 的开放 `body` 使其机器可通过——判定理由与 BLOCKED 上报见 §5.6-B / §9.2。
- **本仓不向任何 `body` 添加任何字段**（§5.6-A）。上一版曾裁决允许一个 server→client 的私有状态载荷字段 `mvpAuthorityPayload`，本轮**已撤销**：ADR-028（Accepted）的 Alternatives 明文否决 free-form payload。落地断言由「只允许一个私有字段名」升级为**出站 body 字段集恰好等于 `_REPLICATION_BODY_REQUIRED` 对应组**（exact-set）。缺失的公共状态载荷字段登记为 BLOCKED **B4**（`ABS-REPLICATION-STATE-PAYLOAD`），并升级为 A1-β 的硬前置。

**(b) 未占用 51 张 Rust 卡的任何独占文件。**
- 本轮实测：51 卡 349 个唯一文件，`.cs`/`.csproj`/`.sln`/`.slnx` 结果集为**空**；顶层前缀集合见 §3.1；候选名求交 `mvp-host` 返回 **FREE**。
- 全部新增文件落在 `mvp-host/**`，与 349 项**前缀级零交集**，可用单行断言机器判定：`任一 51 卡文件路径不以 "mvp-host/" 开头`。
- `mvp-host/` 之外触碰的文件逐条与 349 集合对照见 §3.3 的表：**表体 7 行，其中 5 个不在 349 集内**，**重叠的 2 个**是 `.spec/knowledge/standards/code-style.md` 与 `testing.md`（归 wave 0 的 `establish-cargo-workspace-and-rust-standards`），处置为**串行 + 只增量追加 C# 小节**（§3.3）。
- **本声明可机器复核**，在仓库根执行（本轮实测输出即下方注释所示）：

  ```bash
  python3 -c "
  import json
  S={f for t in json.load(open('docs/LumioServer_Framework_Implementation_Design_2026-08-27/manifests/task-index.json')) for f in t['files']}
  T=['docs/specs/2026-08-28-mvp-csharp-host-design.md','docs/specs/2026-08-28-mvp-csharp-host-cards/','.gitignore','.gitattributes','.github/workflows/repository-policy.yml','.spec/knowledge/standards/code-style.md','.spec/knowledge/standards/testing.md']
  print('set349=%d mvpHostPrefixed=%d touched=%d overlap=%s'%(len(S),len([f for f in S if f.startswith('mvp-host/')]),len(T),sorted(set(T)&S)))"
  # 实测输出：set349=349 mvpHostPrefixed=0 touched=7
  #           （本轮实测 touched=7）overlap=['.spec/knowledge/standards/code-style.md', '.spec/knowledge/standards/testing.md']
  ```
- 未修改 `modules/README.md`（被 `synchronize-implementation-mapping-docs` 独占）、未修改 `.spec/guards/*.toml`（3 个，被 Rust 卡独占）、未修改 `.github/workflows/rust-foundation.yml`（被 wave 13 卡独占）、未修改 `docs/specs/2026-08-27-foundation-measurement-report.md` 与 `manifests/provisional-defaults-measurement.json`。
- 未改变任何 Rust 卡的范围、依赖或 wave 编号。

**(c) 未解冻 D-011；auth 只做 Host 私有存根。**
- 未定义任何凭据 / 票据 / nonce / rotation / 签名算法 / 密钥派生的**线格式**。MVP token 是**不透明字节**，只在 WSS Upgrade 的子协议里出现一次，在 WebSocket 建立前被消费并丢弃。
- **`Handshake` body 恒为恰好 `{role}` 一个字段**，并写成机器断言（§5.4）——这是 D-011 最容易踩的违规点，schema 的开放 `body` 使塞 token 能通过全部机器校验，只有设计纪律 + 门禁能拦。
- 严格遵守 D-011 已冻结的**行为契约**：每次握手（含每次重连）在 session 准入**之前**通过防重放校验。
- verifier 落在 Rust 侧设计已有的合法位置（`injected exact-byte verifier adapter`，原文「不成为 wire 标准」）；不对外发布、不作为公共契约。
- 未实现 Session Resume Token（D-012）：新连接代次一律重做通道认证 + 完整 Handshake 并重新派生授权对象；同连接内 Resync 不重新握手。
- 未新增任何 ErrorCode——全部取自 `ids/index.json` 的 43 个已注册值；认证失败因无对应码而**不发 Envelope Error**（§6.2，BLOCKED B3）。

**(d) 未改变 Host 与 Gameplay/Voxel 的所有权边界。**
- 宿主 Port `IWorldSimulationPort` 的签名中**零 Runtime 类型、零 voxel 类型**；载荷是不透明 `WireFrame`，`StateHash` 是不透明字节。
- 参考存根**不定义** chunk / block / entity / component / ability / voxel / tick-phase 任何类型（机器断言的禁用词表）；世界模型是不透明 key→value + 单调 `AuthorityRevision`。
- 未实现、未复制、未定义 `ReferenceVoxelPort`，也未为其写 differential 测试——它归 LumioGameRuntime（`modules/testing`）+ 架构仓契约侧。
- `sessionRevisionVector` 的 7 字段与 `chunkRevisionSet` 由复制编排层从单一 `AuthorityRevision` 填充，是**冻结 schema 强制的信封字段**，不构成体素建模。
- 未内建集群控制器；集群期望状态（Pool 存在性、Release 指派、实例替换）仍归外部控制面（ADR-012）。
- world-slot 不拥有 `SimulationSession` 状态机（归 Runtime），只经稳定接口驱动其生命周期入口；持有的是句柄而非内部状态。

**(e) 未碰 R-00186 / R-00188。** 本设计未引用、未修改、未依赖这两张需求的任何范围或产出；其证据可复核性问题由总调度在 Windows 侧推送后重核，与本卡无关。

---

## 11. 已知缺口与 known gaps

| ID | 缺口 | 影响什么 | 当前处置 | 解铃人 |
|---|---|---|---|---|
| **G1** | **C# 侧无生成的 `ReplicationEnvelope` POCO**。`Lumio.Gen.LanguageBinding/Bindings.cs` 只有名字映射三元组，仓里不存在该类型定义；`Lumio.Gen.CanonicalSerializer` 只有 2 个常量 | 宿主要收发 Envelope 就必须有具体 C# 类型，但本仓写一套即构成「第二套公共协议定义」，违反 ADR-023 与 v1.4 §17 | 私有 `internal` `MvpEnvelopeDocument` + 五条机器护栏（含**自过期守卫**）+ 12 条 replication fixture（8 正 4 反）金标准门；名字让出 `ReplicationEnvelope`；不生成 typed body POCO（§5.3） | **架构源生成器**（BLOCKED B1） |
| **G2** | **`Lumio.Gen.ProtocolPermissionValidator` 不是可执行校验器**，只有 15 个字段名字符串；而 ADR-022 明文否决 `Hand-written per-repo validators were rejected for drift` | MVP 只能自实现六项比对，字面上就是被否决的做法 | **受控降级 + 四重护栏**：照抄 `lumio_contract.py:1169-1192`（本轮实测坐标；`1005-1028` 是 Root-ABI 表校验，与 gate 无关）不增删、直接消费 `ActivePermissionFields.Names` 建立编译期耦合、2 条 gate fixture 回归、自过期守卫 + 文档声明为临时替身（§5.8） | **架构源生成器**（BLOCKED B2） |
| **G3** | **架构仓 R-00258（TransportProfile WebSocket 档 Capability 登记）已交付并已合并 `origin/main`**。本轮实测锚点：`origin/main` 的 `a738524`（`git log --oneline -1 origin/main -- docs/architecture/TRANSPORT-WEBSOCKET-PROFILE-REGISTRATION.md`）。**注意：合并时该提交被 rebase 重写**——分支上的 `f426278` 与卡上证据评论引用的 SHA 都**不是** `origin/main` 的祖先（`git merge-base --is-ancestor f426278 origin/main` 返回否），引用它即为死锚点 | 结论是「**WebSocket 档不需要任何公共契约变更**」：可靠性落 `Reliable`、大小/分片/反重放/authBinding/errorClass/integrity/8 个 MessageType/断线重连/权限门 6 字段/Host Capability 共 11 项已被现有公共面覆盖，属 D-004 意义上的 adapter 级选择。**原判定「无权威 ID 可用」失效** | 采纳其结论：Host Capability 用 `WebSocketTransport`（沿用既有 `InMemoryTransport` 的 `<Kind>Transport` 约定；`host-capability` 的 capabilities 数组在 schema 上是自由 `id`，**不进 ID Registry 的 `Capability` 命名空间**）；登记 id `LGE-V1.4-TRANSPORT-WS-2026-08-28`。**仍禁止因 WebSocket 新增任何 ErrorCode**（§5.9）。**引用纪律**：卡面验收**不得**把该结论文档当唯一依据，一律回指一手真值——`schemas/replication-envelope.schema.json`、`ids/index.json`、`tools/lumio_contract.py` 的具体行；该文档只作**佐证与出处**，且引用时**首选路径锚点** `origin/main:docs/architecture/TRANSPORT-WEBSOCKET-PROFILE-REGISTRATION.md`（`git show origin/main:<path>` / `git ls-tree origin/main` —— rebase 打不掉），SHA `a738524` 只作辅助定位；**不用任何分支 SHA**。跨仓核实只读已提交对象，**绝不读别人仓的工作区**（他仓 HEAD 可能正被并发切换）。判据链四层递进：`cat-file -t`（挡对象不存在）→ `branch -r --contains`（挡已提交未推送）→ `ls-remote` 或 fetch 后再查（挡本地 ref 陈旧）→ `merge-base --is-ancestor <sha> origin/main`（挡在分支上但未进 main）；**结论必须带测量时刻**——本轮实测同一份交付在 40 分钟内出现过三种状态 | **架构源已答复**（B6 关闭） |
| **G4** | **LumioClient 无远程/WSS 传输**（只有 LocalEmbedded 环回，Wave7 未启动）；且 `ClientConnectionCreateRequest` 无 endpoint/凭据入参、`ClientConnectionCreateResult` 公共面绑死 `LocalEmbeddedLoopback`、上行帧是 3 字节魔数与预测计划字节而非 Envelope | A1 的客户端拨号一端完全不存在；**Server 侧可以全绿而 A1 仍不可演示** | 首批集成卡只验到本仓自带 `SmokeClient`（真跨进程 WSS）；Bot 真机对接拆成显式依赖跨仓卡的后置卡（卡 13） | **LumioClient**（CC-1..CC-5、CC-7） |
| **G5** | **LumioGameRuntime 主体未实现**（11 模块仅 `observability` 有代码，31 卡中 26 张 backlog）；宿主面入口有两份互斥草案且 RT-D-001 未批；`TickRunResult` 既无 egress 出口字段也不携带 `FaultClass` | 无法照抄任何 Runtime 类型作为宿主公共面 | 宿主 Port 完全自有、签名零 Runtime 类型；差异全压进**不进构建图**的 `Runtime.Adapter`；「Adapter 缺席仍全绿」为机器断言（§4.2、§6.5） | **LumioGameRuntime**（RQ-1..RQ-5） |
| **G6** | ~~`repository-policy.yml` 的 README baseline grep 在 main 上失败~~ **已由上游修复，本缺口关闭**。`origin/main` 的 `9fe0cd7`（`ci(policy): 把仓库边界断言从 v1.2 retarget 到 v1.4 基线`）把该步骤的四条 baseline 断言整体 retarget 到 v1.4。本轮在 `637b464` 上逐条复跑 16 条断言：**16/16 通过** | 无影响 | 无需动作；**原卡 1 `fix-repository-policy-baseline-drift` 因此作废并从卡集删除**（见 §13） | **已关闭**（上游 `9fe0cd7`） |
| **G7** | 本仓 `modules/world-slot/README.md` 是 v1.2 表述「Slot 转 `Faulted`，从最近有效 Snapshot 恢复」，与 v1.4 的 ADR-027 fail-stop（同一 Slot 永不恢复）冲突 | 若照本仓 README 实现「Faulted → 恢复 → Running」，会与公共契约冲突，且是行为级返工 | 本设计**以 v1.4 为准**（§6.4）；README 更新登记为 known gap，四模块 README 虽未被卡占用但属语义源，改动应走独立卡 | **本仓，需新卡** |
| **G8** | 本仓 `modules/transport/README.md` 的 Envelope 校验字段清单落后于 v1.4，缺 `transportPolicy` 与 `body` 两个必填字段 | 若按 README 清单实现校验，反例 fixture 可能过不了，或放行本应拒绝的报文 | 本设计把校验**直接绑定到 schema 文件本身**（跑 schema 校验而非抄字段列表） | **本仓，需新卡** |
| **G9** | **`length` 字段语义无任何公共定义**；`integrity` 的哈希原像亦未定义 | 两端必须就 `length` 达成一致才能互通，但无公共口径，8 条 fixture 一律写死 256 无法反推 | 非对称口径：入站只校验 `>= 0` 不交叉核对，出站写 body 字节数；`integrity` 只产出 `{None,"none"}`，接收侧全量校验（§5.7）。声明为 MVP 双端私有约定，不构成公共主张。**超限消息**（`maxMessageBytes` / `maxFragmentBytes`）采纳架构源 R-00258 §4 观察项给的临时口径：`errorClass = Rejectable` + `Error.body.reasonCode = BudgetExceeded`（1035），不自造 `MessageTooLarge` 一类的码 | **架构源**（BLOCKED B5；超限口径已由 R-00258 §4 给出，无需再等） |
| **G10** | **fixture 内 `releaseId` 取值不一致**：replication 系列 `"A-1.1.0"`，gate 系列 `"A-1.1"`，两者都合法但无规范形态说明 | ExactRelease 精确匹配会在一个都合法的值上失败 | 单点常量固定 `"A-1.1.0"`，列为跨仓双向对齐条目（§8.1） | **架构源观察项**（B7） |
| **G11** | **43 个 ErrorCode 中无「凭据无效」语义码**（全表已核对） | 通道认证失败无法在信封层表达 | 不发 Envelope Error，升级阶段 close `1008` + Audit（§6.2） | **架构源 ID Registry**（BLOCKED B3） |
| **G12** | **Audit durable ack 缺席**（observability 是 P1，未列入本仓 MVP 工作包），而 `modules/auth/README.md` 把「Audit 不可写时不得静默放行认证」写成安全红线 | 最容易变成隐形技术债的一处 | 实现 `MvpAuditQueue` 的背压状态并接到 world-slot 关闸（**行为红线保住**）；durable ack / Failure Bundle 装配登记缺席，承接卡 `implement-observability-audit-durable-pipeline`、`implement-observability-failure-bundle-and-emergency-path` | **本仓 Rust 卡** |
| **G13** | **MVP 期无 Runtime `FaultClass` 见证来源** | `SessionLocalProven` / `SlotStateUnproven` 两条分支只有单元测试覆盖、无端到端证据；且缺见证一律从严意味着任何捕获故障都会终止 Slot（fail-stop、进程退出） | `Classify(null) → SlotStateUnproven` 单独单测；**连接级故障严格归 transport 处置（只断该连接）**，符合故障域分层不是绕过 | **LumioGameRuntime**（RQ-1） |
| **G14** | **Windows 侧 SDK 列表本轮无法实测**（单机环境）；「Windows 曾用 10.0.111」是架构源评审的转述。**另有一处本轮新发现的宿主人格问题**：本机是 Apple arm64（`uname -m` → `arm64`），但安装的 .NET SDK 是 **Rosetta 下的 x64**（`dotnet --info` → `RID: osx-x64`、`Architecture: x64`），同机 `rustc -vV` 的 host 亦为 `x86_64-apple-darwin` | 「工程基线版本口径双机可满足」只完成一半的一手验证；**且已完成的那一半是 `osx-x64`（Rosetta）证据，不是原生 `osx-arm64` 证据**  | 结论分三层写（已实测层 / 宿主人格标注层 / 待验证层）：**回填前不得声称「双机可满足」已验证**，且一切本机产出的 SDK / 构建 / 时延数字必须显式标注 `RID: osx-x64 (Rosetta on arm64)`，不得当作原生 arm64 结果；**本轮已探查原生 arm64 交叉复验的可行性并给出结论：不可行（无零成本路径）**——`dotnet --info` 的 `Other architectures found:` 为 `None`，`which -a dotnet` 只有一个入口，`DOTNET_ROOT=/usr/local/Cellar/dotnet/10.0.400/libexec`（x64 Homebrew 前缀），系统内无并存的 arm64 SDK。（对照：Rust 侧有已安装的 aarch64 工具链，可零成本跑原生腿——.NET 侧没有这个便利。）要拿原生腿必须**另装 arm64 .NET SDK**，属系统变更，需用户决定；若日后加装，采用「正式验收证据仍用钉定工具链并标注宿主人格，arm64 腿作为补充证据单列，两者都留、不互相冒充」的口径；卡 14 的 Windows 证据回填要求同时记录该机的 `RID`  | **需 Windows 侧一手输出 + 本机原生 arm64 SDK（可选）** |
| **G15** | **`spec-lint` 不覆盖 `docs/`**（校验范围是 `.spec/**.md` + 仓根 `README.md` + 仓根 `AGENTS.md`）；对任务卡只机器校验根目录 frontmatter（仅 `status` 三值枚举），正文必含节顺序、接口节、禁占位符**全部不是机器可判** | 本文档内部链接与 14 张卡的格式合规高度依赖 reviewer 人眼 | 文档自述该限制；评审清单加人工链接抽查项与卡格式抽查项。扩展 spec-lint 校验范围可另立独立卡（`.spec/tools/spec-lint.mjs` 不在 349 集内），不与本批次绑定 | **本仓，可另立卡** |
| **G16** | 仓根**无 `.editorconfig`**；`.gitignore` 只有 5 行、无 `bin/` `obj/`；`.claude/settings.json` 的提交前 hook 只跑 spec-lint、**不含任何 dotnet 校验** | 第一次 `dotnet build` 就污染 `git status`；C# 侧无本地提交前兜底，只能靠 CI | 卡 2 补 `.gitignore` / `.gitattributes`；`.editorconfig` 下沉 `mvp-host/` 且不设 `root=true` | **本仓卡 2** |
| **G17** | **`mappingSetHash` 无合法取值来源**。它是 `FullSnapshot` / `Delta` 的必填字段（`:355-364`）、取值域 `hash256`，指向一套 ADR-005 所述、由 **LumioGame 拥有**而 MVP 期并不存在的公共映射集；架构源有 `schemas/replication-mapping.schema.json`，本仓无映射集 | 出站必须填一个值，否则每条 `FullSnapshot` / `Delta` 都被本工程自己的语义层判 `SemanticReject`；不登记就会由实现者现场发明（多半抄 fixture 的 64 个 `a`），双端在一个无公共定义的字段上达成隐式约定 | 与 `length` 同一处置模式：出站固定填本仓单点常量（64 个 `0`，provisional，不构成公共主张），入站只校验 `hash256` 正则、不做交叉核对；登记 `ABS-REPLICATION-MAPPING-SET`（§5.7a） | **架构源**（B9） |
| **G18** | **通道认证凭据与 nonce 的线承载无公共定义**。D-011 原文「no public wire format exists yet …… implementations must not invent a wire format before that」；v1.4 正文把「通道认证」作为机制未公开的独立步骤 | MVP 取 `Sec-WebSocket-Protocol: lumio.mvp.v0, <token>, <nonce>` 的固定子协议名与位序；A2 浏览器客户端沿用同一承载时，一个从未被架构源看过的凭据承载会在两个仓固化 | **技术选择不变**（浏览器 API 不能设自定义头但能设子协议，J5），且**本轮取得了直接出处**：架构源 R-00258 交付物 `docs/architecture/TRANSPORT-WEBSOCKET-PROFILE-REGISTRATION.md` §3 明文「清单外的一切都不是公共契约」，并逐条点名「WS 子协议名、端点路径、close code 映射、permessage-deflate、具体 WS 库」**归 Server/Client 自行约定**——本条据此从「MVP 章程推论」升格为「架构源明示授权」，但**登记与退场纪律不减免**；补齐登记：`ABS-AUTH-CREDENTIAL-CARRIAGE` + 本条 + B10，适用 §5.7 同一条退场纪律；`Handshake` body 恰为 `{role}` 的机器断言堵死 D-011 最易踩的违规点（§5.4、§6.2、§5.6-C） | **架构源**（BLOCKED B10） |
| **G19** | **公共 `FullSnapshot` / `Delta` 的 typed body 无状态载荷字段**（实测 fixture body 只含标识、版本向量、`mappingSetHash`、`tombstones`），而 ADR-028（Accepted）的 Alternatives 明文否决 free-form payload 。**另有一处需架构源一并复核的自相矛盾**：R-00258 交付物 §0 断言「现有 Envelope…已经完整覆盖 MVP A1 所需的公共语义」，而同文 §3.3 的 body 必填字段表正好证明状态载荷缺位——两句无法同时成立 。**缺口比「body 里少一个字段」更深**（架构仓 2026-08-28 复核补充，本轮已独立验证）：`schemas/replication-mapping.schema.json` 的 required 是 `mappingId / schemaVersion / source / target / role / owner / visibility / delivery / lifecycle / prediction`——它冻结的是**哪些字段被复制**（描述符），而 Envelope 只携带 `mappingSetHash`；**被映射值的线编码整层未定义**。因此这不是补一个字段能了结的，需要裁决「值怎么上线」 | 服务端无法把世界状态发给客户端；**A1 字面退出条件「Bot 看到方块被挖」整体不可达** | 本仓**不自行补字段**（§5.6-A 撤销了上一版裁决）；出站 body 字段集恰好等于 `_REPLICATION_BODY_REQUIRED`（exact-set 机器断言）；登记 `ABS-REPLICATION-STATE-PAYLOAD`，并把 B4 升级为 **A1-β 的硬前置**（§9.2） | **架构源**（BLOCKED B4） |

---

## 12. 跨仓依赖清单

**本仓一个文件都不改对方仓；以下每条都需要对方仓落卡，由总调度排期。**

### 12.1 LumioGameEngineArchitecture

| ID | 诉求 | 优先级 |
|---|---|---|
| **B8** | **冻结一个 client→server 的 gameplay 输入承载方式**（新 `messageType` + typed body 必填字段，或明确 V1 的承载规则）。当前 8 个 messageType 无一可用 | **P0——阻塞 A1 字面退出条件** |
| **B4** | 为 `FullSnapshot` / `Delta` 冻结一个公共状态载荷字段。本仓**不自行补**（ADR-028 的 Alternatives 明文否决 free-form payload），因此这是**A1-β 的硬前置**，与 B8 并列，不是可并行的观察项 | **P0——与 B8 并列阻塞 A1 字面退出条件** |
| **B1** | 生成 C# 的 `ReplicationEnvelope` POCO（及 `SessionRevisionVector` 等），使各仓不必各自手写 View | P0 |
| **B2** | 生成**可执行**的 C# Protocol/Permission Validator（当前只有 15 个字段名字符串） | P0 |
| **B3** | ErrorCode 词表增补「凭据无效」类语义码（43 个中无对应项） | P1 |
| **B5** | 澄清 `length` 字段语义（哪一段字节）与 `integrity` 的哈希原像定义 | P1 |
| **B6** | ~~R-00258：WebSocket 档的 TransportProfile Capability 登记~~ **已答复（2026-08-28）**：结论「WebSocket 档不需要任何公共契约变更」，登记 id `LGE-V1.4-TRANSPORT-WS-2026-08-28`；本设计已采纳其 §3 的 A1 公共面清单、§3.5 的 `WebSocketTransport` capability 命名、§4 的超限消息临时口径（`Rejectable` + `BudgetExceeded`）。**已合并 `origin/main`**（`a738524`；分支 SHA `f426278` 因 rebase 被重写，**不可作为锚点**）| **已关闭** |
| **B9** | 澄清**无 LumioGame 映射集时 `mappingSetHash` 的合法取值**（是否允许全零哨兵、是否需要一个「无映射集」的规范值）。当前它是 `FullSnapshot` / `Delta` 的必填字段但指向一套 MVP 期不存在的公共映射集（G17） | P1 |
| **B10** | 冻结**通道认证的凭据与 nonce 承载方式**，或明确判归 Host 私有。当前 D-011 只冻结行为契约（准入前必过防重放），承载方式无公共定义，MVP 取子协议位序（G18） | P1 |
| **B7** | 规范化 fixtures 间的 `releaseId` 字面量（`"A-1.1.0"` vs `"A-1.1"`） | P2（观察项） |
| **B11** | **请复核 R-00258 交付物内部的自相矛盾**：§0 断言「现有 Envelope…已经完整覆盖 MVP A1 所需的公共语义」，而 §3.3 的 body 必填字段表证明状态载荷缺位（这正是 B4）。该断言若不修正，会让下游误以为 A1 的公共面已齐备 | P1（与 B4 同源，建议同卡处理） |

### 12.2 LumioClient

**硬约束**：其 CI 硬断言 `modules/` 恰好 11 个子目录——**不得新建模块**；改动只能落在既有 `modules/connection` 与 `modules/bot/host`。

| ID | 诉求 | 建议落点 | 阻塞什么 |
|---|---|---|---|
| **CC-1** | `ClientConnectionCreateRequest` 增加 endpoint URI + 不透明凭据字节 + 不透明 nonce + 连接超时（或新增 `ClientEndpoint` 值类型） | `modules/connection/src/Public/**` | Bot 无法被告知连哪、带什么 token |
| **CC-2** | WSS `IClientConnectionFactory` 实现 | `modules/connection/src/Internal/Transport/**` | 客户端拨号一端不存在 |
| **CC-3** | 上行帧改为 Envelope 形状：`Handshake{role:"Client"}`、`BaselineAck{snapshotId, confirmedRevision}`、`DeltaAck{confirmationSequence, toRevision}`，取代 3 字节魔数与预测计划字节 | `modules/session/src/Internal/**`、`modules/handshake/src/Internal/**` | 服务端会把现有全部上行帧判为畸形 |
| **CC-4** | 按 §8.1 映射表实现并注入 MVP `IHandshakeFrameClassifier` + `ISessionMessageKindMap` | `modules/bot/host/**`（Composition Root） | 默认实现恒 `Unclassified` / `Unknown`，握手推不动 |
| **CC-5** | `modules/bot/host` CLI 支持 `--transport wss|ws --endpoint <uri> --token <opaque>`（现状：非 `local-embedded` 直接退出码 3） | `modules/bot/host/FoundationHostCommand.cs` | Bot 无法连远程 |
| **CC-6** | 修 `IPlatformCapabilityProvider` 异步结果被静默丢弃 | `modules/handshake/src/Internal/HandshakeStateMachine.cs` | 已知缺陷；MVP 用同步能力规避，**非 Server 卡前置** |
| **CC-7** | 远程工厂下 `ClientConnectionCreateResult.Loopback` 为 null 的调用方处理；`ITransportFaultPolicy` 可注入；事件队列容量/drain 上限 | `modules/connection`、`modules/bot/host` | 联调即 NRE；高频 Snapshot 可能静默丢帧 |
| **CC-8** | 输入面能表达「挖哪个方块」（现 `RawInputSample` 仅 `(Buttons, AxisX, AxisY)`，mapper payload 全是 `new byte[]{0x42}` 占位） | `modules/input`、`modules/bot`（+ LumioGame 映射） | **A1「挖方块」上行侧的真正门槛**；依赖 **B8** 先解冻才有意义 |
| **CC-9** | 解码 `FullSnapshot` / `Delta` body 中**架构源冻结的公共状态载荷字段**（B4 的产物），并据以更新 bot 可观察的世界状态 | `modules/bot/host` | **A1「看到方块被挖」下行侧的真正门槛**；依赖 **B4** 先解冻才有意义。本仓已撤销私有 `mvpAuthorityPayload`（§5.6-A），因此这项工作量**不再是本仓私有约定强加的成本**，而是公共契约落地后的正常消费方工作；排 `verify-a1-beta-bot-cross-process-mining` 时必须与 CC-8 一并计入 |

### 12.3 LumioGameRuntime

| ID | 诉求 |
|---|---|
| **RQ-1** | `TickRunResult` 暴露 `FaultClass`（当前无该字段，而本仓 world-slot / coreclr-host 的故障分级依赖 Runtime 见证） |
| **RQ-2** | 为 Runtime egress 定形一个把复制帧交回宿主的类型（`TickRunResult` 现无 egress 字段，而 `EgressPublish` 相位声称产出 egress batch） |
| **RQ-3** | 批准 RT-D-001，冻结宿主面入口（`IRuntimeSession` vs `ISimulationSession`） |
| **RQ-4** | `ObservabilityModule.Create` 开放 `IDurableEvidencePort` / `ITxnJournalPort` 注入口（现只收 event/metric/trace 三个） |
| **RQ-5** | 解决 `ReferenceVoxelPort` 归属冲突：MVP 计划把它当 MVP 期服务器权威体素（生产路径），Runtime 设计却放进 `modules/testing` 且 `ProductionDependencyGuard` 禁止生产工程引用。**不由 LumioServer 解决** |
| **RQ-6** | 修 Runtime 内部 BaselineId 漂移（README = V1.4，`modules/*` 与两份计划 = V1.3）。本仓引用 Runtime 文档时**只引结论、不抄其 BaselineId 字符串** |

---

## 13. 首批实现卡索引

> **卡片本体**落在 [`2026-08-28-mvp-csharp-host-cards/`](2026-08-28-mvp-csharp-host-cards/README.md)（13 张 `<slug>.md` + 一份索引 README）。本节是摘要索引，与该目录逐卡一一对应；口径冲突时以卡片本体为准。

全部卡严格遵循 `.spec/tasks/README.md` 的格式契约：frontmatter 仅 `status`（`pending`/`in_progress`/`completed`）；正文按序 `# 目标` / `## 涉及范围`（逐一列文件路径）/ `## 验收标准`（`- [ ]` 可客观验证）/ `## 依赖`；有邻卡依赖的卡必填 `## 接口`（Consumes/Produces 写精确签名）；`.spec/tasks/README.md`「禁占位符（拆解失败判据）」一节列出的全部占位符与「类似卡 N」式引用一律不得出现，也不得引用任何卡都没定义的类型或函数。**每张 MVP 实现卡的验收标准另含一条**：「未越界实现任何 `mvp-host/absences.json` 列出的条目」。

| # | slug | 一句话目标 | wave | 独占文件集要点 | 依赖 |
|---|---|---|---|---|---|
| 1 | `scaffold-mvp-host-build-baseline` | 建 `mvp-host/` 构建根（下沉全部 .NET 根文件）、SDK verify 脚本、遍历工程、缺席清单，并新增独立 dotnet CI job 与三条隔离不变量断言 | 0 | `mvp-host/{README.md,absences.json,global.json,Directory.Build.props,Directory.Build.targets,Directory.Packages.props,NuGet.config,.editorconfig,build.proj}`、`mvp-host/eng/{verify-sdk.sh,verify-sdk.ps1,verify-isolation.sh,verify-isolation.ps1,verify-all.sh,verify-all.ps1,banned-public-api.txt}`、`.gitignore`、`.gitattributes`、`.github/workflows/repository-policy.yml`（与卡 1 同文件，串行） | 无 |
| 2 | `vendor-architecture-contracts-and-fixture-mirror` | 把 6 个 C# 生成 artifact 与 4 份 schema + 16 条 fixture 以只读镜像 + sha256 锁引入，配 sync/verify 双脚本（漂移退出码 32 / 33） | 1 | `mvp-host/contract-mirror/**`、`mvp-host/src/Lumio.Server.MvpHost.GeneratedContracts/**`、`mvp-host/tests/Lumio.Server.MvpHost.GeneratedContracts.Tests/**`、`mvp-host/eng/{generate-contracts,verify-generated-contracts,sync-contract-mirror,verify-contract-mirror}.{sh,ps1}`、`mvp-host/eng/contract-mirror.sha256`、`mvp-host/eng/verify-all.{sh,ps1}`（在卡 2 版本上插入两条契约校验步骤）、`mvp-host/README.md`（补记契约镜像命令）——后两项与卡 2 同文件但**跨 wave 串行**，与同 wave 的卡 4 无交集 | 1 |
| 3 | `implement-mvp-host-platform-primitives` | 实现 host-runtime 等价最小面：单调时钟、Timer 类型化投递、有界端口、具名线程监督、取消树（禁 sleep/轮询/DateTime） | 1 | `mvp-host/src/Lumio.Server.MvpHost.Platform/**`（含 `queues.json`）、`mvp-host/tests/Lumio.Server.MvpHost.Platform.Tests/**` | 1 |
| 4 | `implement-mvp-envelope-wire-and-fixture-gate` | 实现 `MvpEnvelopeDocument`、规范 JSON 编解码、结构层+语义层双校验、gate 六项判定、9 个按方向分组的 writer 与出站 exact-set 断言，以镜像 16 条 fixture 为回归门（含分层拦截断言与自过期守卫） | 2 | `mvp-host/src/Lumio.Server.MvpHost.Wire/**`、`mvp-host/tests/Lumio.Server.MvpHost.Wire.Tests/**` | 2 |
| 5 | `define-mvp-host-contracts-and-audit-surface` | 定义跨模块唯一契约面（ids/epochs/typed commands+events/有界端口/`IWorldSimulationPort`）与 Audit/Diagnostic 最小写入面，并建立全局架构门禁 | 3 | `mvp-host/src/Lumio.Server.MvpHost.HostContracts/**`、`mvp-host/src/Lumio.Server.MvpHost.Observability/**`、`mvp-host/tests/Lumio.Server.MvpHost.Architecture.Tests/**`、`mvp-host/testkit/Lumio.Server.MvpHost.TestKit/**` | 3, 4 |
| 6 | `implement-mvp-transport-core-and-bounded-queues` | 实现载体无关的连接注册表、`ConnectionEpoch`、校验闸（分配前拒绝）、四条有界队列、限流、可注入故障装饰器、`IByteCarrier` SPI | 4 | `mvp-host/src/Lumio.Server.MvpHost.Transport/**`（含 `queues.json`）、`mvp-host/tests/Lumio.Server.MvpHost.Transport.Tests/**` | 5 |
| 7 | `implement-mvp-auth-stub-and-permission-gate` | 实现 injected exact-byte verifier、防重放窗口与 `ReplayStorm`、不可变 `PermissionGrant`、gate 执行体、auth 拒绝 Audit 形状 | 4 | `mvp-host/src/Lumio.Server.MvpHost.Auth/**`（含 `queues.json`）、`mvp-host/tests/Lumio.Server.MvpHost.Auth.Tests/**` | 5 |
| 8 | `implement-mvp-world-slot-aggregate-and-sim-port-stub` | 实现 `WorldSlotHost` 聚合根（13 态以 fixture 为真值，前向迁移表与 `anyActiveTo` 规则两份独立集合）、Gate 唯一所有权、Quiesce 五步原子序列、Owner Thread + TickPermit 链、`IFaultAdjudicator`，与 `IWorldSimulationPort` 参考存根 + `IWorldMutationSink` 实现 | 4 | `mvp-host/src/Lumio.Server.MvpHost.WorldSlot/**`（含 `queues.json`）、`mvp-host/src/Lumio.Server.MvpHost.Simulation.Reference/**`、`mvp-host/tests/Lumio.Server.MvpHost.WorldSlot.Tests/**` | 5 |
| 9 | `implement-mvp-websocket-carrier-adapter` | 实现 `IByteCarrier` 的 WSS/WS 版本：Kestrel 监听、Upgrade 期子协议 token 终结、一 WS 消息 = 一 Envelope、Close 帧与空闲超时、服务端主动 Close 入口；零第三方 NuGet | 5 | `mvp-host/src/Lumio.Server.MvpHost.Transport.WebSocket/**`、`mvp-host/tests/Lumio.Server.MvpHost.Transport.WebSocket.Tests/**`（第 10 个测试工程，见 §3.2 / §4.2） | 6, 7 |
| 10 | `implement-mvp-session-admission-saga-and-reconnect` | 实现 `ServerConnectionSession` 八态、Admission saga 八步 + 恰好一次补偿、重连窗口（无 Resume Token）、Drain/Kick、架构源 v1.4 §7.1 复制编排（Transport ACK 与 Baseline ACK 分离） | 5 | `mvp-host/src/Lumio.Server.MvpHost.Session/**`（含 `queues.json`）、`mvp-host/tests/Lumio.Server.MvpHost.Session.Tests/**` | 6, 7, 8 |
| 11 | `assemble-mvp-host-app-and-smoke-client` | 组装可执行 `lumio-mvp-host`（显式 new、无 DI 容器、测试控制面仅回环且需显式开关）与自带 `SmokeClient`（构造合法 Envelope，与 Bot 同 wire） | 6 | `mvp-host/src/Lumio.Server.MvpHost.App/**`、`mvp-host/src/Lumio.Server.MvpHost.SmokeClient/**`、`mvp-host/tests/Lumio.Server.MvpHost.App.Tests/**`（第 11 个测试工程，见 §3.2 / §4.2） | 9, 10 |
| 12 | `verify-a1-alpha-cross-process-replication-loop` | A1-α 全 17 步跨进程自动化验收（真实 `ws://` 套接字，判定面 = 客户端 trace + 服务端 trace 两个文件），并显式声明 A1-β 因 **B4 + B8** 而 BLOCKED、不在本卡范围 | 7 | `mvp-host/tests/Lumio.Server.MvpHost.Integration.Tests/**`、`mvp-host/eng/verify-integration.{sh,ps1}`（新建，§7.5 的集成测试显式入口） | 11 |
| 13 | `writeback-csharp-standards-and-dual-machine-evidence` | 在 `code-style.md` / `testing.md` 增量追加 `## C#（MVP 宿主）` 小节（双语言域、formatter/analyzer 口径、SDK pin 与 cd 硬规程、C# 验证命令族、生成物纪律），并回填 Windows 侧 SDK 一手证据 | 8 | `.spec/knowledge/standards/code-style.md`、`.spec/knowledge/standards/testing.md`。★ 与 51 卡 wave-0 的 `establish-cargo-workspace-and-rust-standards` **文件集重叠，必须串行**，卡内写明「后落地方只增量追加本语言小节，不得覆盖另一语言小节」 | 1, 2, 12 |

**后置跨仓卡（本轮不落单，需总调度跨仓排期）**：`verify-a1-beta-bot-cross-process-mining` —— A1 字面退出条件正本，依赖架构源 **B4 + B8**（分别解冻下行状态载荷与上行命令承载）与 LumioClient **CC-1..CC-5 + CC-8 + CC-9**。

### 13.1 依赖 DAG

> **本轮修订（2026-08-28）**：原 wave 0 的 `fix-repository-policy-baseline-drift` **已作废删除**——其目标已由上游
> `origin/main` 的 `9fe0cd7`（`ci(policy): 把仓库边界断言从 v1.2 retarget 到 v1.4 基线`）达成，本轮在 `637b464` 上
> 实测 repository-policy 的 16 条断言 **16/16 通过**。卡集由 14 张降为 **13 张**，全部 wave 前移一档。

```
wave 0:  [1] scaffold-mvp-host-build-baseline                                       (无前置)
              |
wave 1:  [2] vendor-architecture-contracts   [3] implement-platform-primitives      (并行)
              |                                    |
wave 2:  [4] implement-envelope-wire ---------------+
              |                                    |
wave 3:  [5] define-host-contracts-and-audit <-----+
              |
wave 4:  [6] transport-core   [7] auth-stub   [8] world-slot + sim-port-stub         (三路并行)
              |                   |                |
wave 5:  [9] websocket-carrier <--+                |
         [10] session-admission-saga <-------------+                                 (两路并行)
              |
wave 6:  [11] assemble-app-and-smoke-client
              |
wave 7:  [12] verify-a1-alpha-cross-process-replication-loop
              |
wave 8:  [13] writeback-csharp-standards-and-dual-machine-evidence
```

卡 13（writeback）排在最后而非与卡 13 并行，理由是它要把 C# 验证命令族**完整**写进 `testing.md`，其中包含卡 3 产出的 `eng/verify-contract-mirror.sh` / `eng/verify-generated-contracts.sh` 与卡 13 产出的 `eng/verify-integration.sh`——文档不描述尚不存在的命令。

### 13.2 同 wave 文件集互斥证明

| wave | 并行卡 | 各自独占的顶层路径 | 交集 |
|---|---|---|---|
| 2 | 3, 4 | `mvp-host/contract-mirror/**` + `mvp-host/src/…GeneratedContracts/**` + `mvp-host/tests/…GeneratedContracts.Tests/**` + `mvp-host/eng/{generate,verify}-contracts*` + `{sync,verify}-contract-mirror*` + `contract-mirror.sha256` + `mvp-host/eng/verify-all.{sh,ps1}` + `mvp-host/README.md` **vs** `mvp-host/src/…Platform/**` + `mvp-host/tests/…Platform.Tests/**` | **∅** |
| 5 | 7, 8, 9 | `…Transport/**` + `…Transport.Tests/**` **vs** `…Auth/**` + `…Auth.Tests/**` **vs** `…WorldSlot/**` + `…Simulation.Reference/**` + `…WorldSlot.Tests/**` | **∅** |
| 6 | 10, 11 | `…Transport.WebSocket/**` + `…Transport.WebSocket.Tests/**` **vs** `…Session/**` + `…Session.Tests/**` | **∅** |

wave 0 / 1 / 3 / 4 / 7 / 8 / 9 各只有一张卡，无需互斥证明。

**串行强制项（文件重叠，不得并行）**：
- 卡 1 与卡 2 共享 `.github/workflows/repository-policy.yml`（卡 1 改 baseline 断言、卡 2 新增独立 dotnet job）→ 卡 2 依赖卡 1。
- 卡 2 与卡 3 共享 `mvp-host/eng/verify-all.{sh,ps1}` 与 `mvp-host/README.md`（卡 3 在卡 2 的版本上插入两条契约校验步骤并补记命令）→ 卡 3 依赖卡 2。
- 卡 14 与 51 卡的 `establish-cargo-workspace-and-rust-standards` 共享 `code-style.md` / `testing.md` → **跨批次串行**，且约定「后落地方只增量追加本语言小节」。该 Rust 卡当前 `status=pending`、`dependencies=[]`、wave=0、未开工，因此 C# 先落地可行。
- `queues.json` 分散在各生产工程目录内（§4.3），因此卡 7/8/9/11 各自新增自己那份，**不共享文件**。

**并行 worker 的合入纪律**（依据 `.spec/AGENTS.md`「并行边界与合入」）：并行 worker 各在独立 git worktree 实现，reviewer 审 worktree 相对基线的完整 diff，通过后主 loop 合入主工作区；未过审不合入，冲突退回实现方。

---

## 14. 三案冲突处的关键裁决记录

本文档由三份独立提案合成。以下逐条记录**冲突处的裁决与理由**（未冲突的一致结论不复述）。

| # | 冲突点 | 裁决 | 理由 |
|---|---|---|---|
| **J1** | **`body` 私有扩展：全面拒绝 vs 双向允许 vs 非对称** | **全面拒绝（本轮推翻上一版的非对称裁决）**：两个方向都不加字段；出站 body 字段集**恰好等于** `_REPLICATION_BODY_REQUIRED`（exact-set 机器断言）。同时**保留**子协议 token 承载，两者的原则性区别写进 §5.6-C 的对照表 | 上一版的非对称裁决建立在「顶层 `additionalProperties:false`（闭）而 `body` 开放 = 刻意的『信封冻结、body 可扩展』结构」这一推断上。该推断被一份直接对口的 **Accepted ADR 推翻**：`ADR-028-replication-typed-bodies.md`（Status **Accepted**，Baseline `LGE-V1.3-2026-08-27`，已进入 V1.4 基线，Owner LumioGameRuntime）的 Decision 为 8 个 messageType 各定义 required typed body，其 **Alternatives 原文**：`Keeping a free-form payload was rejected because two implementations can pass the gate and disagree on Snapshot identity.` —— 它否决的正是这个做法，否决理由（两个实现都能过门却对 Snapshot 身份不一致）恰是上一版造成的失效模式（双端需带外约定同一段不透明字节）。四条支撑：① `lumio_contract.py:355-364` 与 ADR-028 逐字一致，「只查缺失不查多余」是**机器门的能力边界，不是设计许可**；② `repository-architecture.md` 明文「不得在 Host 内自行改写公共 Envelope；先在架构源完成 ADR、Schema、Fixture 和新 Baseline」；③ §5.9 自订的 BLOCKED 第一条（需要新增公共字段即停手）适用于它自己；④ 非对称不成立——server→client 与 client→server 同属「公共面缺位 → 自行补一个」。代价被诚实计入：`FullSnapshot`/`Delta` 在 MVP 期确实语义上为空，因此 A1-α 只证明协议与生命周期闭环，「Bot 看到方块被挖」整体归 A1-β（B4 升级为硬前置）。**对照保留项**：子协议 token 承载落在**任何冻结公共产物之外**，且 MVP 计划 §2.2 / §4 **显式授权** auth 存根——与「扩展受 Accepted ADR 治理的冻结 typed body」不是同一类事，但仍按 §5.9 补齐三处登记。 |
| **J2** | **A1 退出条件的交付形态** | 拆成 **A1-α（17 步全自动，本批次交付）** 与 **A1-β（BLOCKED）**；A1-α 用**带外、仅回环、开关门控的测试控制面** `InjectWorldMutation` 代替被阻塞的上行链路 | 带外控制面不经任何 Envelope，因而根本不是 wire，不触碰任何冻结面；同时它让「注入变更 → 服务端 revision 递增 → 客户端在 Delta 中观察到该变更」这条完整因果链跨进程可验证。比「只验 revision 数字前进」有说服力，比「发明上行 wire」保真。 |
| **J3** | **DTO 命名与 body 建模** | 名字取 `MvpEnvelopeDocument`（`Mvp` 前缀刺眼 + 与目录同名 + `Document` 表明是 reader/writer 非 definition）；**body 保持 `JsonObject`，不生成任何 typed body POCO**；配**自过期守卫**测试 | 三提案各取一半：`Mvp` 前缀（isolation/exit）让临时性在每个调用点可见；无 typed body POCO（fidelity）挡住最容易硬化成事实标准的东西；自过期守卫（isolation）把「记得删」从人的记忆变成 CI 的义务。三者互补，无一冗余。 |
| **J4** | **`length` 处置：producer 写/consumer 不读 + IL 断言 vs 非对称口径** | 取**非对称口径**（入站只校验 `>= 0`，出站写 body 字节数），并**只**放弃 `length` 这一处的 IL 级断言「解析路径不访问 Length」 | **裁决范围仅限 `length` 这一处**：该不变量已被 fixture 回归门本身机器保证（8 条 fixture 全写 256，若入站交叉核对必红），再加一条脆弱的 IL 扫描是纯冗余。**本裁决不否决 IL/依赖级断言这一手段本身**——本设计仍有多处需要「谁调用了谁」级别的判定（队列构造点、gate 只判一次、故障策略不硬编码、`Session` 不调用客户端 writer）。这些统一用中央包表内的 `TngTech.ArchUnitNET` 的**方法调用依赖断言**实现（§4.3 的断言机制纪律、§7.4），不手写 `MethodBody.GetILAsByteArray` 扫描，也不引入任何未冻结的分析包。两者都判不了的条目（如「实现内不出现除 `FixedTimeEquals` 外的比较路径」）降级为签名级收敛 + 定向单测 + 评审项，并在卡面写明是哪一种。 |
| **J5** | **auth token 承载：不透明字节串 vs 子协议** | 取**子协议** `Sec-WebSocket-Protocol: lumio.mvp.v0, <token>, <nonce>` | 浏览器 WebSocket API **不能设自定义头但能设子协议**，而 MVP 终点形态是 A2 的浏览器体素客户端。选子协议使将来切浏览器时不需要改认证承载——这是一条有前瞻依据的技术理由，优于「随便挑一个头」。 |
| **J6** | **队列登记落点** | 每工程自带 `queues.json` + `Architecture.Tests` 聚合断言 | `modules/README.md` §4.2 被 Rust 卡独占不可动；而一份共享的 MVP 登记表会让卡 7/8/9/11 互相文件冲突。分散登记 + 机器聚合同时解决两者，且不牺牲七项合同的强制性。 |
| **J7** | **跨模块依赖：同层兄弟直连 vs 中央 `HostContracts`** | 取**中央 `HostContracts`（Layer 2）**，同层兄弟相互零引用 | 事件端口集中在契约层后，「事件不产生反向边」变成机制保证而非纪律；且分层用每工程自带的 `MvpHostLayer` 属性声明（不用共享 allowlist 文件），新增工程不产生卡间冲突。 |
| **J8** | **测试栈：VSTest vs MTP** | 取 **VSTest 路线**（LumioClient 口径），并写死反转触发条件 | A1 回环的对手方进程就是 LumioClient 的 bot host，运行器口径对齐显著降低跨进程调试成本；该组合已实测在 net10.0 + SDK 10.0.400 上还原成功。反转条件（Runtime.Adapter 进构建图时重评）写进工程基线，避免将来无据可循。 |
| **J9** | **`SmokeClient` 归属：testkit 内 vs 独立可执行** | 取**独立可执行工程** | A1-α 要求**真跨进程**（`LocalSplitProcess` 形态）；放在 testkit 内的库无法作为独立进程被拉起。多一个工程换来验收场景的保真度。 |
| **J10** | **CI job：塞进现有 `readme` job vs 新增独立 job** | 取**新增独立 dotnet job** | 现有 job 因 baseline grep 已红（实测），塞进去会被连坐，reviewer 无法区分红灯是既存缺陷还是新卡引入。独立 job 让 C# 侧的绿/红信号自洽。 |
| **J11** | **`Simulation.Reference` 档位：test-double vs production absence-filler** | 取 **production 档位的 absence-filler**，护栏改为禁用词表 + 被引用计数恰为 1（`App`） | A1-α 需要它在生产路径上跑且 `App` 引用它；若标为 test double 再让生产工程依赖，就违反了自己的依赖门禁。诚实标注档位、改用更强的内容护栏（零 ECS/Voxel/Gameplay 词汇），比伪装档位更可审计。 |
| **J12** | **fixture 总数断言** | **不硬编码架构源 fixture 总数**，只断言本仓镜像的 **16 条 fixture / 20 个受哈希锁文件** + BaselineId 哨兵 | 研究阶段记录 160，本轮实测 167——架构源在两次观测间已变更。任何基于总数的断言都会随上游变更误红。改用「镜像哈希 + BaselineId 断言」覆盖同一个漂移检测意图。 |

---

## 15. 缺席登记规范（`mvp-host/absences.json`）

机器可读，每条四元组。**该文件由卡 2 一次性建立全量 19 条，卡 3–14 只读校验、不追加**——原因是卡 3/4、7/8/9、10/11、13/14 分别同 wave 并行，若各卡追加同一文件就破坏「同 wave 文件集严格互斥」；而全部缺席项在设计阶段已可穷举（§2 右列、§6.3 的 `Faulted`、§6.4 的 `NativeReady` 与 7 条暂不进入的迁移、§11 的 G1/G2/G3/G9/G11/G12/**G17/G18/G19**），不需要在实现期发现。

**本轮新增三条（16 → 19）**，全部来自本轮对抗审查暴露的「私有约定未登记」：

| id | clause（摘要） | source | reason | successor |
|---|---|---|---|---|
| `ABS-REPLICATION-STATE-PAYLOAD` | 公共 `FullSnapshot` / `Delta` 的 typed body 无状态载荷字段；本仓不自行补（ADR-028 否决 free-form payload），MVP 期客户端观察不到世界内容 | `contract-mirror/schemas/replication-envelope.schema.json` | `决策门冻结` | `needs-new-card` |
| `ABS-REPLICATION-MAPPING-SET` | `mappingSetHash` 指向 LumioGame 拥有、MVP 期不存在的公共映射集；出站填本仓单点常量（provisional），入站只校验 `hash256` 正则 | `contract-mirror/schemas/replication-envelope.schema.json` | `阶段未到` | `needs-new-card` |
| `ABS-AUTH-CREDENTIAL-CARRIAGE` | 通道认证凭据与 nonce 的线承载无公共定义；MVP 取 `Sec-WebSocket-Protocol` 子协议位序 `lumio.mvp.v0, <token>, <nonce>` | `docs/architecture/DECISIONS_PENDING.md` D-011 | `决策门冻结` | `needs-new-card` |

> `source` 必须指向**本仓内真实存在的路径**（`Architecture.Tests` 的 `AbsencesManifestTest` 会校验）。上表第三条的 `source` 指架构源文件，因此在 `absences.json` 里写成本仓镜像内的等价落点 `contract-mirror/MIRROR.md`（其中记录来源仓与 commit），并把 D-011 的坐标写进 `clause` 文本——**不得**写一个仓内不存在的路径。这与 §4.3 队列登记「分散登记 + 机器聚合」的取舍方向相反，理由也相反：队列条目随实现产生、缺席条目随设计产生。

格式（卡 2 建立文件时按此格式）：

```json
{
  "baselineId": "LGE-V1.4-2026-08-27",
  "absences": [
    {
      "id": "ABS-WORLDSLOT-NATIVE",
      "clause": "WorldSlotHost 的 NativeReady 状态承载 Native 库加载；MVP 无 Native 可加载",
      "source": "contract-mirror/fixtures/valid/state-machine-world-slot-host.json (transitions[1..2])",
      "reason": "阶段未到",
      "successor": "implement-world-slot-aggregate-epoch-admission-and-quota"
    },
    {
      "id": "ABS-RELEASE-EXACTMATCH",
      "clause": "ExactRelease 的完整语义（Catalog 消费、Manifest 校验、Pool 成员健康）",
      "source": "modules/session/README.md 与 modules/release-agent/README.md",
      "reason": "实现方为 P1",
      "successor": "implement-release-catalog-manifest-verification"
    },
    {
      "id": "ABS-AUDIT-DURABLE-ACK",
      "clause": "Audit durable ack、背压关闸的完整语义、Failure Bundle 装配",
      "source": "modules/auth/README.md 与 modules/observability/README.md",
      "reason": "实现方为 P1",
      "successor": "implement-observability-audit-durable-pipeline"
    },
    {
      "id": "ABS-CLIENT-UPLINK-COMMAND",
      "clause": "客户端上行 gameplay 命令的 wire 承载（A1-β）",
      "source": "docs/architecture/DECISIONS_PENDING.md D-009",
      "reason": "决策门冻结",
      "successor": "needs-new-card"
    }
  ]
}
```

`reason` 只能取四值之一：`载体已提供` / `阶段未到` / `决策门冻结` / `实现方为 P1`。**「简化」「暂时」不是合法理由。** `successor` 是承接的既有 Rust 卡 slug，或字面量 `needs-new-card`。`Architecture.Tests` 断言：每条含全部四个字段、`reason` 在枚举内、`source` 指向的路径存在（`contract-mirror/` 内的路径可解析）。
