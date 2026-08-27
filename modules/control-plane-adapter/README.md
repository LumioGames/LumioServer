# control-plane-adapter 模块

> 外部控制面的进程内边界：签名命令接收与验证、fencing token 校验、命令幂等队列、就绪/排空/退出证据上报。

## 模块定位与目标

`control-plane-adapter` 是外部控制面（部署编排/supervisor）与本进程之间的唯一双向边界。集群期望状态——哪些 Pool 存在、各服务哪个 Release、何时以目标实例替换旧实例——由外部控制面唯一拥有，本进程不拥有（架构源 ADR-012）。本模块只做四件事：验证进来的签名命令、把合法命令放进有界幂等队列、校验 fencing token、把本进程的就绪/排空/退出证据报出去。它不理解维护语义，也不推进任何状态机。

## 负责什么

- 签名命令接收：从命令通道（传输形态属公共决策门 D-010，临时默认为文件/CLI 投递）读取维护等管理命令。
- 命令验证：`operator` 签名校验（信任锚来自签名配置）、按架构源 `schemas/maintenance-command.schema.json` 做结构校验、fencing token 有效性校验——携带过期 fencing token 的命令以稳定错误 `FencingTokenStale` 拒绝（架构源 ADR-012）。
- 幂等命令队列：以 `maintenanceId` 为幂等键的有界队列；重复命令不入队，返回当前进度；完成后的重放返回终态。
- 状态上报：向控制面报告就绪状态、drain 进度、维护进度与退出证据（`ReadyToExit` 与分类退出码是控制面启动目标实例的前置证据）。
- 命令与裁决全程 Audit（durable）。

## 明确不负责什么

- 不拥有集群期望状态、Pool 替换决策或目标实例激活（归外部控制面；本进程终态是 `ReadyToExit`/退出，起新实例不是本进程的动作）。
- 不推进维护进度状态机（归 [maintenance-agent](../maintenance-agent/README.md)，它从本模块的队列消费命令）。
- 不做玩家身份认证（归 [auth](../auth/README.md)）；操作者签名与玩家凭据是两条独立信任链。
- 不定义命令 Schema、fencing 语义或错误码（归架构源）。
- 不做本进程 Release 身份与健康的裁决（归 [release-agent](../release-agent/README.md)；本模块只转发其产出的状态视图）。

## 拥有的状态与资源

- 已验证命令的有界幂等队列（`maintenanceId -> 命令 | 进度引用`）。
- 操作者签名信任锚（启动期从签名配置装载，运行期只读）。
- 当前 fencing token 视图与校验状态。
- 对外状态报告的缓冲与投递句柄。

## 输入、输出与稳定接口

- **输入**：外部签名命令（不可信，须验证）、[maintenance-agent](../maintenance-agent/README.md) 的进度回写、[release-agent](../release-agent/README.md) 的健康/身份视图、[process](../process/README.md) 的生命周期状态。
- **输出**：已验证命令队列（供 maintenance-agent 消费）、状态/证据报告（送控制面）、拒绝的稳定错误（`FencingTokenStale`、签名无效、Schema 非法）。
- **稳定接口**：`poll_commands(budget) -> Vec<VerifiedCommand>`（maintenance-agent 拉取）；`report_progress(maintenanceId, progress)`；`report_lifecycle(state, evidence)`；`current_fencing() -> FencingView`。

## 上游与下游依赖

- **上游**：[maintenance-agent](../maintenance-agent/README.md)（消费命令、回写进度）、[process](../process/README.md)（生命周期状态上报）。
- **下游**：[host-runtime](../host-runtime/README.md)（通道读取的定时轮询）、[observability](../observability/README.md)（Audit 事件）。

## 生命周期与状态机

- 无业务状态机；命令条目生命周期：`Received -> Verified -> Queued -> Consumed（由 maintenance-agent 拉走）`，验证失败即 `Rejected`（含稳定原因，写 Audit）。
- 随 [process](../process/README.md) 在平台服务层初始化；`Stopping` 期间拒绝新命令但保持状态上报直至退出。

## 线程、队列与并发所有权

- 命令通道轮询经 [host-runtime](../host-runtime/README.md) Timer 投递的类型化命令驱动，无自有常驻线程。
- 命令队列容量小且固定（SRV-D-015 端口约定）；满载以稳定错误拒绝——控制面负责重试，进程内不缓存无界命令。

## 正常数据流与失败路径

- **正常**：签名命令 → 签名/Schema/fencing 三重验证 → 幂等检查 → 入队 → maintenance-agent 拉取执行 → 进度回写 → 状态上报 → 终态报告。
- **失败路径**：
  - 签名无效/操作者越权：拒绝，Audit（Warn），不入队。
  - Schema 非法（缺 scope、Forced 带非零宽限期等）：拒绝并返回结构化原因（对应架构源反例 Fixture `fixtures/invalid/maintenance-missing-scope.json`、`fixtures/invalid/maintenance-forced-with-grace.json`）。
  - fencing token 过期：以 `FencingTokenStale` 拒绝——旧实例不得执行新纪元的替换命令。
  - 重复 `maintenanceId`：不重复执行，返回当前进度或终态（幂等契约）。
  - 状态上报通道不可用：本地缓冲并计数；上报是尽力而为，命令执行的正确性不依赖上报成功。

## 错误分类、恢复与降级

- **可重试**：状态上报的瞬时投递失败（有限缓冲重试）。
- **可拒绝**：签名/Schema/fencing/越权/队列满——全部稳定错误。
- **可致命**：签名信任锚装载失败（启动期拒绝启动）。
- **降级**：无隐式降级；命令通道不可用时进程继续服务（控制面缺席不影响数据面）。

## 配置、Capability 与安全约束

- 操作者信任锚经签名配置装载；密钥不入库、不进日志（本仓 [rules/system.md](../../.spec/rules/system.md)）。
- 管理面与玩家数据面隔离：命令通道不占用玩家传输栈，不经 [transport](../transport/README.md)。
- 本模块改动属安全面：按本仓调度规则至少快审，不走快速收口通道。

## 日志、Metrics、Trace 与 Audit

- 每条命令的接收/验证/拒绝/消费写 Audit（durable，关联 `maintenanceId`、`operator`、作用域三元组；correlation `scope` 为 `Release`）。
- Metrics：命令验证成功/拒绝率（按稳定原因）、队列深度、上报投递失败数。

## 测试面、故障矩阵与性能指标

- **测试面**：签名验证矩阵、fencing token 过期拒绝、幂等重放（进行中/已完成两种）、Schema 反例全覆盖、Stopping 期拒新命令。
- **故障矩阵**：伪造签名、过期 fencing、命令风暴打满队列、上报通道中断。
- **性能指标**：无热路径要求；命令验证延迟与队列操作开销记录即可。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-012-release-update-maintenance.md`（控制面所有权、fencing、幂等键）。
- 架构源 `schemas/maintenance-command.schema.json`：正例 `fixtures/valid/maintenance-graceful.json`、`fixtures/valid/maintenance-forced.json`；反例 `fixtures/invalid/maintenance-missing-scope.json`、`fixtures/invalid/maintenance-forced-with-grace.json`。

## 尚未批准的决策门

- **D-010**（控制面命令传输与期望状态存储）：临时默认值为文件/CLI 签名命令投递 + 外部进程 supervisor；传输选型属部署层，命令 Schema 或 fencing 语义变更须新 BaselineId。登记见 [modules/README.md](../README.md) §11.1。
- **SRV-D-015**（内部命令端口约定，含本模块命令队列容量与满载拒绝）：见 [modules/README.md](../README.md) §11.2。
