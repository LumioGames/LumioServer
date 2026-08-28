# coreclr-host 模块

> CoreCLR 与稳定 Runtime 装载、ServerGameplay Collectible ALC 生命周期、异常到稳定错误码转换与故障分级。

## 模块定位与目标

`coreclr-host` 是 Rust Host 与 Managed 世界之间的唯一桥梁。一个进程只启动一个 CoreCLR、只加载一个 CoreEngine 包、只服务一个 GameRelease（决策门 D-001 默认值）；Gameplay 以 Collectible ALC 装载并可热重载。本模块保证跨边界只有稳定 ABI 与稳定错误码，Managed 异常与 Rust panic 都不会以原始形态穿过边界。捕获只是**搬运**故障，不裁决其波及面（架构源 ADR-006，v1.1）：模拟路径上被捕获的失败带回稳定 Error Code 加 Runtime 见证的 `FaultClass`，本模块原样转交 [world-slot](../world-slot/README.md) 聚合根裁决——hosting 桥自己永不判定"这是 Session 级还是 Slot 级"。

## 负责什么

- CoreCLR 启动与配置：进程唯一 CoreCLR 实例的初始化参数、GC 模式与关闭时序。
- CoreEngine 包装载协调：调用 CoreEngine Loader 装载唯一 Native 组合；Loader 对第二版本、符号冲突、ABI/Capability 不匹配和重复释放的拒绝语义归 `LumioCoreEngine`（架构源 §8.2），本模块负责调用与结果处置。
- 稳定 Runtime 装载：装载 `LumioGameRuntime` Managed Host，完成 `RuntimeApiV*` 版本校验。
- Gameplay ALC 生命周期：ServerGameplay Assembly 的 Collectible ALC 装载、激活、热重载与卸载；卸载遵循 `Quiesce -> Cancel -> Drain -> Dispose -> Root 验证 -> Unload` 顺序，配合 Runtime 的 `GameplayModuleScope` 契约。
- ABI 校验：按 `NativeManagedAbiV1` 校验 `abi_version`、`struct_size`、`capability_bits`、指针宽度与端序；不匹配在 World 创建前失败（架构源 ADR-006）。
- 异常/panic 转换：Rust 侧捕获 panic、Managed 入口捕获 Exception，统一映射为稳定 Error Code。
- 故障见证转交：把 Runtime 见证的 `FaultClass`（`SessionLocalProven`/`SlotStateUnproven`/`ProcessFault`，架构源 ID Registry）连同 Error Code 原样转交 world-slot；**缺见证的捕获故障按 `SlotStateUnproven` 转交**（默认从严，架构源 ADR-006）。OOM、Stack Overflow、CoreCLR 崩溃、Native UB 恒为 `ProcessFault`（交 [process](../process/README.md) 处置）。
- Managed Tick 入口封装：为 Simulation Owner Thread 提供唯一的 Runtime Tick 调用入口；该线程是唯一 Managed Tick 入口（架构源 §8.1）。

## 明确不负责什么

- 不定义 ABI、Loader 拒绝规则或生成 Header（归 `LumioCoreEngine`/`LumioNativeCore`）。
- 不拥有 ALC 内部的 Managed 对象生命周期与 Hot Reload 契约语义（归 `LumioGameRuntime`）。
- 不拥有 Simulation Owner Thread（归 [world-slot](../world-slot/README.md)）；本模块提供入口，不提供线程。
- 不加载第二套 Native 包、不支持一进程多 Release（D-001 变更需新 ADR 并更新架构源 ADR-006/012 与 Loader Schema）。
- 不裁决故障波及面：不从"异常可捕获"推断状态一致性，不决定隔离 Session 还是恢复 Slot（裁决归 [world-slot](../world-slot/README.md) 依据 Runtime 见证执行）；不解释 Gameplay 异常的业务含义。

## 拥有的状态与资源

- CoreCLR 实例句柄与装载状态。
- CoreEngine 包句柄、Root API Table 指针与 `capability_bits`。
- 稳定 Runtime 句柄与 `RuntimeApiV*` 版本记录。
- Gameplay ALC 状态机与当前活动模块的 Hash/签名记录。

## 输入、输出与稳定接口

- **输入**：ReleaseManifest 中的 Assembly Hash 与 ABI 要求（经 [release-agent](../release-agent/README.md) 校验后转入）、`LoadGameplay/UnloadGameplay` 命令（仅来自 world-slot 聚合根）、Tick 调用（来自 Simulation Owner Thread）。
- **输出**：Runtime Tick 入口封装、稳定 Error Code + Runtime 见证 `FaultClass` 的转交、ALC 状态迁移事件。
- **稳定接口**：`load_runtime(manifest) -> RuntimeHandle | StableError`；`load_gameplay(alcRequest) -> ScopeHandle | StableError`；`tick_entry(slotContext, batch) -> TickResult | (ErrorCode, FaultClass)`；`unload_gameplay(scope) -> Ok | LeakEvidence`。

## 上游与下游依赖

- **上游**：[world-slot](../world-slot/README.md)（Tick 入口调用、`LoadGameplay/UnloadGameplay` 命令、World 创建前的 ABI 校验——热重载/停机对本模块的影响只经聚合根命令传导，维护语义不直达本模块）、[process](../process/README.md)（组装期初始化/析构）。
- **下游**：[host-profiles](../host-profiles/README.md)（Native/Runtime Capability 位）、[observability](../observability/README.md)（事件与 Failure Bundle 素材）。

## 生命周期与状态机

```text
CoreClrDown -> CoreEngineLoaded -> CoreClrStarted -> RuntimeLoaded
 -> GameplayLoaded -> GameplayActive <-> GameplayQuiescing（热重载/卸载）
 -> GameplayUnloaded（Root 验证通过）
任一活动状态 -> ProcessFault（进程级故障，不可原地恢复）
```

- 热重载：新模块装入新 ALC → 校验 Hash/签名/ABI → Quiesce 旧 Scope → 切换 → 卸载旧 ALC；失败时卸载新模块并保留最后有效活动模块（架构源 ADR-014 失败语义）。热重载仅限**同 Release 内**的模块热更；Active Session 内不得跨 Release 替换 Gameplay Scope（架构源 §13.1，v1.2 明文）——Release 切换只能走新 Pool 进程。
- CoreCLR 一经启动不支持进程内重启；`ProcessFault` 的恢复路径是进程重启 + Snapshot/WAL 恢复。

## 线程、队列与并发所有权

- 无自有线程；`nethost`/`hostfxr` discovery、CoreCLR 原生 bootstrap 与 function-table 获取可在 `host-runtime` 监督的 control context 完成，此时尚未进入 Managed Ready。
- Simulation Owner Thread 绑定后，Managed delegate 的初始化、Gameplay load/unload 与 Tick 调用全部只在该 Owner Thread 执行；原生 bootstrap control 不冒充 Managed Tick 入口。
- Native Worker 不回调 Hot Gameplay；Managed 调用期间不得持有可能阻塞的 Rust 锁；取消、超时与 World 销毁后的异步完成是终态且不能写状态（架构源 §8.1）。
- 跨边界只传固定宽度 POD、版本化 Buffer 与不透明 Index+Generation+Context Handle；内存由创建侧释放或调用方提供 Buffer。

## 正常数据流与失败路径

- **正常**：Manifest 校验通过 → CoreEngine/CoreCLR/Runtime/Gameplay 逐级装载 → `GameplayActive` → 每 Tick 经 `tick_entry` 进出 → 关闭时逆序卸载并通过 Root 验证。
- **失败路径**：
  - ABI/版本/Capability 不匹配：World 创建前以稳定错误失败（对应架构源反例 Fixture `fixtures/invalid/native-managed-abi-pointer-width.json`）。
  - Buffer 不足：返回所需大小，调用方重试（架构源 ADR-006）。
  - 无效/重复 Handle：稳定错误，不产生未定义行为。
  - Gameplay Exception（可捕获）：转稳定 Error Code，连同 Runtime 见证 `FaultClass` 转交 world-slot——`SessionLocalProven` 由聚合根隔离单 Session，`SlotStateUnproven`（含缺见证默认）由聚合根强制 Slot 恢复；本模块不做该判定。
  - ALC 卸载失败（Root 泄漏）：产出 LeakEvidence 与 Failure Bundle；旧模块保持活动或进程按维护策略重启，不留半卸载状态。
  - OOM/Stack Overflow/CoreCLR 崩溃/Native UB：进程级故障，写 crash marker，进程终止后从最近有效 Snapshot 恢复。

## 错误分类、恢复与降级

- **可重试**：Buffer 重试（带所需大小）；热重载失败后按维护编排择机重试。
- **可拒绝**：ABI/Hash/签名/Capability 不匹配、非法 Handle、World 销毁后的迟到完成。
- **可致命**：进程级故障四类（OOM、Stack Overflow、CoreCLR 崩溃、Native UB）。
- **降级**：热重载失败保留最后有效模块继续服务；不存在"跳过校验装载"的降级路径。

## 配置、Capability 与安全约束

- Server 默认 CoreCLR；Server HybridCLR 只是后续兼容性验证，不是 V1 依赖（架构源 §10、D-006 关联）。
- Gameplay 模块必须通过 Hash、签名与 Release 校验才能装载；装载来源只能是 ReleaseManifest 声明的 Artifact。
- `capability_bits` 与 [host-profiles](../host-profiles/README.md) 的能力声明一致性在启动期核对。

## 日志、Metrics、Trace 与 Audit

- ALC 装载/激活/热重载/卸载全部写 Audit（关联 `gameReleaseId` 与模块 Hash）。
- Metrics：FFI 调用批量大小、Managed Tick 入口耗时、异常转换计数（按 FaultClass 分类）、ALC 卸载时长与泄漏计数。
- 进程级故障路径产出 Failure Bundle 素材（寄存器/堆栈摘要交由平台机制，本模块保证 correlation 字段齐全）。

## 测试面、故障矩阵与性能指标

- **测试面**：CoreCLR Smoke（启动/装载/卸载）、ABI 兼容与指针宽度失败、panic/Exception 双向转换、`FaultClass` 见证原样转交（含缺见证默认 `SlotStateUnproven`）、stale-handle、重复装载拒绝、取消/超时/销毁后完成、ALC 卸载与 Root 验证（架构源 ADR-006 验证清单）。
- **故障矩阵**：`SessionLocalProven` 见证隔离单 Session 而 `SlotStateUnproven` 强制 Slot 恢复（裁决在 world-slot，本模块提供见证转交事实）、ALC 泄漏、热重载失败回退、CoreCLR 崩溃后的进程恢复演练。
- **性能指标**：FFI Batch 大小与单次跨边界开销、热重载停顿时长（Quiesce 到切换完成）、GC 暂停对 Tick 尾延迟的贡献。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-006-native-managed-abi.md`（ABI、Loader、`FaultClass` 见证与"捕获不等于分类"）、`docs/adr/ADR-014-platform-capability.md`（热重载失败语义、CoreCLR 默认）。
- 架构源 `ids/index.json`：`FaultClass` 命名空间与 `ErrorCode` 注册表。
- 架构源 `schemas/native-managed-abi.schema.json`：正例 `fixtures/valid/native-managed-abi.json`，反例 `fixtures/invalid/native-managed-abi-pointer-width.json`。
- 架构源 `schemas/release-manifest.schema.json`（`runtimeApi`、`coreEngineAbi`、Assembly Hash 字段）：正例 `fixtures/valid/release-manifest-a-1.1.json`。

## 尚未批准的决策门

- **D-001**（一进程一 Release）：本模块按临时默认值推进（每进程一个 `gameReleaseId`），该值 provisional、未冻结；启用进程内多 Release 装载需新 ADR 并更新架构源 ADR-006/012 与 Loader Schema。登记见 [modules/README.md](../README.md) §11.1。
- 受 **D-006** 间接影响：Server HybridCLR 兼容性验证若未来立项，装载策略变更须走新 Capability 与 Manifest 声明。
