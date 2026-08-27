# LumioServer `coreclr-host` 模块实现级设架

> 架构基线：`LGE-V1.2-2026-08-27`  
> 实现优先级：**P0**  
> crate：`lumio-coreclr-host`  
> 本文只定义仓内实现结构；公共 Schema / ABI / ID / 状态机仍由 `LumioGameEngineArchitecture` 维护。

## A. 一句话职责与非职责

**职责：** 通过官方 hostfxr/nethost 与上游生成 ABI 启动单个 CoreCLR；在绑定 Simulation Owner Thread 后由该线程执行全部 Managed Runtime/Gameplay/Tick 入口，并向 world-slot 原样转交 Runtime fault witness。

**明确不负责：**
- 不拥有 Simulation Owner Thread、Tick scheduling、WorldSlotHost state 或 ECS/Game state。
- 不从异常是否可捕获推断 Session/Slot/Process FaultClass。
- 不手写/扩展 HostApiV1/ManagedApiV1、ABI descriptor、C ABI 字段或 Gameplay 方法地址协议。
- V1 不装载第二个 CoreCLR；不把 HybridCLR 当 Server 前置。

## B. crate、目录与文件清单

建议 package 名：`lumio-coreclr-host`。文件按所有权/职责切分，不创建 `common.rs`、`globals.rs`、`event_bus.rs` 或大锅文件。

| 文件 | 唯一职责 |
| --- | --- |
| `modules/coreclr-host/Cargo.toml` | `netcorehost` 与 generated ABI crates，隔离 unsafe。 |
| `modules/coreclr-host/src/lib.rs` | 导出 supplier-neutral host/ports。 |
| `modules/coreclr-host/src/state.rs` | Uninitialized/RuntimeLoaded/StableRuntimeReady/GameplayReady/Stopping/Stopped/Faulted。 |
| `modules/coreclr-host/src/contracts.rs` | re-export/validate generated ABI descriptors，不复制字段。 |
| `modules/coreclr-host/src/host.rs` | CoreClrHost facade。 |
| `modules/coreclr-host/src/bootstrap.rs` | nethost/hostfxr discovery 与 runtime config load。 |
| `modules/coreclr-host/src/runtime_scope.rs` | 稳定 Runtime 装载和 ABI table 获取。 |
| `modules/coreclr-host/src/gameplay_scope.rs` | Gameplay scope load/unload 命令桥。 |
| `modules/coreclr-host/src/thread_affinity.rs` | control vs owner-thread token。 |
| `modules/coreclr-host/src/invocation.rs` | tick/control invocation wrapper。 |
| `modules/coreclr-host/src/fault.rs` | 异常归一化与 witness passthrough。 |
| `modules/coreclr-host/src/commands.rs` | Load/BindOwner/LoadGameplay/UnloadGameplay/Stop。 |
| `modules/coreclr-host/src/events.rs` | Ready/ScopeLoaded/InvocationFailed/Witness/Stopped。 |
| `modules/coreclr-host/src/adapters/netcorehost.rs` | 唯一 `netcorehost`/hostfxr 供应商边界。 |
| `modules/coreclr-host/src/ffi/mod.rs` | 最小 unsafe 模块与 pointer validation。 |
| `modules/coreclr-host/src/error.rs` | hostfxr/ABI/thread/scope errors。 |
| `modules/coreclr-host/tests/abi_conformance_test.rs` | generated descriptor/layout/version fixture。 |
| `modules/coreclr-host/tests/thread_affinity_test.rs` | 控制调用和 Tick 调用分离。 |
| `modules/coreclr-host/tests/fault_passthrough_test.rs` | 不裁决 FaultClass。 |
| `modules/coreclr-host/tests/unload_soak_test.rs` | scope 反复装卸资源证据。 |

## C. 公开/内部类型、Trait、命令、事件与端口

### C.1 类型、命令与事件
- `CoreClrHost`、`CoreClrHostState`、`RuntimeScopeHandle`、`GameplayScopeHandle`。
- `OwnerThreadToken`、`ManagedTickRequest`、`ManagedTickOutcome`。
- `CoreClrCommand::{StartCoreClr, BindOwnerThread, InitializeManagedRuntime, LoadGameplayScope, UnloadGameplayScope, Stop}`。
- `CoreClrEvent::{RuntimeReady, GameplayReady, InvocationFailed, RuntimeWitness, ScopeUnloaded, Stopped}`。

### C.2 稳定仓内端口
| 接口 | 落点 | 契约 |
| --- | --- | --- |
| `CoreClrControlPort::try_send(CoreClrCommand)` | `commands.rs` | 原生 bootstrap/control 命令；所有跨 Managed delegate 的命令由 world-slot owner thread执行。 |
| `ManagedTickPort::invoke(token, request)` | `invocation.rs` | 同步、仅 owner thread；返回 outcome，不进行 fault adjudication。 |
| `CoreClrEventPort::try_recv()` | `events.rs` | world-slot/process 消费。 |
| `AbiContractValidator::validate(descriptor)` | `contracts.rs` | 严格比对 generated version/layout/calling convention。 |

### C.3 Rust 签名草案

以下签名是**仓内实现接口**，不是新增公共 ABI；实现时只能引用上游 generated 类型，不能复制公共字段。

```rust
impl CoreClrControlPort {
    pub fn try_send(&self, command: NativeCoreClrControlCommand) -> Result<(), CoreClrPortError>;
}

// 由 world-slot 的 Simulation Owner Thread 持有；所有 Managed delegate 调用均经此端口。
impl ManagedRuntimePort {
    pub fn initialize(
        &mut self,
        owner: &OwnerThreadToken,
        request: ManagedRuntimeInitializeRequest,
    ) -> Result<ManagedRuntimeReady, ManagedInvocationError>;

    pub fn load_gameplay(
        &mut self,
        owner: &OwnerThreadToken,
        request: GameplayLoadRequest,
    ) -> Result<GameplayScopeHandle, ManagedInvocationError>;

    pub fn invoke_tick(
        &mut self,
        owner: &OwnerThreadToken,
        request: ManagedTickRequest,
    ) -> Result<ManagedTickOutcome, ManagedInvocationError>;
}

impl AbiContractValidator {
    pub fn validate(
        descriptor: &GeneratedNativeManagedAbiDescriptor,
    ) -> Result<ValidatedAbiContract, AbiContractError>;
}
```

## D. 状态、资源与生命周期所有权

- `CoreClrHostState`、hostfxr handle、Runtime assembly/scope handles、ABI negotiation result。
- bootstrap/control 调用与 Tick 热路径的 thread-affinity token。
- Managed invocation outcome、异常摘要、可选 generated `RuntimeFaultWitness` 的完整转交。
- Gameplay scope unload evidence；不拥有 Runtime 内部 module registry。

### D.1 模块红线
- coreclr-host 只转交 `FaultClass` witness，不裁决。
- 线程口径固定为：原生 hostfxr bootstrap 可在 control context；所有 Managed delegate 调用均在 Owner Thread。

## E. 线程、队列、背压与关闭

### E.1 线程/执行上下文
- hostfxr/nethost 发现、CoreCLR 原生启动和 ABI 描述校验可运行在 host-runtime 受监督 control context；这些步骤不得调用 Managed 业务入口。
- Runtime 初始化、Gameplay load/unload 与 Tick 等全部 Managed delegate 调用只能由绑定的 Simulation Owner Thread 执行。
- 异步 managed callback 禁止直接进入 Host 状态；只能通过 generated completion/command port。
- 不自建线程；hostfxr/managed 内部线程是供应商 runtime，需在 evidence 中可观测。

### E.2 队列合同
| 队列 | 元素 | 所有者 | 生产者 | 消费者 | 顺序 | 容量配置 | 满载动作 | 关闭语义 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `CoreClrControlInbox` | `CoreClrCommand` | coreclr-host | process/world-slot | control runner | FIFO | `coreclr.control.capacity` | 返回 busy；Unload/Stop 保留槽 | shutdown 后完成当前 ABI 调用并拒绝新 scope |
| `CoreClrEventOutbox` | `CoreClrEvent` | coreclr-host | coreclr runner / owner thread | world-slot / process | per invocation FIFO | `coreclr.event.capacity` | 关键 fault witness 不丢；无法交付升级 supervisor | 终态 drain |

这些行必须写入仓库 Queue Contract Registry；数值来自配置快照，SRV-D 默认值不得提升为公共常量。

## F. 失败分类、恢复与显式 Ack

| 分类 | 实现要求 |
| --- | --- |
| 可拒绝 | ABI version/layout/calling convention mismatch、错误线程、非法 scope state；不调用 Managed。 |
| 可重试 | 可选 Gameplay scope 暂不可加载；仅在 release policy 允许时重试。 |
| Slot 级候选 | Managed invocation 失败但无 Runtime witness：只发 raw evidence；world-slot 归为 SlotStateUnproven。 |
| 进程级 | CoreCLR bootstrap/稳定 Runtime ABI 失败或 hostfxr 崩溃；process 启动失败/终止。 |

## G. 编译依赖、成熟方案与 Adapter 隔离

### G.1 编译依赖
**允许：**
- host-runtime
- observability
- host-profiles
- generated Managed Host ABI
- generated Core Engine contract
- `netcorehost 0.22`

**禁止：**
- world-slot/session/process 反向类型（用 coreclr-owned epoch/thread token）
- LumioGame 源码
- legacy COM hosting API
- 私有 ABI schema

### G.2 成熟方案选择
| 方案 | 用途 | 成熟性、许可证与隔离 |
| --- | --- | --- |
| Microsoft official `nethost`/`hostfxr` API | CoreCLR hosting | 唯一受支持原生 hosting 路径；由 .NET runtime 发布。 |
| `netcorehost 0.22` | Rust hostfxr bindings | 活跃、MIT；只在 adapter/unsafe 边界，稳定 API 不暴露 crate 类型。 |
| `libloading`（若 netcorehost 内部需要） | 动态符号 | 成熟、ISC；不直接散布到模块。 |

### G.3 明确拒绝的自研项
- 不自研 CLR loader、GC bridge、delegate thunk、ABI table 或异常分类器。
- 只写最小 host adapter/affinity guard，因为上游 API 不知道 Lumio Slot epoch 与 Tick owner 约束。

## H. 测试面与 Fixture

- ABI golden：上游 native-managed-abi fixture、指针宽度/版本/表长反例。
- 线程：bootstrap 可在 control context，Tick 只在绑定 owner；错误线程 fail-fast。
- 故障：Managed throw、panic-like fatal、缺 witness、有 witness原样传递。
- 生命周期：部分初始化失败逆序释放、重复 stop 幂等、ALC unload soak。
- AOT：Server 目标不是 NativeAOT；记录 trimming/AOT 非目标，不依赖动态生成 Host ABI。

## I. 决策门与配置默认

- D-006：Server HybridCLR 未冻结，V1 不作为依赖或 fallback。
- D-001：单 CoreCLR 是 provisional V1 default，但 Host state 明确，不私造 multi-CLR。
- 上游 ABI hash/version 不匹配是硬失败，不能用 best effort。

## J. 本模块任务卡

| 任务 | Wave | 单一目标 | 依赖 |
| --- | --- | --- | --- |
| [`implement-coreclr-generated-abi-contract-facade`](../../../.spec/tasks/implement-coreclr-generated-abi-contract-facade.md) | Wave 3 | 只读消费Managed/Core generated contracts，定义host state、control/owner-thread token和无fault裁决的结果类型。 | `consume-upstream-generated-contract-artifacts`, `implement-host-runtime-bounded-ports` |
| [`implement-coreclr-lifecycle-and-fault-passthrough`](../../../.spec/tasks/implement-coreclr-lifecycle-and-fault-passthrough.md) | Wave 4 | 建立纯生命周期reducer、ManagedTickPort、scope load/unload效果和异常/witness passthrough。 | `implement-coreclr-generated-abi-contract-facade`, `implement-host-runtime-clock-and-timer-delivery` |
| [`implement-coreclr-netcorehost-adapter`](../../../.spec/tasks/implement-coreclr-netcorehost-adapter.md) | Wave 5 | 通过netcorehost封装CoreCLR discovery/load/function table获取，集中unsafe和供应商错误映射。 | `implement-coreclr-lifecycle-and-fault-passthrough`, `implement-host-runtime-supervision-cancellation-and-join` |

## K. 完成定义

本模块的任务卡全部通过；模块单测/fixture/故障测试通过；Queue Registry、Cargo DAG、许可证与源码红线检查通过；没有供应商类型泄漏、无界队列、任意回调或未见证 FaultClass 推断。
