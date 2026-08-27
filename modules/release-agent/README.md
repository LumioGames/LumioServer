# release-agent 模块

> 本进程的 Release 身份代理：Catalog 消费、Manifest/签名/Capability 校验、本 Pool 成员状态、健康检查与 ExactRelease 接入匹配。

## 模块定位与目标

`release-agent` 拥有"**本进程**服务哪个 Release、这次握手与本进程是否精确匹配、本进程所属 Pool 成员处于什么状态"的裁决。它是代理而非路由器（v1.1 更名裁决，见 [modules/README.md](../README.md) §10.1）：集群层面"哪些 Pool 存在、流量如何分配、何时替换实例"的期望状态归外部控制面（架构源 ADR-012），本模块只消费签名 `ReleaseCatalog` 做校验与匹配，并维护本进程这一个 Pool 成员的状态与健康。`A 1.1` 与 `BOE 2.1` 同时在线是两组进程各自的事实，不是本模块内的路由表。Manifest、ABI、签名、SBOM、Capability 或健康检查任一失败都阻止本进程进入 `Serving`。

## 负责什么

- ReleaseCatalog 装载与消费：校验 Catalog 签名与 `baselineId`，维护本进程持有的只读副本；重复路由键拒绝（对应架构源反例 Fixture `fixtures/invalid/release-catalog-duplicate-route.json`）。
- ReleaseManifest 校验：`manifestHash`、`serverAssemblyHash`、`gameplayContractHash`、`runtimeApi`、`coreEngineAbi`、`networkProtocol`、`signature`、`sbom`、`capabilities` 与 `coreEnginePackage` 精确引用块（`packageId + manifestDigest + artifactSetDigest + abiIdentity + targetProfileDigest`，其 `abiIdentity` 必须与 `coreEngineAbi` 声明一致——架构源 §13.1/ADR-018 语义）全量校验；任一失败阻止 Serving（架构源 ADR-012 失败语义）。
- 本进程身份固定：启动期绑定 `productId + gameReleaseId + releasePoolId`，运行期不可变。
- 本 Pool 成员状态机执行：公共 Pool 状态机中属于本进程的片段——启动期 `Published -> Verified -> Warmup -> Serving`，维护期 `Serving -> Draining -> Empty -> Retired`；迁移由 [maintenance-agent](../maintenance-agent/README.md) 类型化命令触发、本模块执行并记录。**新 Pool 的目标实例状态发生在目标进程内**，本进程不驱动它。
- ExactRelease 接入匹配：握手中的 `productId + gameReleaseId` 与本进程身份精确比对（D-007 默认），不匹配返回稳定错误与强制更新指引；重连目标的合法性依据 Catalog（重连被路由到哪个实例是集群层动作，本模块只裁决"能不能接"）。
- 健康检查：自身健康探测（周期 SRV-D-007，Timer 经 [host-runtime](../host-runtime/README.md)）；`healthy` 与 `activeSessions` 视图经 [control-plane-adapter](../control-plane-adapter/README.md) 报告给控制面。
- 版本固定依据：为 [session](../session/README.md) 提供"该 Session 固定在哪个 Release"的裁决依据。

## 明确不负责什么

- 不拥有集群期望状态、跨进程流量分配或实例替换时机（归外部控制面）；不维护其他进程/Pool 的状态。
- 不组装 Release（Manifest 生成、签名、Release Composition 归 `LumioGame`）。
- 不定义 Catalog/Manifest Schema、Pool 状态枚举或兼容策略枚举（归架构源）。
- 不执行维护命令编排（归 maintenance-agent）；本模块是本 Pool 成员状态的执行者与记录者，不是发起者。
- 不装载 Assembly（归 [coreclr-host](../coreclr-host/README.md)）；本模块校验 Manifest 后把结论交给装载方。
- 不管理 Session 生命周期（归 session）。

## 拥有的状态与资源

- 已验证的 ReleaseCatalog 只读副本与其签名/版本元数据。
- 本进程绑定的 `productId + gameReleaseId + releasePoolId` 身份（不可变）。
- 本 Pool 成员状态机当前态与迁移历史（可审计）。
- 健康检查调度状态与最近结果。

## 输入、输出与稳定接口

- **输入**：签名 ReleaseCatalog（部署分发）、目标 Release 的 Manifest 与 Artifact 引用、maintenance-agent 的状态迁移命令、健康探测结果。
- **输出**：Manifest 校验结论（供 coreclr-host 装载）、ExactRelease 匹配裁决（供 session Admission）、本 Pool 成员状态与健康视图（供 maintenance-agent 与 control-plane-adapter）、稳定拒绝错误（版本不匹配/未知路由键）。
- **稳定接口**：`validate_manifest(manifest) -> Verified | StableError`；`match_release(productId, gameReleaseId) -> Ok | StableError`；`transition_pool_member(targetState, evidence) -> Ok | StableError`；`health() -> PoolMemberHealth`。

## 上游与下游依赖

- **上游**：[session](../session/README.md)（Admission 期匹配）、[maintenance-agent](../maintenance-agent/README.md)（状态迁移命令）、[process](../process/README.md)（启动期 Manifest 校验）。
- **下游**：[transport](../transport/README.md)（仅类型/Endpoint 数据结构的编译依赖；Endpoint 配置经 [process](../process/README.md) 组装期接线送达 transport，运行期无命令边）、[control-plane-adapter](../control-plane-adapter/README.md)（健康/身份上报）、[host-runtime](../host-runtime/README.md)（健康检查 Timer）、[observability](../observability/README.md)（Audit 与 Metrics）。

## 生命周期与状态机

Pool 状态机是公共契约（架构源 §13.2，枚举与 `schemas/release-catalog.schema.json` 的 `state` 字段一致）；本模块执行其中本进程成员的片段：

```text
Published -> Verified -> Warmup -> Serving        （本进程启动期）
Serving -> Draining -> Empty -> Retired            （本进程维护/退役期）
任一阶段 -> Rollback / Faulted
```

- 启动期完成 `Published -> Verified`（Manifest 全量校验）；`Warmup -> Serving` 需健康检查通过。
- `Draining` 后本进程停止新接入（经聚合根闸门）但继续服务存量 Session 直至自然结束、显式迁移或维护期限（D-002）。
- `Rollback` 保留旧的已验证资产与 Snapshot（架构源 ADR-012 失败语义）。

## 线程、队列与并发所有权

- 无自有线程；健康检查由 host-runtime Timer 投递的类型化命令驱动，在低频控制上下文执行。
- 匹配查询是无锁只读（Catalog 副本与身份不可变，更新走原子替换）；状态迁移串行执行并全程记录。

## 正常数据流与失败路径

- **正常**：Catalog 装载校验 → 本进程 Release 的 Manifest 校验 → `Verified` → coreclr-host 装载 → Warmup 健康检查 → `Serving` → 为每个握手提供匹配裁决。
- **失败路径**：
  - Catalog 签名无效/`baselineId` 不符/重复路由键：拒绝装载，进程不进入 Serving。
  - Manifest 校验失败（Hash/签名/SBOM/ABI/Capability 任一）：`Faulted`，阻止装载（对应架构源反例 Fixture `fixtures/invalid/release-manifest-mismatch.json`）。
  - 健康检查连续失败：本成员标 unhealthy，经 control-plane-adapter 上报；处置（替换/回滚）由控制面决定。
  - 匹配失败/未知路由键：稳定错误 + 强制更新指引；不做任何隐式降级匹配。

## 错误分类、恢复与降级

- **可重试**：健康探测瞬时失败（按阈值累计）。
- **可拒绝**：版本不匹配、未知路由键、Catalog/Manifest 校验失败。
- **可致命**：本进程绑定 Release 的 Manifest 校验失败（无法提供服务，进程级处置）。
- **降级**：无隐式降级；`Rollback` 是显式编排动作且保留旧资产。

## 配置、Capability 与安全约束

- Catalog 分发与信任锚来自签名配置；Manifest `capabilities` 与 [host-profiles](../host-profiles/README.md) 声明在启动期核对。
- 目标 Pool 之外的产品/Release 不得被默认影响（根 [README.md](../../README.md) Architecture Gate）。
- 兼容判定只认 `compatibilityPolicy` 枚举；不得从 semver 推断兼容性（架构源 ADR-012）。

## 日志、Metrics、Trace 与 Audit

- 本 Pool 成员状态每次迁移写 Audit（correlation `scope` 为 `Release`，关联 `releasePoolId`、`gameReleaseId`、证据引用）。
- Metrics：匹配命中/拒绝率（按稳定原因）、健康检查成功率、本成员 `activeSessions`。
- 匹配、Drain 与踢人事件共用公共 correlation 字段（架构源 ADR-012 契约条款）。

## 测试面、故障矩阵与性能指标

- **测试面**：`A 1.1` 与 `BOE 2.1` Manifest 并行校验、mismatch 失败、Warmup/Drain/Rollback 本成员状态迁移、并发 Pool 隔离（架构源 ADR-012 验证清单）、Hash/Signature/Capability 拒绝（根 README Headless Test Surface）。
- **故障矩阵**：Catalog 重复路由键、签名损坏、健康检查抖动、Draining 中的新接入拒绝与存量保持。
- **性能指标**：匹配查询延迟（Admission 路径预算内）、Catalog 原子更新停顿、健康检查开销。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-012-release-update-maintenance.md`。
- 架构源 `schemas/release-catalog.schema.json`：正例 `fixtures/valid/release-catalog.json`，反例 `fixtures/invalid/release-catalog-duplicate-route.json`。
- 架构源 `schemas/release-manifest.schema.json`：正例 `fixtures/valid/release-manifest-a-1.1.json`、`fixtures/valid/release-manifest-boe-2.1.json`；反例 `fixtures/invalid/release-manifest-mismatch.json`。

## 尚未批准的决策门

- **D-001**（一进程一 Release）：本模块按临时默认值推进（provisional、未冻结）；多 Release 进程内共存需新 ADR。
- **D-007**（N/N-1 兼容）：临时默认值为精确匹配拒绝；`DeclaredNMinusOne` 枚举值仅为 Schema 预留，启用需新 ADR、握手规则与 Fixture。
- **D-010**（控制面命令传输与期望状态存储）：健康上报通道随其确认。
- **SRV-D-007**（健康检查周期与阈值）：临时默认值为 5 秒周期、连续 3 次失败标 unhealthy；Production Hardening 阶段确认。均登记于 [modules/README.md](../README.md) §11。
