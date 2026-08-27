# auth 模块

> 认证、票据校验、防重放、连接级权限语义与认证审计事件源。

## 模块定位与目标

`auth` 拥有"这个连接是谁、允许它做什么"的全部裁决。它是安全红线面：认证、防重放和权限校验不能被任何本地快捷路径（包括 LocalEmbedded）跳过，全部裁决结果可审计。它独立于 [transport](../transport/README.md)（传输失败是可重试错误，认证失败是可拒绝错误——故障域不同）也独立于 [session](../session/README.md)（身份先于会话存在，重连时凭同一身份重新校验）。

## 负责什么

- 握手期身份认证：校验客户端出示的凭据/票据。凭据 wire 格式**未冻结**（公共决策门 D-011，架构源 `docs/architecture/DECISIONS_PENDING.md`）：本仓在公共 Schema 落地前只冻结行为契约——每次握手（含重连）必经防重放校验后才可能被接纳——不得私造 wire 格式或预设具体密码学方案。
- 防重放：维护防重放窗口与 nonce 单调性检查（窗口计时经 [host-runtime](../host-runtime/README.md) 单调时钟）；重放请求以稳定错误拒绝；连续命中发出 `ReplayStorm` 类型化信号（经组装期接线送 [transport](../transport/README.md) 收紧该连接限流）。
- 连接级授权语义：裁决"该身份在该 Session 允许发送哪些 `messageType`"，产出**不可变授权对象**（SRV-D-013）——绑定与撤销由 [session](../session/README.md) 经连接 epoch 驱动，本模块不持有绑定关系；执行点在 transport 的解码后入队前（见 [modules/README.md](../README.md) §3.2 分工约定）。
- 凭据验证材料管理：验证公钥/信任锚从签名配置装载；Secret 与普通配表分离（架构源 ADR-010）。
- 认证审计事件源：登录成功/失败、权限拒绝、重放检测全部发 Audit（durable；Session 建立前的拒绝事件用 `Release` 作用域，不伪造 `sessionId`——对应架构源正例 Fixture `fixtures/valid/logging-auth-reject-audit.json`）。

## 明确不负责什么

- 不做传输、Envelope 解析或限流（归 [transport](../transport/README.md)）。
- 不做 Admission 决策或 Session 生命周期（归 [session](../session/README.md)）；auth 只回答"身份是否有效、权限是什么"，不回答"现在能否接入"。
- 不做 Release 版本匹配（归 [release-agent](../release-agent/README.md)）。
- 不裁决 Gameplay 内权限（技能、物品、管理指令的业务权限归 Runtime/Game）；本模块的边界是**连接与消息类型级**权限。
- 不定义错误码语义（稳定错误分类归架构源契约生成物；本模块只映射到可重试/可拒绝/可致命三类）。

## 拥有的状态与资源

- 防重放窗口状态（时间窗 + nonce 记录，有界）。
- 已验证身份缓存（身份 → 权限上下文，带过期）。
- 凭据验证材料（公钥/信任锚，启动期装载、运行期只读）。

## 输入、输出与稳定接口

- **输入**：握手凭据（经 [session](../session/README.md) 的 Admission 管道转交）、重连时的身份重校验请求。
- **输出**：认证裁决（通过 + 不可变授权对象 / 稳定原因拒绝）、`ReplayStorm` 信号、Audit 事件。
- **稳定接口**：`authenticate(credentials) -> Identity | StableReason`；`authorize(identity, sessionScope) -> PermissionGrant`（不可变对象）；`check_replay(nonce, window) -> Ok | Replayed`。

## 上游与下游依赖

- **上游**：[session](../session/README.md)（Admission 管道调用认证与授权；重连重校验）。
- **下游**：[host-profiles](../host-profiles/README.md)（权限相关 Capability 位查询）、[host-runtime](../host-runtime/README.md)（防重放窗口计时与缓存过期）、[observability](../observability/README.md)（Audit 事件）。

## 生命周期与状态机

- 无自有运行期状态机；身份缓存条目生命周期：`Validated -> Active -> Expired/Revoked`。
- 验证材料随配置快照在启动期冻结；轮换密钥 = 新签名配置版本 + Tick 边界切换（经 [process](../process/README.md) 配置流程）。

## 线程、队列与并发所有权

- 无自有线程；认证在 Admission 调用方（session 编排路径）上同步执行，不在 Reactor 热路径也不在 Simulation Owner Thread 上执行。
- 防重放窗口与身份缓存是本模块内部加锁的小状态，锁不跨越任何 FFI 或队列边界。

## 正常数据流与失败路径

- **正常**：握手凭据 → 票据验证 → 防重放检查 → 身份确立 → 权限上下文产出 → Audit（成功）→ 交回 Admission 管道。
- **失败路径**：
  - 凭据无效/过期/签名不符：稳定原因拒绝，Audit（失败），不消耗防重放窗口配额。
  - 重放检测命中：拒绝并 Audit；连续命中发 `ReplayStorm` 类型化信号，transport 据此收紧该连接限流（组装期接线，无编译依赖边）。
  - 验证材料装载失败：启动失败（配置类错误），不降级放行。
  - Audit 队列背压：认证结果**不得**在 Audit 不可写时静默放行——遵循架构源 ADR-011 的持久队列满载语义，由编排层停止新接入。

## 错误分类、恢复与降级

- **可重试**：无（认证裁决不重试；客户端可重新发起握手）。
- **可拒绝**：凭据无效、票据过期、重放、权限不足——全部返回稳定错误。
- **可致命**：验证材料损坏或缺失（启动期发现，进程拒绝启动）。
- **降级**：不存在认证降级路径；任何"跳过认证"的配置在生产 Profile 中不可表达（dev-only 后门不得进生产）。

## 配置、Capability 与安全约束

- 凭据验证材料经签名配置装载；密钥/凭据不入库、不进 prompt、不进日志（本仓 [rules/system.md](../../.spec/rules/system.md)）。
- LocalEmbedded 使用**同一**权限路径，不因单进程而绕过（架构源 ADR-009）。
- 本模块的改动属安全面：按本仓调度规则永不走快速收口通道，至少快审。

## 日志、Metrics、Trace 与 Audit

- Audit（durable）：登录、拒绝、重放、权限变更——默认不可静默丢失。
- Metrics：认证成功/失败率（按稳定原因分类）、重放命中数、身份缓存命中率。
- 全部事件携带公共 correlation 字段；凭据内容与身份敏感字段在入队前脱敏。

## 测试面、故障矩阵与性能指标

- **测试面**：认证成功/失败矩阵、防重放窗口边界（窗口内重放、窗口外过期 nonce）、权限上下文与 `messageType` 过滤联动、重连重校验、LocalEmbedded 同权限路径保真。
- **故障矩阵**：伪造票据、过期票据、签名密钥不匹配、重放风暴（联动限流）、Audit 背压下的接入停止。
- **性能指标**：单次认证裁决延迟（Admission 路径预算内）、防重放检查吞吐、身份缓存内存上限。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-009-local-transport.md`（同权限路径保真）、`docs/adr/ADR-011-observability.md`（Audit durable 语义、correlation 作用域）、`docs/adr/ADR-012-release-update-maintenance.md`（重连只能路由到 Catalog 允许目标，身份重校验是前提）。
- 凭据票据本身尚无公共 Schema——公共决策门 D-011（架构源 `docs/architecture/DECISIONS_PENDING.md`）；冻结须在架构源新增 ADR + Schema + 正反 Fixture，本模块不得先行私造 wire 格式。
- 认证拒绝的可观测证据：`schemas/logging-event.schema.json` 正例 `fixtures/valid/logging-auth-reject-audit.json`（Release 作用域，Session 前事件不伪造 `sessionId`）。

## 尚未批准的决策门

- **D-011**（认证凭据 wire 格式与验证机制，公共决策门）：临时默认值为**不冻结**——只固定行为契约（握手必经防重放校验），wire 格式、密钥体系与验证机制随架构源 ADR + Schema 一并裁决；本仓不预设具体密码学方案。登记见 [modules/README.md](../README.md) §11.1。
- **SRV-D-005**（防重放窗口参数）：临时默认值为 30 秒窗口 + 单调 nonce（仅参数，不含格式设计）；安全评审确认。
- **SRV-D-013**（授权对象派生与撤销语义）：见 [session](../session/README.md) 与 [modules/README.md](../README.md) §11.2。
- 受 **D-007**（N/N-1 兼容窗口）间接影响：若未来开放兼容窗口，票据与授权对象需声明跨版本语义，须随该 ADR 一并评审。
