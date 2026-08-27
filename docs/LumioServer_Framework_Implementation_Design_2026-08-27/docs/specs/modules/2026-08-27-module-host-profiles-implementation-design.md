# LumioServer `host-profiles` 模块实现级设架

> 架构基线：`LGE-V1.2-2026-08-27`  
> 实现优先级：**P1**  
> crate：`lumio-host-profiles`  
> 本文只定义仓内实现结构；公共 Schema / ABI / ID / 状态机仍由 `LumioGameEngineArchitecture` 维护。

## A. 一句话职责与非职责

**职责：** 把公共 HostCapability/Preset 与仓内配置解析成不可变 `HostCompositionPlan`、预算和故障声明；只描述组件能力，不构造组件、不反向依赖模块。

**明确不负责：**
- 不持有模块实例、端口、sink、thread、queue 或 mutable runtime state。
- 不把 profile 写成散落的 `#[cfg]` 业务分叉，不绕过 LocalEmbedded fidelity。
- 不发明 HostCapability 字段或把动态资源余量混入静态 capability。
- 不依赖 observability 或任何一等模块 crate。

## B. crate、目录与文件清单

建议 package 名：`lumio-host-profiles`。文件按所有权/职责切分，不创建 `common.rs`、`globals.rs`、`event_bus.rs` 或大锅文件。

| 文件 | 唯一职责 |
| --- | --- |
| `modules/host-profiles/Cargo.toml` | generated HostCapability + serde/validation；无模块依赖。 |
| `modules/host-profiles/src/lib.rs` | 导出 profile inputs/plans。 |
| `modules/host-profiles/src/preset.rs` | Preset enum/reference，严格对齐上游。 |
| `modules/host-profiles/src/capability.rs` | 静态 required/provided capability match。 |
| `modules/host-profiles/src/budget.rs` | configured queue/thread/memory limits；不含动态用量。 |
| `modules/host-profiles/src/composition.rs` | `HostCompositionPlan` 仅由 adapter class/requirement enums组成。 |
| `modules/host-profiles/src/fault_profile.rs` | 可声明的 fault decorator plan。 |
| `modules/host-profiles/src/validation.rs` | LocalEmbedded fidelity、D-010/D-011/D-004 capability gates。 |
| `modules/host-profiles/src/error.rs` | capability/profile/budget/gate errors。 |
| `modules/host-profiles/tests/capability_fixture_test.rs` | HostCapability fixtures。 |
| `modules/host-profiles/tests/composition_matrix_test.rs` | 三 profile + headless。 |
| `modules/host-profiles/tests/no_module_dependency_test.rs` | Cargo metadata guard。 |

## C. 公开/内部类型、Trait、命令、事件与端口

### C.1 类型、命令与事件
- `HostProfileInput`、`HostPreset`、`HostCompositionPlan`。
- `TransportAdapterClass::{Remote, LocalEmbeddedBytes, LocalSplitProcess}`。
- `ControlChannelRequirement::{RequiredProduction, InjectedTestOnly, DisabledByProfile}`。
- `PersistenceMode`、`ObservabilityPlan`、`ConfiguredBudgets`、`FaultDecoratorPlan`。
- `StaticCapabilitySet` 与 `ConfiguredLimits` 分离；无 RuntimeStatus。

### C.2 稳定仓内端口
| 接口 | 落点 | 契约 |
| --- | --- | --- |
| `resolve_profile(input, capability, config) -> HostCompositionPlan` | `composition.rs` | 纯函数；不创建组件。 |
| `validate_plan(plan) -> Result<ValidatedHostCompositionPlan>` | `validation.rs` | 检查 fidelity、gate和预算完整性。 |
| `match_capabilities(required, provided)` | `capability.rs` | 只处理公共静态 capability。 |

### C.3 Rust 签名草案

以下签名是**仓内实现接口**，不是新增公共 ABI；实现时只能引用上游 generated 类型，不能复制公共字段。

```rust
pub fn resolve_profile(
    input: HostProfileInput,
    capability: &GeneratedHostCapability,
    config: &ProfileConfig,
) -> Result<HostCompositionPlan, HostProfileError>;

pub fn validate_plan(
    plan: HostCompositionPlan,
) -> Result<ValidatedHostCompositionPlan, HostProfileError>;

pub fn match_capabilities(
    required: &StaticCapabilitySet,
    provided: &StaticCapabilitySet,
) -> Result<CapabilityMatch, CapabilityMismatch>;
```

## D. 状态、资源与生命周期所有权

- profile/preset input、静态 capability match、configured budgets 和 immutable composition descriptor。
- `RemoteDS`/`LocalEmbedded`/`LocalSplitProcess`/headless 场景的 adapter class选择描述。
- fault decorator declaration、test-only capability flag与验证错误。
- 不拥有动态 health/queue depth。

### D.1 模块红线
- 生产 binary同时包含所需 adapters；业务代码不散布 profile `#[cfg]`。
- host-profiles不得知道具体模块构造函数。

## E. 线程、队列、背压与关闭

### E.1 线程/执行上下文
- 纯函数/不可变数据，无线程、无队列、无 timer。
- 只在 process bootstrap 解析一次并冻结；运行中变更必须经新进程配置，不热改。
- 测试 profile由同一 validator验证。
- 具体 factory mapping留在 process `wiring.rs`，避免反向依赖。

### E.2 队列合同
本模块无运行时队列；如实现中出现队列，必须先更新 `modules/README.md` Queue Contract Matrix 与 policy manifest。

## F. 失败分类、恢复与显式 Ack

| 分类 | 实现要求 |
| --- | --- |
| 可拒绝 | capability缺失、预算无效、LocalEmbedded尝试绕过层、RemoteDS缺生产控制通道。 |
| 不可重试 | 同一冻结输入的 plan validation失败；修改配置/基线后重启。 |
| 无运行时 fault | 本模块无线程/状态，不产生 Session/Slot/Process FaultClass。 |
| 证据 | 错误列精确缺失 capability/gate，不包含secret。 |

## G. 编译依赖、成熟方案与 Adapter 隔离

### G.1 编译依赖
**允许：**
- generated HostCapability contracts
- `serde`
- `thiserror`

**禁止：**
- process/host-runtime/observability/任何业务模块
- Tokio/vendor adapter crate
- dynamic resource registry

### G.2 成熟方案选择
| 方案 | 用途 | 成熟性、许可证与隔离 |
| --- | --- | --- |
| `serde` + generated schema validator | profile/capability输入 | 成熟、宽松；字段权威仍来自架构源。 |
| 无 DI/container 框架 | composition descriptor | 具体组件集合固定，process显式 mapping更可审计。 |

### G.3 明确拒绝的自研项
- 不自研 DI container、feature framework、动态插件装载器。
- 仅实现纯 profile resolver，因为它承载架构 fidelity/decision gate验证，通用 DI无法表达这些不变量。

## H. 测试面与 Fixture

- Golden：HostCapability valid/invalid fixtures。
- Matrix：RemoteDS/LocalEmbedded/LocalSplitProcess/headless 的 component requirements。
- 负例：LocalEmbedded跳过codec/auth/queue、RemoteDS无D-010 channel、生产计划使用test adapter。
- Property：相同输入 plan byte-for-byte稳定；plan无动态健康字段。
- Policy：cargo metadata确认零一等模块依赖。

## I. 决策门与配置默认

- D-004/D-010/D-011 是否已满足作为 composition capability，不由 profile模块自行决定。
- D-001 defaults只在 plan value，不通过 Cargo feature永久裁剪。
- headless是角色/adapter组合，不代表跳过协议链。

## J. 本模块任务卡

| 任务 | Wave | 单一目标 | 依赖 |
| --- | --- | --- | --- |
| [`implement-host-profile-resolution-and-capability-matching`](../../../.spec/tasks/implement-host-profile-resolution-and-capability-matching.md) | Wave 2 | 将 generated HostCapability、配置和 preset 纯函数化为 immutable plan，零一等模块依赖。 | `consume-upstream-generated-contract-artifacts` |
| [`implement-host-profile-fault-decorator-declarations`](../../../.spec/tasks/implement-host-profile-fault-decorator-declarations.md) | Wave 3 | 增加仅描述、不执行的 deterministic fault plan，并阻止测试 adapter进入生产 composition。 | `implement-host-profile-resolution-and-capability-matching` |

## K. 完成定义

本模块的任务卡全部通过；模块单测/fixture/故障测试通过；Queue Registry、Cargo DAG、许可证与源码红线检查通过；没有供应商类型泄漏、无界队列、任意回调或未见证 FaultClass 推断。
