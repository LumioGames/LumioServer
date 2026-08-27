# LumioServer `auth` 模块实现级设架

> 架构基线：`LGE-V1.2-2026-08-27`  
> 实现优先级：**P0**  
> crate：`lumio-auth`  
> 本文只定义仓内实现结构；公共 Schema / ABI / ID / 状态机仍由 `LumioGameEngineArchitecture` 维护。

## A. 一句话职责与非职责

**职责：** 对 transport 交来的不透明凭据输入执行认证行为、重放防护和权限裁决，产出不可变 principal/grant 与认证审计事实；不定义凭据 wire。

**明确不负责：**
- 不拥有连接、ConnectionRegistry、Session、Admission Gate、Release pinning 或 Socket。
- D-011 冻结前不定义 ticket 字段、签名算法、nonce wire、密钥派生或公开序列化格式。
- 不把凭据、token、key 或可逆摘要写入日志/任务示例。
- 不直接限流/断开连接；只发 typed 风险事件，由 transport/session 执行。

## B. crate、目录与文件清单

建议 package 名：`lumio-auth`。文件按所有权/职责切分，不创建 `common.rs`、`globals.rs`、`event_bus.rs` 或大锅文件。

| 文件 | 唯一职责 |
| --- | --- |
| `modules/auth/Cargo.toml` | secrecy/zeroize/lru/thiserror；无具体 wire crypto 默认。 |
| `modules/auth/src/lib.rs` | 导出行为契约与仓内值类型。 |
| `modules/auth/src/credential.rs` | `OpaqueCredentialInput`，禁止 Debug/Serialize。 |
| `modules/auth/src/verifier.rs` | `CredentialVerifier` SPI 与结果归一化。 |
| `modules/auth/src/replay.rs` | bounded replay fingerprint cache 与 expiry。 |
| `modules/auth/src/identity.rs` | `PrincipalId`/claims 的仓内稳定视图。 |
| `modules/auth/src/permission.rs` | `PermissionGrant`、grant epoch、expiry/revocation。 |
| `modules/auth/src/service.rs` | 串行认证 runner 和命令处理。 |
| `modules/auth/src/commands.rs` | Authenticate/Revoke/Expire typed commands。 |
| `modules/auth/src/events.rs` | Succeeded/Rejected/RiskDetected/Revoked events。 |
| `modules/auth/src/audit.rs` | 生成公共 LoggingEvent/Audit 输入，不写 sink。 |
| `modules/auth/src/error.rs` | credential invalid/busy/replay/verifier unavailable。 |
| `modules/auth/src/adapters/injected.rs` | 仅测试/集成用 exact-byte verifier，不成为 wire 标准。 |
| `modules/auth/tests/behavior_contract_test.rs` | 成功、拒绝、超时、撤销。 |
| `modules/auth/tests/replay_property_test.rs` | bounded/expiry/duplicate invariants。 |
| `modules/auth/tests/secret_redaction_test.rs` | Debug/log/evidence 永不包含凭据。 |

## C. 公开/内部类型、Trait、命令、事件与端口

### C.1 类型、命令与事件
- `OpaqueCredentialInput`、`AuthRequestId`、`PrincipalId`、`PermissionGrant`、`GrantEpoch`。
- `AuthenticateCommand { requestId, connectionId, connectionEpoch, opaqueCredential, context }`。
- `AuthEvent::{Authenticated, Rejected, ReplayDetected, Revoked, VerifierUnavailable}`。
- `CredentialVerifier` 是 adapter SPI；实现不得泄漏到 session API。

### C.2 稳定仓内端口
| 接口 | 落点 | 契约 |
| --- | --- | --- |
| `AuthCommandPort::try_send(AuthenticateCommand)` | `commands.rs` | session 唯一入口；显式 request id/ack。 |
| `AuthEventPort::try_recv()` | `events.rs` | session 消费结果；包含不可变 grant，不含 secret。 |
| `CredentialVerifier::verify(&Secret<OpaqueCredentialInput>, VerificationContext)` | `verifier.rs` | D-011 只冻结后替换生产 adapter；返回 supplier-neutral result。 |
| `AuthService::apply_timer(TimerFired)` | `service.rs` | 只处理已知 auth timer token，迟到 generation 拒绝。 |

### C.3 Rust 签名草案

以下签名是**仓内实现接口**，不是新增公共 ABI；实现时只能引用上游 generated 类型，不能复制公共字段。

```rust
pub trait CredentialVerifier: Send {
    fn verify(
        &mut self,
        credential: &Secret<OpaqueCredentialInput>,
        context: VerificationContext,
    ) -> Result<VerifierResult, VerifierError>;
}

impl AuthCommandPort {
    pub fn try_send(&self, command: AuthenticateCommand) -> Result<(), AuthPortError>;
}

impl AuthService {
    pub fn reduce(&mut self, input: AuthInput) -> Result<AuthEffects, AuthError>;
    pub fn apply_timer(&mut self, fired: TimerFired) -> Result<AuthEffects, AuthError>;
}

pub struct PermissionGrant {
    pub principal_id: PrincipalId,
    pub permission_ids: Box<[PermissionId]>,
    pub grant_epoch: GrantEpoch,
    pub expires_at: MonotonicInstant,
}
```

## D. 状态、资源与生命周期所有权

- 认证请求状态、bounded replay cache、principal identity、permission grant 生命周期与 revocation epoch。
- `CredentialVerifier` adapter 的结果归一化；秘密值封装与及时 zeroize。
- 认证审计事件事实和 correlation；durable ack 仍归 observability。
- 按 connection epoch 绑定的 grant 视图，不拥有 transport registry。

### D.1 模块红线
- 任何任务卡和测试样例不得包含真实或貌似真实的 key/token。
- PermissionGrant 不能由 transport 解释；session 只把必要引用复制到 transport。

## E. 线程、队列、背压与关闭

### E.1 线程/执行上下文
- 认证服务运行在 host-runtime 有界 control executor；慢 verifier 必须有预算/超时。
- 不创建线程；过期与 revocation 通过 TimerFired/typed command。
- 任何完成都回到 session control inbox；不得在 verifier completion 内改连接。
- replay cache 由 auth serial runner 单写，避免跨模块共享锁。

### E.2 队列合同
| 队列 | 元素 | 所有者 | 生产者 | 消费者 | 顺序 | 容量配置 | 满载动作 | 关闭语义 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `AuthRequestQueue` | `AuthenticateCommand` | auth | session | auth runner | FIFO per connection epoch | `auth.request.capacity` | 返回 `AuthBusy`，session 决定重试/关闭 | shutdown 后拒绝并完成 pending ack |
| `AuthEventQueue` | `AuthEvent` | session | auth runner | session | FIFO per request id | `auth.event.capacity` | 高严重风险写 diagnostic emergency；不得丢成功 ack | close 前 drain 已完成事件 |

这些行必须写入仓库 Queue Contract Registry；数值来自配置快照，SRV-D 默认值不得提升为公共常量。

## F. 失败分类、恢复与显式 Ack

| 分类 | 实现要求 |
| --- | --- |
| 可拒绝 | 凭据无效/过期/权限不足/重放；AuthRejected，session 保持未绑定。 |
| 可重试 | verifier 暂不可用或 executor 满；返回 `VerifierUnavailable/AuthBusy`，有界重试。 |
| 连接级 | 重放风暴或撤销命中；发 RiskDetected，由 session/transport 关闭。 |
| 进程级 | 秘密泄漏断言、replay cache 不可保持上限或审计 durable path 失效；按 policy 升级。 |

## G. 编译依赖、成熟方案与 Adapter 隔离

### G.1 编译依赖
**允许：**
- host-runtime
- observability
- host-profiles
- generated architecture IDs/LoggingEvent
- `secrecy`、`zeroize`、`lru`

**禁止：**
- transport/session/world-slot/release-agent
- 具体 JWT/PASETO/自定义票据 crate 作为默认 wire
- secret 的 Serialize/Debug

### G.2 成熟方案选择
| 方案 | 用途 | 成熟性、许可证与隔离 |
| --- | --- | --- |
| `secrecy 0.10` | 秘密值暴露控制 | 成熟、MIT/Apache-2.0；只可通过 `ExposeSecret` 在 verifier adapter 短暂访问。 |
| `zeroize 1.9` | 内存清理 | RustCrypto 生态、宽松许可证；credential buffer drop 清理。 |
| `lru 0.18` | bounded replay index | 成熟、MIT；auth runner 单写并叠加 monotonic expiry。 |
| `subtle`（算法需要时） | 常数时间比较 | RustCrypto 成熟；仅具体 verifier adapter，D-011 前不选签名算法。 |

### G.3 明确拒绝的自研项
- 不自研密码算法、token 格式、密钥派生、通用限流器。
- replay cache 只组合成熟 LRU + 单调 expiry；自有部分是必要的 connection/grant epoch 行为契约。

## H. 测试面与 Fixture

- 行为：无效/过期/revoked/replay/duplicate request。
- 属性：cache 项数始终 ≤ 配置；同 fingerprint 在窗口内至多成功一次。
- 竞态：认证完成 vs connection close/reconnect epoch；旧结果必须拒绝。
- 安全：所有 Debug、LoggingEvent、Failure Bundle fragment 扫描无 secret bytes。
- D-011 conformance：待架构 fixture 出现后只新增 adapter fixture，不改行为 API。

## I. 决策门与配置默认

- D-011 是生产 verifier/wire adapter 的硬阻塞；当前只可实现行为 core 和 injected test adapter。
- SRV-D-006 只提供默认预算；auth 风险事件必须与 transport 限流通过 typed command 联动。

## J. 本模块任务卡

| 任务 | Wave | 单一目标 | 依赖 |
| --- | --- | --- | --- |
| [`implement-auth-behavior-core-and-verifier-port`](../../../.spec/tasks/implement-auth-behavior-core-and-verifier-port.md) | Wave 4 | 定义 opaque credential、auth request/result、secret-safe verifier SPI和串行服务，不选择D-011 wire/算法。 | `implement-host-runtime-bounded-ports`, `implement-observability-diagnostic-metrics-trace-pipeline` |
| [`implement-auth-replay-grant-revocation-and-epoch`](../../../.spec/tasks/implement-auth-replay-grant-revocation-and-epoch.md) | Wave 5 | 组合 bounded LRU+monotonic expiry，产出 immutable PermissionGrant并拒绝旧connection/grant epoch。 | `implement-auth-behavior-core-and-verifier-port`, `implement-host-runtime-clock-and-timer-delivery` |

## K. 完成定义

本模块的任务卡全部通过；模块单测/fixture/故障测试通过；Queue Registry、Cargo DAG、许可证与源码红线检查通过；没有供应商类型泄漏、无界队列、任意回调或未见证 FaultClass 推断。
