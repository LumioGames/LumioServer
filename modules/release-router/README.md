# release-router 模块

> ReleaseCatalog 消费、Manifest/签名/Capability 校验、Pool 状态机、健康检查与路由决策。

## 模块定位与目标

`release-router` 拥有"这个进程服务哪个 Release、这个连接应该去哪个 Pool、Pool 现在处于什么状态"的裁决。`ReleaseCatalog` 是签名、版本化的产品/版本/Artifact/Capability/路由清单，路由键至少是 `productId + gameReleaseId`；`A 1.1` 与 `BOE 2.1` 可以同时在线，但每个进程只加载一个 Release（架构源 §13.1、ADR-012）。Manifest、ABI、签名、SBOM、Capability 或健康检查任一失败都阻止进入 `Serving`。

## 负责什么

- ReleaseCatalog 装载与消费：校验 Catalog 签名与 `baselineId`，维护本进程持有的 Catalog 副本；重复路由键拒绝（对应架构源反例 Fixture `fixtures/invalid/release-catalog-duplicate-route.json`）。
- ReleaseManifest 校验：`manifestHash`、`serverAssemblyHash`、`gameplayContractHash`、`runtimeApi`、`coreEngineAbi`、`networkProtocol`、`signature`、`sbom`、`capabilities` 全量校验；任一失败阻止 Serving（架构源 ADR-012 失败语义）。
- Pool 状态机维护：`Published -> Verified -> Warmup -> Serving`，旧 Pool `Draining -> Empty -> Retired`，任一阶段可 `Rollback / Faulted`；状态迁移由 [maintenance](../maintenance/README.md) 编排触发、本模块执行并记录。
- 路由决策：握手中的 `productId + gameReleaseId` → 目标 Pool/Endpoint；`ExactRelease` 精确匹配（D-007 默认），不匹配返回稳定错误与强制更新指引。
- 健康检查：新 Pool 通过健康检查才接收新 Session（SRV-D-007）；`healthy` 与 `activeSessions` 状态回写 Catalog 视图。
- 版本固定记录：为 [session](../session/README.md) 提供"该 Session 固定在哪个 Release"的裁决依据；重连路由只允许 Catalog 中允许的目标 Release。

## 明确不负责什么

- 不组装 Release（Manifest 生成、签名、Release Composition 归 `LumioGame`）。
- 不定义 Catalog/Manifest Schema、Pool 状态枚举或兼容策略枚举（归架构源）。
- 不执行维护命令编排（归 [maintenance](../maintenance/README.md)）；本模块是 Pool 状态的执行者与记录者，不是发起者。
- 不装载 Assembly（归 [coreclr-host](../coreclr-host/README.md)）；本模块校验 Manifest 后把结果交给装载方。
- 不管理 Session 生命周期（归 [session](../session/README.md)）。

## 拥有的状态与资源

- 已验证的 ReleaseCatalog 副本与其签名/版本元数据。
- 本进程绑定的 `productId + gameReleaseId + releasePoolId` 身份。
- Pool 状态机当前态与迁移历史（可审计）。
- 健康检查调度器状态与最近结果。

## 输入、输出与稳定接口

- **输入**：签名 ReleaseCatalog（部署分发）、目标 Release 的 Manifest 与 Artifact 引用、维护编排的状态迁移指令、健康探测结果。
- **输出**：Manifest 校验结论（供 coreclr-host 装载）、路由裁决（供 session Admission）、Pool 状态与健康视图（供 maintenance 与运维）、稳定拒绝错误（版本不匹配/未知路由）。
- **稳定接口**：`validate_manifest(manifest) -> Verified | StableError`；`route(productId, gameReleaseId) -> PoolTarget | StableError`；`transition_pool(poolId, targetState, evidence) -> Ok | StableError`；`health() -> PoolHealth`。

## 上游与下游依赖

- **上游**：[session](../session/README.md)（Admission 期路由与匹配）、[maintenance](../maintenance/README.md)（Pool 状态迁移编排）、[process](../process/README.md)（启动期 Manifest 校验）。
- **下游**：[network](../network/README.md)（Endpoint 配置）、[observability](../observability/README.md)（Audit 与 Metrics）。

## 生命周期与状态机

Pool 状态机是公共契约（架构源 §13.2，枚举与 `schemas/release-catalog.schema.json` 的 `state` 字段一致）：

```text
Published -> Verified -> Warmup -> Serving
Old Serving -> Draining -> Empty -> Retired
任一阶段 -> Rollback / Faulted
```

- 本模块启动期完成 `Published -> Verified`（Manifest 全量校验）；`Warmup -> Serving` 需健康检查通过；`Draining` 后停止新接入但继续服务存量 Session 直至自然结束、显式迁移或维护期限（D-002）。
- `Rollback` 保留旧的已验证 Pool 与 Snapshot（架构源 ADR-012 失败语义）。

## 线程、队列与并发所有权

- 健康检查在独立的低频调度线程执行（周期 SRV-D-007）；路由查询是无锁只读（Catalog 副本不可变，更新走原子替换）。
- 无业务队列；状态迁移是低频编排动作，串行执行并全程记录。

## 正常数据流与失败路径

- **正常**：Catalog 装载校验 → 本进程 Release 的 Manifest 校验 → `Verified` → coreclr-host 装载 → Warmup 健康检查 → `Serving` → 为每个握手提供路由裁决。
- **失败路径**：
  - Catalog 签名无效/`baselineId` 不符/重复路由键：拒绝装载，进程不进入 Serving。
  - Manifest 校验失败（Hash/签名/SBOM/ABI/Capability 任一）：`Faulted`，阻止装载（对应架构源反例 Fixture `fixtures/invalid/release-manifest-mismatch.json`）。
  - 健康检查连续失败：Pool 标 unhealthy，不接新 Session；升级处置由 maintenance 编排（Rollback 或 Faulted）。
  - 路由未命中/版本不匹配：稳定错误 + 强制更新指引；不做任何隐式降级路由。

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

- Pool 状态每次迁移写 Audit（关联 `releasePoolId`、`gameReleaseId`、证据引用）。
- Metrics：路由命中/拒绝率（按稳定原因）、健康检查成功率、各 Pool `activeSessions`。
- 路由、Drain 与踢人事件共用公共 correlation 字段（架构源 ADR-012 契约条款）。

## 测试面、故障矩阵与性能指标

- **测试面**：`A 1.1` 与 `BOE 2.1` Manifest 并行校验、mismatch 失败、Warmup/Drain/Rollback 全状态迁移、并发 Pool 隔离（架构源 ADR-012 验证清单）、Hash/Signature/Capability 拒绝（根 README Headless Test Surface）。
- **故障矩阵**：Catalog 重复路由、签名损坏、健康检查抖动、Draining 中的新接入拒绝与存量保持。
- **性能指标**：路由查询延迟（Admission 路径预算内）、Catalog 原子更新停顿、健康检查开销。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-012-release-update-maintenance.md`。
- 架构源 `schemas/release-catalog.schema.json`：正例 `fixtures/valid/release-catalog.json`，反例 `fixtures/invalid/release-catalog-duplicate-route.json`。
- 架构源 `schemas/release-manifest.schema.json`：正例 `fixtures/valid/release-manifest-a-1.1.json`、`fixtures/valid/release-manifest-boe-2.1.json`；反例 `fixtures/invalid/release-manifest-mismatch.json`。

## 尚未批准的决策门

- **D-001**（一进程一 Release）：临时默认值即本设计；多 Release 进程内共存需新 ADR。
- **D-007**（N/N-1 兼容）：临时默认值为精确匹配拒绝；`DeclaredNMinusOne` 枚举值仅为 Schema 预留，启用需新 ADR、握手规则与 Fixture。
- **SRV-D-007**（健康检查周期与阈值）：临时默认值为 5 秒周期、连续 3 次失败标 unhealthy；Production Hardening 阶段确认。均登记于 [modules/README.md](../README.md) §11。
