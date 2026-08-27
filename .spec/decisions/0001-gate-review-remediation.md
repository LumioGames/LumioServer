# 0001 · 按架构门审退回结论重构模块边界（聚合根收权 + 控制面收缩 + host-runtime 新设）

- 日期：2026-08-27
- 状态：生效

## 背景

`docs/LumioServer_Architecture_Gate_Review_c5350a5.md` 对模块骨架文档做门审，裁决为退回：10 项 P0（聚合根缺位、故障分级依赖"可捕获性"推断、维护进度机越权拥有生命周期与 `TargetActivated`、Audit 所有权与 ack 语义含混、连接注册表多写者、`ClientReplicaSession` 命名侵权、维护 deadline 时钟域错误、日志关联字段允许伪造、字段拼写偏离 Schema、依赖图三种语义混画）与 7 项 P1（定时语义九处分散、路由职责名不副实等）。其中公共语义缺口需先在架构源修复（已完成：ADR-001/006/011/012 修订、三个 Schema 变更、新增 D-009/D-010/D-011 决策门、BaselineId 升至 `LGE-V1.1-2026-08-27`），本仓随新基线落地内部重构。

## 决策

1. **`world-slot` 升级为 Host 侧唯一聚合根**：Host Admission Gate、生命周期 epoch、Quiesce/Drain/Snapshot/Stop 原子序列、pacing 启停、`FaultClass` 裁决五项收权；`session`/`pacing`/`maintenance-agent`/`coreclr-host` 相应收缩为命令执行方。
2. **故障分级只认 Runtime 见证**：`coreclr-host` 只转交 `FaultClass`（`SessionLocalProven`/`SlotStateUnproven`/`ProcessFault`），缺见证默认 `SlotStateUnproven`；Host 永不从异常可捕获性推断故障域。
3. **控制面收缩**：集群期望状态与目标实例激活归外部控制面；删除维护进度机的 `TargetActivated` 阶段，本进程终态为 `ReadyToExit`/退出。新设 `control-plane-adapter` 承担签名验证、fencing、幂等与证据上报；`maintenance` 更名 `maintenance-agent`，是不拥有生命周期的进度机；维护 deadline 用 `graceDeadlineSeconds` 一次性换算单调时钟。
4. **所有权与命名修正**：`network` 更名 `transport` 并成为连接注册表唯一写入者（session 经类型化命令请求变更）；`release-router` 更名 `release-agent` 收缩为本进程身份代理；服务端每连接记录命名 `ServerConnectionSession`，禁止映射公共 `ClientReplicaSession`；Audit 队列归 `observability`、WAL/TxnJournal/CommandLog 归 `persistence-host`，persistence commit ack 与 Audit durable ack 为两个独立完成信号。
5. **`host-runtime` 新设为最底层模块**：单调时钟、Timer、取消树、任务监督收拢于此；任何模块不得自建 sleep/轮询线程；跨模块协作一律类型化命令/事件 + 有界端口 + 显式 ack，禁止任意闭包回调注册（端口参数见 SRV-D-015）。
6. **文档基建**：`modules/README.md` 拆三张依赖图（编译/命令流/事件-ack 流）、增设 Queue Contract Matrix 与术语拼写表（camelCase 字段/PascalCase 类型/snake_case ABI，废除"叙述惯例"豁免）；新增 `protocol-dispatch` 封锁占位模块钉住 D-009 边界；内部参数以 SRV-D-001..017 决策门登记，未测量数值不得写死。

## 后果

- 模块数从 12 增至 15（新设 3 个），文档面与决策门数量上升；换来所有权唯一、依赖单向、命令/事件语义可验证。
- 聚合根把关键序列集中到 `world-slot`，其正确性成为单点关键面——由 epoch/`StaleEpoch` 契约与门审测试面兜底。
- `protocol-dispatch`/凭据 wire 格式在公共决策门（D-009/D-011）冻结前封锁，相关实现推迟。
- 全部 SRV-D 数值是临时默认值，Foundation/Vertical Slice 阶段需按测量确认并逐条转正。
