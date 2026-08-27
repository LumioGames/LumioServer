# LumioServer `release-agent` 模块实现级设架

> 架构基线：`LGE-V1.2-2026-08-27`  
> 实现优先级：**P1**  
> crate：`lumio-release-agent`  
> 本文只定义仓内实现结构；公共 Schema / ABI / ID / 状态机仍由 `LumioGameEngineArchitecture` 维护。

## A. 一句话职责与非职责

**职责：** 拥有本进程单一 Release 身份、Catalog/Manifest 校验、ExactRelease 匹配与本地 member state/health evidence；不拥有跨进程路由或 Pool desired state。

**明确不负责：**
- 不选择/启动/停止其他实例，不维护集群 Pool 存在性或目标实例激活。
- 不把 D-001 provisional 写成永久公共常量，不实现 N/N-1 兼容。
- 不拥有 transport connection/session admission；只回答 release predicate 与发送本地状态报告。
- 不重写 ReleaseCatalog/ReleaseManifest 字段、九态或 hash 语义。

## B. crate、目录与文件清单

建议 package 名：`lumio-release-agent`。文件按所有权/职责切分，不创建 `common.rs`、`globals.rs`、`event_bus.rs` 或大锅文件。

| 文件 | 唯一职责 |
| --- | --- |
| `modules/release-agent/Cargo.toml` | generated release contracts、hash/serde validation。 |
| `modules/release-agent/src/lib.rs` | 导出本地 release agent API。 |
| `modules/release-agent/src/identity.rs` | gameReleaseId 和 artifact identities。 |
| `modules/release-agent/src/catalog.rs` | generated ReleaseCatalog 借用与结构校验。 |
| `modules/release-agent/src/manifest.rs` | generated ReleaseManifest 借用、hash/路径验证。 |
| `modules/release-agent/src/verifier.rs` | catalog↔manifest↔configured release 一致性。 |
| `modules/release-agent/src/matching.rs` | ExactRelease predicate。 |
| `modules/release-agent/src/member_state.rs` | 本地 member state，不复制全局 Pool state machine。 |
| `modules/release-agent/src/health.rs` | timer-driven local health evidence。 |
| `modules/release-agent/src/reports.rs` | 发送给 control plane 的 local report。 |
| `modules/release-agent/src/commands.rs` | Verify/Match/EnterServing/EnterDraining/HealthTimer/Stop。 |
| `modules/release-agent/src/events.rs` | Verified/Matched/Rejected/StateChanged/HealthChanged。 |
| `modules/release-agent/src/service.rs` | serial reducer。 |
| `modules/release-agent/src/error.rs` | catalog/manifest/hash/state/report errors。 |
| `modules/release-agent/tests/release_fixture_test.rs` | valid/invalid catalog/manifest fixtures。 |
| `modules/release-agent/tests/exact_match_test.rs` | N/N-1 未启用。 |
| `modules/release-agent/tests/local_state_test.rs` | 无 TargetActivated/全局 owner。 |

## C. 公开/内部类型、Trait、命令、事件与端口

### C.1 类型、命令与事件
- `LocalReleaseIdentity`、`VerifiedReleaseBundle`、`ReleaseMatchRequest/Result`。
- `LocalReleaseMemberState`（明确标记仓内 local，不冒充公共 Pool state）。
- `ReleaseCommand::{VerifyConfiguredRelease, MatchSession, EnterServing, EnterDraining, HealthTimer, Stop}`。
- `ReleaseEvent::{Verified, MatchAccepted, MatchRejected, LocalStateChanged, HealthChanged}`。

### C.2 稳定仓内端口
| 接口 | 落点 | 契约 |
| --- | --- | --- |
| `ReleaseCommandPort::try_send(ReleaseCommand)` | `commands.rs` | session/maintenance/process 唯一控制入口。 |
| `ReleaseEventPort::try_recv()` | `events.rs` | 显式 ack/event。 |
| `ReleaseQueryPort::snapshot()` | `service.rs` | 不可变 verified identity/member view。 |
| `ReleaseReporter::try_report(LocalReleaseReport)` | `reports.rs` | adapter port；控制面类型由 control-plane-adapter 拥有/生成。 |

### C.3 Rust 签名草案

以下签名是**仓内实现接口**，不是新增公共 ABI；实现时只能引用上游 generated 类型，不能复制公共字段。

```rust
impl ReleaseVerifier {
    pub fn verify(
        configured: &ConfiguredReleaseIdentity,
        catalog: &GeneratedReleaseCatalog,
        manifest: &GeneratedReleaseManifest,
        artifacts: &ArtifactEvidenceSet,
    ) -> Result<VerifiedReleaseBundle, ReleaseVerificationError>;
}

impl ReleaseMatchPolicy {
    pub fn exact_match(
        verified: &VerifiedReleaseBundle,
        requested_game_release_id: &GameReleaseId,
    ) -> ReleaseMatchResult;
}

impl ReleaseCommandPort {
    pub fn try_send(&self, command: ReleaseCommand) -> Result<(), ReleasePortError>;
}
```

## D. 状态、资源与生命周期所有权

- `LocalReleaseIdentity`、已验证 Catalog/Manifest snapshot、校验 provenance。
- 本进程 member lifecycle/readiness/health sample 与 report sequence。
- ExactRelease predicate、manifest/core/runtime/game artifact binding evidence。
- 健康检查 timer token；不拥有 timer thread。

### D.1 模块红线
- 禁止出现跨进程最终路由决策或目标实例启动。
- 公共 ReleaseCatalog 九态不得重命名、合并或以 local state 替代。

## E. 线程、队列、背压与关闭

### E.1 线程/执行上下文
- 纯 control runner，经 host-runtime 有界 executor。
- Catalog/Manifest 文件 I/O 通过 process bootstrap 或受监督 adapter 完成，随后冻结 snapshot。
- 健康检查由 TimerFired 触发，不自建低频线程。
- 报告通过 control-plane/transport typed port，失败只影响本地 report 状态。

### E.2 队列合同
| 队列 | 元素 | 所有者 | 生产者 | 消费者 | 顺序 | 容量配置 | 满载动作 | 关闭语义 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `ReleaseCommandInbox` | `ReleaseCommand` | release-agent | process/session/maintenance/timer | release runner | FIFO | `release.command.capacity` | 返回 busy；drain/fault 保留槽 | stop 后拒绝新 match |
| `ReleaseEventOutbox` | `ReleaseEvent` | session/maintenance/process | release runner | consumers | FIFO by report sequence | `release.event.capacity` | 关键 state change 不丢；报告可 coalesce 最新 | 终态 drain |

这些行必须写入仓库 Queue Contract Registry；数值来自配置快照，SRV-D 默认值不得提升为公共常量。

## F. 失败分类、恢复与显式 Ack

| 分类 | 实现要求 |
| --- | --- |
| 可拒绝 | session gameReleaseId 不等、manifest mismatch、catalog duplicate/missing route；不进入 admission。 |
| 可重试 | 报告通道暂不可用或健康 sample 暂缺；本地 serving predicate 不被静默篡改。 |
| 进程启动失败 | configured release/manifest/core/runtime hash 不一致；listener 不开放。 |
| 本地故障 | 运行中 artifact evidence 失真；进入 local Faulted/report，是否退出由 process/world policy。 |

## G. 编译依赖、成熟方案与 Adapter 隔离

### G.1 编译依赖
**允许：**
- transport
- control-plane-adapter
- host-runtime
- observability
- host-profiles
- generated architecture release contracts
- `sha2`/`hex` if algorithm required by schema

**禁止：**
- session/world-slot/process 反向实现
- cluster client SDK 类型进入 API
- LumioGame source
- N/N-1 私自启用

### G.2 成熟方案选择
| 方案 | 用途 | 成熟性、许可证与隔离 |
| --- | --- | --- |
| `sha2` | Schema 指定的 SHA-256 校验（仅若上游明确） | RustCrypto 成熟、MIT/Apache-2.0；算法由生成契约选择，不由模块发明。 |
| `serde` + generated validators | Catalog/Manifest | 只消费上游字段/fixtures。 |
| host-runtime Timer | health cadence | 不自建健康线程。 |

### G.3 明确拒绝的自研项
- 不自研 release router、service discovery、orchestrator、hash 算法或兼容性协议。
- 只写本地一致性 predicate/reducer，因为这是单进程 release evidence 所有权。

## H. 测试面与 Fixture

- Golden：ReleaseCatalog/Manifest 正反 fixtures、camelCase 字段。
- Property：验证成功的 bundle 对同一输入结果稳定；任何 artifact mismatch 都拒绝。
- 行为：Serving→Draining→ReadyToExit local report，不存在 TargetActivated。
- 定时：旧 health timer generation 不改变新 state。
- 集成：session admission 只消费 exact-match result。

## I. 决策门与配置默认

- D-001 当前一进程一 gameReleaseId；类型中保留 identity value，不导出单例常量。
- D-007 `DeclaredNMinusOne` 仅 Schema 预留，V1 predicate 固定 ExactRelease。
- 控制面上报 channel 受 D-010；行为与报告值可先实现。

## J. 本模块任务卡

| 任务 | Wave | 单一目标 | 依赖 |
| --- | --- | --- | --- |
| [`implement-release-catalog-manifest-verification`](../../../.spec/tasks/implement-release-catalog-manifest-verification.md) | Wave 3 | 验证configured gameReleaseId、Catalog、Manifest和artifact hashes，提供session exact-match结果。 | `consume-upstream-generated-contract-artifacts`, `implement-host-runtime-bounded-ports` |
| [`implement-release-local-member-state-health-and-reporting`](../../../.spec/tasks/implement-release-local-member-state-health-and-reporting.md) | Wave 6 | 建立本进程local state reducer、timer-driven health和control-plane report，不拥有全局Pool。 | `implement-release-catalog-manifest-verification`, `implement-host-runtime-clock-and-timer-delivery`, `implement-control-plane-behavior-core` |

## K. 完成定义

本模块的任务卡全部通过；模块单测/fixture/故障测试通过；Queue Registry、Cargo DAG、许可证与源码红线检查通过；没有供应商类型泄漏、无界队列、任意回调或未见证 FaultClass 推断。
