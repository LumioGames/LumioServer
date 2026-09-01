# host-runtime 模块

> 单调时钟、Timer 服务、取消树、任务监督、有界执行原语——全仓时间与异步骨架的唯一所有者。

## 当前实现（R-00359 切片）

本 crate 目前只落地切片消费的最小面：单调时钟（含可 `advance` 的测试偏移）、有界 MPSC、具名受监督线程。Timer wheel、层级取消树和共享执行器仍按模块规划后续补齐。生产代码不得绕过本 crate 直接 `spawn`。

## 模块定位与目标

`host-runtime` 把"时间从哪里读、定时任务在哪个线程唤醒、异步任务 panic 之后谁知道、取消如何级联"收拢为一个最底层模块。设立它的原因是：重连窗口、健康检查、防重放窗口、Checkpoint 调度、Watchdog、维护 deadline 等九处定时语义如果散落各模块，会催生九种隐式线程与九种时间语义（架构门审 P1-01）。本模块不含任何业务语义，是编译依赖图的最底层；其他模块只做时间与任务的**消费方**。

## 负责什么

- 单调时钟：进程唯一的单调时间源；墙钟只用于日志时间戳与外部协议字段，deadline 一律以单调时钟表达（架构源 ADR-012 的宿主侧支撑）。
- Timer 服务：中心化 timer wheel；到期投递为**类型化命令**进入所有者声明的有界队列，不直接在 timer 线程执行任意回调。
- 取消树：`Process -> Pool -> Slot -> Session -> Connection` 层级取消令牌；上层取消自动级联下层；取消后到达的完成是终态且不能写状态（架构源 ADR-006 失败语义的宿主侧执行）。
- 任务监督：受监督任务句柄——panic 被捕获、转为稳定的 `TaskPanicked` 监督事件（经组装期接线上报 [process](../process/README.md) 进程 Watchdog），不允许静默死亡的线程。
- 有界执行原语：有界 SPSC/MPSC 通道、有界任务队列、join barrier、重试预算/退避策略的类型化实现，供上层模块声明式使用。
- 确定性测试时钟：测试 Profile 下以确定性时钟替换单调时钟源（Adapter 层替换，消费方无感知；支撑架构源 §4.4 Level 2 Determinism）。
- 线程命名与登记：所有经本模块创建的线程具名并登记，供进程 Watchdog 汇聚心跳。

## 明确不负责什么

- 不拥有 TickRate 与 Tick 触发判定（归 [pacing](../pacing/README.md)）；`pacing` 是本模块时钟的消费方。
- 不拥有任何业务定时语义：重连窗口归 [session](../session/README.md)、健康检查归 [release-agent](../release-agent/README.md)、Checkpoint 调度归 [persistence-host](../persistence-host/README.md)、维护 deadline 归 [maintenance-agent](../maintenance-agent/README.md)——本模块只提供"到期投递命令"这一机械事实。
- 不定义队列的容量与满载策略数值（各队列所有者声明，参数属 SRV-D 决策门）。
- 不做日志/事件管道（归 [observability](../observability/README.md)）；监督事件经组装期接线投递，本模块不依赖任何上层。

## 拥有的状态与资源

- 单调时钟源（及测试 Profile 下的确定性替身）。
- Timer wheel 与到期登记表（`timerId -> 所有者队列、命令载荷、epoch 标记`）。
- 取消树节点表与层级关系。
- 受监督任务登记表（线程/任务句柄、心跳、panic 状态）。

## 输入、输出与稳定接口

- **输入**：Timer 注册/取消请求、取消树节点创建/触发、受监督任务的提交。
- **输出**：到期的类型化命令（进所有者有界队列）、`TaskPanicked` 监督事件、时钟读数。
- **稳定接口**：`now_monotonic() -> Instant`；`register_timer(deadline, ownerQueue, command, cancelToken) -> TimerRef`；`cancel(timerRef)`；`cancel_scope(token)` 级联取消；`spawn_supervised(name, task, cancelToken) -> TaskRef`；`bounded_channel(capacity) -> (Tx, Rx)`。

## 上游与下游依赖

- **上游**：全部拥有定时/异步语义的模块（pacing、session、auth、release-agent、persistence-host、maintenance-agent、world-slot、process、transport、observability）。
- **下游**：无。本模块是编译依赖最底层；监督事件的上报通过 [process](../process/README.md) 在组装期注入的类型化端口完成，不产生对上编译依赖。

## 生命周期与状态机

- 随 [process](../process/README.md) 在 `observability` 之前最早初始化（观测管道自身的 Sink 线程也受本模块监督）；析构时最后停止——先取消全部 Timer、级联取消树、join 全部受监督任务，再退出。
- 无业务状态机；Timer 条目生命周期：`Registered -> Fired（命令已入所有者队列）/ Cancelled`；投递失败（所有者队列满）按所有者声明的满载策略处置并计数，不静默丢弃。

## 线程、队列与并发所有权

- 拥有 Timer 线程与（若部署启用）共享执行器线程池；全部线程具名、受监督。
- 到期投递是"命令入队"而非"回调执行"：业务逻辑永远在所有者自己的线程/队列上下文运行，timer 线程不执行业务代码——这使死锁与重入在结构上不可表达。
- 取消令牌是无锁只读快照；级联取消是幂等操作。

## 正常数据流与失败路径

- **正常**：所有者注册 Timer（携带自身队列与命令）→ 到期 → 命令入队 → 所有者线程消费执行。
- **失败路径**：
  - 所有者队列满：按该队列声明的满载动作处置（丢弃计数/拒绝/背压），投递失败计入 Metrics 并产生诊断事件；deadline 类命令的投递失败升级为监督事件。
  - 受监督任务 panic：捕获、记录、发 `TaskPanicked`；是否重启由所有者策略声明，不隐式重启。
  - 时钟源异常（平台故障）：上报进程级处置；单调时钟不受墙钟跳变影响。
  - 取消后的迟到到期：以 epoch/token 校验丢弃，不投递。

## 错误分类、恢复与降级

- **可重试**：无（投递失败不隐式重试；重试预算是显式原语，由调用方声明使用）。
- **可拒绝**：非法注册（零容量队列、已取消的 token）。
- **可致命**：单调时钟源不可用、Timer 线程自身 panic——进程级处置。
- **降级**：无隐式降级；Timer 精度参数属部署配置。

## 配置、Capability 与安全约束

- Timer wheel 精度、执行器线程数来自不可变配置快照。
- 确定性时钟替换只在测试类 `roomMode` 的 Profile 下可表达（[host-profiles](../host-profiles/README.md) 声明），生产 Profile 强制真实单调时钟。
- 本模块不接触任何外部输入或凭据。

## 日志、Metrics、Trace 与 Audit

- Metrics：活跃 Timer 数、到期投递延迟（注册 deadline 与实际入队时刻之差）、投递失败数、受监督任务 panic 数、取消级联耗时。
- `TaskPanicked` 与投递失败产出 Diagnostic 事件（Error 级）；本模块无 Audit 义务（业务审计由命令所有者记录）。

## 测试面、故障矩阵与性能指标

- **测试面**：到期投递顺序、取消级联（父取消后子 Timer 全部失效）、panic 捕获与监督事件、确定性时钟下的可重放调度、投递失败的满载策略执行。
- **故障矩阵**：队列满投递失败、取消竞争（到期与取消同刻）、timer 线程 panic、墙钟跳变不影响单调 deadline。
- **性能指标**：Timer 注册/触发吞吐、到期投递延迟 p99、10k 级并发 Timer 的内存与 CPU 开销。

## 对应 ADR、Schema 与 Fixture

- 架构源 `docs/adr/ADR-001-session-lifecycle.md`（时钟所有权分界的宿主侧执行）、`docs/adr/ADR-006-native-managed-abi.md`（取消/超时/销毁后完成是终态）、`docs/adr/ADR-012-release-update-maintenance.md`（维护 deadline 的单调时钟转换点）。
- 无本模块专属公共 Schema；时间字段进入公共契约时一律遵循对应 Schema（如 `maintenance-command.schema.json` 的 `graceDeadlineSeconds`）。

## 尚未批准的决策门

- **SRV-D-012**（执行器与 Timer 模型：专用具名线程 vs 共享执行器、timer wheel 精度、panic 重启策略）：临时默认值为每所有者专用具名线程 + 单 Timer 线程 + 不隐式重启；Foundation 阶段按线程数与调度开销测量确认。登记见 [modules/README.md](../README.md) §11.2。
