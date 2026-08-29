# contract-mirror — 架构源契约的只读字节级镜像

本目录是 `LumioGameEngineArchitecture` 公共契约的**只读镜像**，让本仓的协议测试今天就能独立跑绿，
不必靠环境变量指向兄弟仓（那在 CI 上等于没有这道门）。

**本目录下的任何文件都不得手改。** 唯一的更新路径是同步命令，且必须与哈希清单一起提交。
本文件（`MIRROR.md`）是唯一例外——它是本仓手写的说明，架构源没有对应文件。

| 项 | 值 |
|---|---|
| 来源仓 | `LumioGameEngineArchitecture`（本机 `/Users/cui/LumioGames/LumioGameEngineArchitecture`；路径按机器不同，靠 `$LUMIO_ARCHITECTURE_ROOT` 指定） |
| 来源 ref | `origin/main`（可用 `$LUMIO_ARCHITECTURE_REF` 覆盖） |
| 来源 commit | `664ccd6cf77751190942439b9a4ac08184becdb6` |
| 测量时刻 | 2026-08-29（架构源当天前进多次，跨仓状态几十分钟即过期——判断同步状态请当场重测，别信本行） |
| BaselineId | `LGE-V1.4-2026-08-27` |
| 哈希清单 | [`../eng/contract-mirror.sha256`](../eng/contract-mirror.sha256)（29 个受锁文件） |
| **退场条件** | 架构源发布可用的 C# 契约包（而非源码 artifact）后，本镜像随本仓 DTO 一并删除。 |

## 命令

```bash
cd mvp-host && bash eng/sync-contract-mirror.sh              # 从架构源重新同步并重写哈希清单
cd mvp-host && bash eng/verify-contract-mirror.sh            # 校验；漂移退出码 33
cd mvp-host && bash eng/verify-contract-mirror.sh --self-test  # 对照组探针：证明守护真的会响
```

`sync` 需要 `$LUMIO_ARCHITECTURE_ROOT`；`verify` 的第一条检查不需要。Windows 用同名 `.ps1`。

跨仓一律**只读已提交对象**（`git show <ref>:<path>`），**绝不读他仓工作区**——他仓的 HEAD
可能正被另一个会话切换，读工作区会读到半截状态或误判文件不存在。

## 守护是两条独立检查，不是一条

| 检查 | 需要架构源 | 失败后果 |
|---|---|---|
| ① **产物未被手改**：本地文件 vs `eng/contract-mirror.sha256` | 否 | 退出码 `33`，逐条打印 `MVP_HOST_MIRROR_DRIFT <kind> <path>` |
| ② **与上游同步**：清单哈希 vs 架构源当前 ref | 是 | **只报告**，`MVP_HOST_MIRROR_UPSTREAM behind=N`，不影响退出码 |

拆开是因为两者性质完全不同：①是事故（有人手改了本该只读的镜像），②是日常（上游 additive
增补正是被鼓励的，落后于上游不是本仓的错误状态）。合成一条的结果是红灯常亮、然后被无视。

守护本身必须能被证伪，否则「有一份看起来在守护的东西」就是自欺。`--self-test` 在临时目录里
造镜像、篡改一个字节、确认检查①确实返回 33，再确认原样返回 0——对照组与实验组都跑。

## 本目录下不放任何 `.csproj` / `.cs` / `Directory.Build.*`

契约生成物走**源码拷贝**而非工程引用。实测理由：架构源的 6 个生成工程原样是 `net8.0`，
而本构建根的 `Directory.Build.targets` 对每个 SDK 工程硬断言 `TargetFramework == net10.0`。
在子目录放空壳 `Directory.Build.props` **拦不住**父级 `Directory.Build.targets`——MSBuild 对
`.props` 与 `.targets` 的向上查找**各自独立**，`.props` 命中即停不影响 `.targets` 继续上溯。
构建输出 `VALIDATE-RAN TFM=net8.0 RootPropsSeen=` 后随即
`error : TFM must be net10.0 but was net8.0` 与 `Build FAILED`。

因此 `.cs` 拷进 [`../src/Lumio.Server.MvpHost.GeneratedContracts/Generated/`](../src/Lumio.Server.MvpHost.GeneratedContracts/Generated/)
随本工程以 `net10.0` 编译，`.csproj` 一个都不拷。该目录另有自己的哈希锁与
`bash eng/verify-generated-contracts.sh`（漂移退出码 `32`）。

验证本条：`cd mvp-host && find contract-mirror -name '*.csproj' -o -name '*.cs' -o -name 'Directory.Build.*'` 应无输出。

## 镜像清单（29 个受哈希锁文件）

`MIRROR.md` 不在其中——架构源没有对应文件，进清单会与「与架构源字节相同」互斥。

### schemas/（6 份）

`common` 是其余五份的 `$ref` 闭包底座，必须在场。

| 文件 | 为什么在这里 |
|---|---|
| `replication-envelope.schema.json` | 复制信封的结构真值。含 **9 条 `allOf` / `if`-`then`**，每个 `messageType` 一条，`body` 带 `additionalProperties: false`（ADR-045 的 body 封闭性）。 |
| `common.schema.json` | 上面五份的 `$ref` 目标（`hash256` / `id` / `revision` / `sessionReleaseTriple` / `sessionRevisionVector` …）。 |
| `protocol-permission-gate.schema.json` | permission gate 判定的结构真值。 |
| `logging-event.schema.json` | audit 事件的结构真值。 |
| `replication-mapping.schema.json` | `fixtures/invalid/replication-mapping-empty-field.json` 的归属 schema——镜像了 fixture 就必须镜像它的 schema，否则集合不自洽。 |
| `session-revision-vector.schema.json` | `FullSnapshot.body.sessionRevisionVector` 在信封 schema 里只是 `{"type":"object"}` 空壳，结构真值在这里。 |

### canonical/（1 份）

| 文件 | 为什么在这里 |
|---|---|
| `canonical-digest-profile.json` | `mappingSetHash` 的 golden 出处（源路径 `packages/canonical/`）。ADR-045 §2 把它定义为 ADR-041 的 `ReplicationMappingSetV1` 域摘要：空映射集的值是 `a805f7c841f708981cc82a93047d7b0c8e6bf923f3dba18e179036741a6d2ea7`（canonicalBytes = `{"digestDomain":"ReplicationMappingSetV1","mappings":[]}`），**不是空串、不是全零、不是省略成员**——三种 sentinel 都被该 ADR 明文否决。有了这份 golden，下游卡能自算复核该常量，而不是抄一串字面量。 |

### fixtures/valid/（11 条）

8 条 replication 正向（`handshake` / `full-snapshot` / `baseline-ack` / `delta` / `delta-ack` /
`resync` / `maintenance-kick` / `error`）+ `protocol-permission-gate-accept` +
`state-machine-world-slot-host` + `logging-auth-reject-audit`。

### fixtures/invalid/（11 条）

**10 条 replication 反向全集**（文件名以 `replication-` 起头的全部 invalid fixture）：
`ack-smuggled-command`、`body-extra-member`、`gap-without-resync`、`integrity-value-mismatch`、
`length-exceeds-max`、`mapping-empty-field`、`mapping-set-hash-type`、`missing-snapshot-identity`、
`unregistered-message-type`、`unreliable-full-snapshot`；外加 `protocol-permission-gate-stale-generation`。

取**全集**而非挑选，是因为「挑哪几条」需要一次判断，而判断会随上游增补腐烂；
「凡 `replication-` 前缀的反例全要」是机器可判的规则，上游新增反例时 `sync` 之后清单加一行即可。

`session-revision-legacy-chunk-key.json` 与 `session-revision-negative.json`
**不镜像**：它们校验的是裸的版本向量对象，不是 replication 信封，属另一条测试链路。

## 已知缺口

`fixtures/valid/state-machine-world-slot-host.json` 的归属 schema 是 `state-machine-descriptor`，
本仓**不镜像**该 schema（沿用卡面写定的清单，本卡不擅自扩集）。因此这一条 fixture
目前只能作数据读取、不能在本仓做结构层校验。需要时另开卡补 schema，别在本卡外偷偷加。

## 新增或删除一项镜像

`eng/contract-mirror.sha256` 的**路径列**就是「要镜像哪些文件」的真值，`sync` 只按它逐条重拷。

- 新增：先在清单里加一行 `0000000000000000000000000000000000000000000000000000000000000000  contract-mirror/<路径>`，再跑 `sync`，哈希由脚本填实。
- 删除：删掉那一行再跑 `sync`（文件随之被清掉）。

源路径由镜像路径推导：`contract-mirror/canonical/X` ← `packages/canonical/X`，
其余 `contract-mirror/Y` ← `Y`（`schemas/…` 与 `fixtures/…` 与架构源同构）。

## 本文件不得改名或移除

[`../absences.json`](../absences.json) 的 `ABS-AUTH-CREDENTIAL-CARRIAGE` 把 `source` 指向本文件路径
（实测：`absences.json` 第 135 行 `"source": "contract-mirror/MIRROR.md"`）。

该路径的存在性目前**还没有**机器门禁——校验它的 `AbsencesManifestTest` 属
`define-mvp-host-contracts-and-audit-surface`（尚未交付），本仓当前没有 `Architecture.Tests` 工程。
也就是说本卡落地之前，那条 `source` 指向的是一个**不存在的路径**，没有任何东西会响；
本卡落地之后它才成立。在那条门禁到位前，改名或移除本文件不会变红——正因如此才写在这里。
