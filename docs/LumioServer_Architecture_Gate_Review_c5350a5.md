# LumioServer 架构门审查报告

**审查对象：** `LumioServer` commit `c5350a5`。该提交共改动 14 个文件，范围是根 `README.md`、`modules/README.md` 与 12 个模块 README，没有改动受保护的架构镜像。

**规范基线：** `LGE-V1.0-2026-08-27`。上传的 `LumioGameEngine V3 v0.3` 文件只是兼容指针，已经明确废弃，不能作为本轮规范来源。

本报告按用户提供的对抗式架构门禁执行。

---

## 1. 裁决：**退回**

发现：

- **10 个 P0**
- **7 个 P1**
- **2 个 P2**

当前文档不能作为 Foundation 实现基线。

需要强调：问题不在于“还缺几个工具类”，而在于现有模块图没有冻结真正的状态所有权、控制面边界和跨模块命令契约。现在直接建立 Cargo Workspace，会把隐式依赖、双重状态所有权和错误的故障隔离固化到 crate API 中。

---

## 2. 一句话总评

**最大的结构性风险是：公共 `WorldSlotHost` 聚合生命周期被拆散给 `process/session/pacing/world-slot/maintenance` 五个独立状态机，同时依赖图又隐藏了它们之间的双向控制路径，导致实现时只能靠 `process` 上帝模块、反向回调或共享可变注册表把系统重新拼起来。**

---

# 3. 问题清单

## P0

### P0-01：`WorldSlotHost` 聚合所有权被拆散，公共生命周期已失去唯一所有者

**严重度：P0**

**证据**

公共 ADR-001 明确规定：

> `WorldSlotHost` owns process resources, admission, Wall Clock, pacing and host lifecycle.

并规定只有所有者可以发起状态迁移。

但本仓把这些状态拆给了多个独立所有者：

- `session` 拥有 Admission 开关。
- `pacing` 拥有 Wall Clock、暂停位与 Tick 调度。
- `world-slot` 拥有 `WorldSlotHost` 状态机。
- `process` 又拥有 `Starting/Ready/Serving/Draining/Stopping/Faulted`。
- `maintenance` 拥有另一套 `AdmissionClosed/Draining/Persisting/Kicking/...` 状态机。

**违反**

- 放行门槛 1：可变状态所有权必须唯一。
- 放行门槛 3：权威状态变化只能在合法 owner 和 Tick Barrier 应用。
- ADR-001：`WorldSlotHost` 是 Host 侧聚合所有者。
- 架构源 §3.2：状态迁移只能由所属者发起。

**Foundation 落地时为什么会爆**

维护关闭时会出现至少四个并发写入者：

1. `maintenance` 决定关闭 Admission。
2. `session` 修改 Admission 开关。
3. `pacing` 修改暂停位。
4. `world-slot` 修改 Host 状态。
5. `process` 再把进程置为 Draining/Stopping。

只要其中任何一步失败或超时，就会产生以下半活状态：

- Admission 已关，但 Slot 仍 Running。
- Tick 已停，但 Ingress 仍接受输入。
- Snapshot 已开始，但重连 Session 又绑定到 Slot。
- `process` 认为 Draining，`world-slot` 仍认为 Running。
- `maintenance` 进入 Failed，Slot 却已经不可逆 Quiescing。

文档目前没有定义这些状态之间的原子迁移、epoch、幂等命令和失败回滚。

**具体怎么改**

1. 修改 `modules/world-slot/README.md`：
   - 把 `world-slot` 明确为 **`WorldSlotHost` Host 聚合根**。
   - 它唯一拥有：
     - Host Admission Gate；
     - Slot lifecycle epoch；
     - Quiesce/Drain/Snapshot/Stop 原子序列；
     - Pacing 启停状态；
     - Simulation Owner Thread。
   - `session`、`pacing`、`network` 只能执行它下发的机械命令。

2. 修改 `modules/session/README.md`：
   - 删除“拥有 Admission 开关状态”。
   - 改为“消费 Host Admission Gate，执行每个连接的接纳或拒绝”。

3. 修改 `modules/pacing/README.md`：
   - `pacing` 保留内部 Tick 调度状态，但 Host 级 `pause/resume` 迁移只能接受 `world-slot` 的 typed command。
   - 删除“状态迁移由编排层发起”这种多发起者表述。

4. 修改 `modules/process/README.md`：
   - `process` 只拥有进程状态。
   - 关闭时只能请求 `world-slot.quiesce()`，不能直接拼接 Session、Pacing、Persistence 的内部操作。

5. 修改 `modules/maintenance/README.md`：
   - 维护模块只拥有维护命令进度。
   - 不得自行拥有 Host 生命周期阶段；Host 阶段通过 `WorldSlotHost` 状态视图体现。

---

### P0-02：依赖图不是实际依赖图，隐藏边已经形成反馈环

**严重度：P0**

**证据**

`modules/README.md` 声称依赖“只能从上向下”，并声称：

- `network` 永不依赖 `session/world-slot`；
- `process` 对所有模块的组装边故意不画；
- `observability/host-profiles` 是全员只读依赖。

但正文实际存在以下未画边：

- Tick 流程中，`world-slot` 直接从 `network` 取 Ingress、向 Egress 写入，因此存在 `world-slot -> network`。
- `network` 把握手和连接事件交给 `session`，而 `session` 又向 `network` 下发绑定、踢出和注册表更新，形成 `network ↔ session` 运行时反馈环。
- `maintenance` 明确向 `pacing` 发指令，`world-slot` 又声明接受维护的 Quiesce/Snapshot/销毁指令，但主图没有这些边。
- `persistence-host` 在磁盘满时“触发维护”，而维护又等待 persistence 回执，形成 `maintenance ↔ persistence-host`。
- `host-profiles` 既被所有模块依赖，又保存“Preset 到模块装配差异的映射”，这会使它反过来知道模块组成。

公共架构明确区分“源码依赖图”和“运行时调用图”，并指出源码依赖箭头不是运行时调用方向。本仓把二者混成了一张图。

**违反**

- 放行门槛 2：依赖方向必须无环。
- 放行门槛 7：README 与模块正文不得静默冲突。
- 公共架构 §2.2：源码依赖、生成物依赖、运行时加载关系必须分图表达。

**Foundation 落地时为什么会爆**

实现者会有三种选择，三种都错：

1. 让模块相互引用 crate，形成 Rust crate cycle。
2. 把所有调用接口挪到 `process`，形成上帝模块。
3. 使用动态 callback/trait object 绕过依赖图，形成无法审计的运行期环。

尤其 `network ↔ session` 和 `maintenance ↔ persistence-host` 并不是不能存在反馈，而是必须被表达为：

- 单向编译依赖；
- 双向的 typed command/event port；
- 每个方向有明确队列 owner、容量、失败和取消语义。

当前文档没有做这一区分。

**具体怎么改**

修改 `modules/README.md` §3：

1. 删除当前单一 Mermaid 依赖图。
2. 改成三张图：
   - **crate/source dependency DAG**；
   - **runtime command graph**；
   - **runtime event/ack graph**。
3. `process` 放在 DAG 外作为 Composition Root，不得归类为“基础层公共依赖”。
4. 所有反馈环必须经无状态 contract crate 中的 typed port，而不是直接 crate 反向依赖。
5. 每个模块 README 的“上游与下游依赖”拆成：
   - 编译依赖；
   - 接收的 Commands；
   - 发出的 Events；
   - 调用的同步 Query Ports。
6. 不得再使用“图中未画出”“经编排间接触达”隐藏实际边。

---

### P0-03：`session` 正在写 `network` 所有的连接注册表

**严重度：P0**

**证据**

`network` 明确声明连接注册表归自己所有，包括传输句柄、限流状态和权限上下文绑定。

但 `session` 明确写道：

> 把 auth 产出的权限上下文写入连接注册表。

并把“连接注册表更新”列为自己的输出。

模块总文档又试图用一句“绑定归 session，执行点归 network”消解这个冲突。

**违反**

- 放行门槛 1：同一可变状态只能有一个所有者。
- `network` 自身“不拥有认证语义，但拥有连接注册表”的边界。
- LocalEmbedded 同权限路径要求：不能通过共享对象引用旁路正常绑定流程。

**Foundation 落地时为什么会爆**

如果 `session` 能直接写 `network::ConnectionRegistry`：

- `session` 必须依赖 network 内部类型；
- network 的连接 epoch、断开竞态和权限绑定顺序会泄露给 session；
- 旧连接断开后迟到的绑定可能覆盖新连接；
- 权限撤销、票据过期和重连重校验没有原子更新点；
- Reactor 线程可能在绑定更新过程中读取半更新的权限状态。

所谓“只读权限绑定”只描述读取方式，不能消除创建、替换和撤销时的可变写操作。

**具体怎么改**

1. 在公共架构源先定义版本化的 Host 权限授予契约；在 Schema/ADR 完成前，本仓不得私造 wire 或跨仓格式。
2. `auth`：
   - 唯一生产不可变权限裁决结果；
   - 唯一拥有票据验证、防重放和撤销语义。
3. `session`：
   - 只建立 `SessionRef ↔ ConnectionRef` 关联；
   - 只向 network 发送 `BindConnection`/`UnbindConnection` typed command；
   - 不接触注册表对象。
4. `network`：
   - 是连接注册表唯一写入者；
   - 在自身线程/队列边界原子应用绑定；
   - 用 connection generation/epoch 拒绝迟到绑定。
5. 在 `modules/network/README.md`、`modules/auth/README.md`、`modules/session/README.md` 和 `modules/README.md` 同步改写，并补充权限过期、撤销和重连竞态流程。

---

### P0-04：`session` 发明了平行状态机，并错误映射公共 `ClientReplicaSession`

**严重度：P0**

**证据**

公共模型区分：

- Server：`WorldSlotHost -> ServerSimulationSession`
- Client：`ClientReplicaSession`
- `SimulationSession` 归 Runtime；
- `ClientReplicaSession` 归客户端。

而 `modules/session/README.md` 定义：

```text
Admitted -> Handshaking -> Active -> ReconnectWindow ...
```

并逐项标注它与公共 `ClientReplicaSession` 的 `Negotiating/Synchronizing/Active/Resyncing` 对应关系，随后又声称“状态迁移只能由本模块发起”，引用公共状态机所有权规则。

**违反**

- 放行门槛 4：不得重定义公共状态机。
- 公共架构 §1.2：`SimulationSession` 不等于远端 Client 对象。
- 公共架构 §3.2：ClientReplicaSession 的 owner 是 Client，不是 Server Host。

**Foundation 落地时为什么会爆**

实现者会自然把 `Active/Resyncing/Handshaking` 当成一套跨进程共享状态，从而出现：

- Server 推断 Client 本地 Replica 是否 Active；
- Server Connection 状态与 Runtime ReplicationContext 状态互相覆盖；
- 重连窗口关闭时误销毁 Runtime SimulationSession；
- Server 状态机增加字段后，被错误当作 wire 协议；
- Client、Runtime、Server 三方各有一套名字相似但语义不同的状态机。

**具体怎么改**

修改 `modules/session/README.md`：

1. 把该状态机命名为内部 `ServerConnectionSession` 或 `RemoteClientSessionRecord`。
2. 明确它仅表示 Server 侧连接身份、重连保留和 Slot 关联。
3. 删除所有“对应 ClientReplicaSession 某状态”的映射。
4. 删除引用公共 Client 状态机所有权规则的表述。
5. `ReplicationContext` 只保存 opaque handle，不拥有其 Replication 状态。
6. 如果该状态需要跨 wire 可见，必须先在架构源新增独立 Schema、ADR 和 fixtures；否则明确为 Host 私有状态。

---

### P0-05：公共契约字段被文档级重定义

**严重度：P0**

**证据**

#### Envelope

公共 Schema 使用：

```text
protocolVersion
sessionId
productId
gameReleaseId
messageType
...
```

且 `messageType` 是固定枚举。

根 README 却使用：

```text
ProtocolVersion
SessionId
ProductId
GameReleaseId
MessageType
```

`modules/README.md` 甚至明确称 PascalCase 是“叙述惯例”。

#### MaintenanceCommand

Schema 必填字段包含 `issuedAt`；`broadcastCode` 是公共可选字段，正例 fixture 明确使用。

但 `maintenance` 的“按 Schema 校验”字段清单漏掉了 `issuedAt`，也没有在输入校验责任中说明 `broadcastCode`。

#### NativeManagedAbi

JSON Schema 使用 `abiVersion/structSize/capabilityBits`。

`coreclr-host` 却称“按 Schema 校验 `abi_version/struct_size/capability_bits`”。这些是 C ABI 表命名，不是 JSON Schema 字段名。

**违反**

- 放行门槛 4：公共 Schema 不得被本仓重定义。
- 本仓 code-style：已发布 Schema 标识符必须保持原拼写。
- 本仓 repository architecture：冲突必须回架构源更新，Host 不得自行改写。

**Foundation 落地时为什么会爆**

文档中的标识符最终会变成：

- Rust struct 字段；
- Serde rename；
- C# generated DTO；
- Metrics label；
- 测试 fixture；
- 运维脚本字段。

允许“散文写法”与 Schema 不一致，会直接生成第二套手写协议。

**具体怎么改**

- `README.md` §Network 与 Session：全部改成 Schema 原始 camelCase。
- `modules/README.md` §10：删除“PascalCase 是叙述惯例”。
- `maintenance/README.md`：按 Schema 精确列出 required 与 optional 字段。
- `coreclr-host/README.md`：
  - 明确 JSON descriptor 使用 camelCase；
  - C ABI Root Table 使用 snake_case；
  - 二者不得混写为同一个 Schema。
- 所有模块 README 执行一次结构化字段全文比对；公共字段一律使用生成源拼写。

---

### P0-06：Maintenance 的 deadline 同时被定义成 Logical Tick 和 15 分钟 Wall Clock

**严重度：P0**

**证据**

公共 Schema 定义的是：

```text
deadlineTick: TickId
```

`maintenance` 状态机又规定：

> `deadlineTick` 到达即从 Draining 进入 Kicking。

但 SRV-D-010 的临时默认值是“15 分钟”。

同时 Forced 流程会立即停止 Tick 提交。

**违反**

- 公共 ADR-001 的 Wall Clock / Logical Tick 分离。
- 放行门槛 5：未批准决策不得混写成既定语义。
- 公共 MaintenanceCommand Schema 的时钟域。
- 维护必须最终收敛、不能依赖一个可能停止推进的时钟。

**Foundation 落地时为什么会爆**

存在四个无解问题：

1. Pool 维护期间可能没有 active WorldSlot，`deadlineTick` 属于哪个世界？
2. 多 Slot 后不同 Slot 的 TickId 不一致。
3. Paused/Quiescing 后 Tick 不推进，deadline 永不到达。
4. “15 分钟”无法无损转换成固定 Tick 数；TickRate 可以变、服务器可能降频。

因此 Graceful 维护无法保证在规定时间内收敛。

**具体怎么改**

这不是 LumioServer 可以本地修补的问题，必须先修改公共架构源：

1. 在 ADR-012 新增“Maintenance deadline clock domain”决策。
2. 更新 `maintenance-command.schema.json` 和正反 fixtures。
3. 明确：
   - 管理面 deadline 使用什么时钟；
   - Host 收到命令后如何转换为单调时钟 deadline；
   - 是否还保留 Slot 级 Tick cut；
   - 无 active Slot、Paused、时钟跳变时的语义。
4. 发布新 BaselineId。
5. 新基线完成前：
   - 删除 SRV-D-010 的“15 分钟”既定默认；
   - 在 `maintenance/README.md` 标为架构源阻塞项；
   - 不得实现 deadline 状态迁移。

---

### P0-07：单个 DS 进程被写成 Release Pool 全局控制面，并要求在自己关闭后启动目标实例

**严重度：P0**

**证据**

`release-router` 声称自己拥有：

> 这个连接应该去哪个 Pool、Pool 现在处于什么状态。

同时每个进程只加载一个 Release。

`maintenance` 的本地状态机又包含：

```text
OldInstanceClosed -> TargetActivated
```

公共架构要求旧实例关闭后启动目标 Release，但没有说“由已关闭的旧进程自己启动”。

**违反**

- 唯一状态所有权：Pool desired state、进程 local state 和集群路由状态没有区分。
- 故障域约束：即将退出的进程不能成为替换动作唯一协调者。
- D-001：单进程只服务一个 Release，跨 Pool 路由必然涉及进程外实体。

**Foundation 落地时为什么会爆**

旧实例不能在完成退出后执行 `TargetActivated`。若由退出前 fork/启动目标：

- 容器和编排平台不一定允许；
- 双实例 fencing 不明确；
- 崩溃发生在“关旧/启新”之间时无人接管；
- 多台机器无法共享一致 Pool 状态；
- Catalog endpoint 与真实实例健康状态可能漂移；
- local process 的 Pool 状态会和外部负载均衡器形成双真相。

**具体怎么改**

1. 新增 `control-plane-adapter` 模块：
   - 接收签名 desired-state/maintenance command；
   - 报告 local readiness、drain、exit evidence；
   - 与外部 supervisor/container orchestrator 交互；
   - 不拥有全局 desired state。

2. 把 `release-router` 收缩并重命名为 `release-agent`：
   - 只拥有本进程 Release 身份；
   - 校验 Catalog/Manifest；
   - 报告 local Serving/Draining/Health；
   - 不裁决跨进程最终路由。

3. 把 `maintenance` 收缩为 `maintenance-agent`：
   - 只执行当前进程的本地维护步骤；
   - 状态终点为 `ReadyToExit/Exited`，而不是 `TargetActivated`。

4. 更新公共 ADR-012：
   - 明确 Pool desired-state owner；
   - 明确 fencing token、命令幂等、重放和 replacement ownership；
   - 明确谁在旧实例退出后启动目标实例。

---

### P0-08：Audit 与恢复日志的所有权仍然冲突，公共 Logging Schema 还无法表达启动期和认证期事件

**严重度：P0**

**证据**

分层设计声称：

- Audit 归 `observability`；
- WAL/TxnJournal/CommandLog 归 `persistence-host`。

这个拆分原则本身合理。

但正文仍有冲突：

- `maintenance` 把 Snapshot/WAL/**Audit** 全部写成“经 persistence-host 落盘”。
- `modules/README.md` 把 Audit/TxnJournal/CommandLog 三条管道全部指向 observability。
- `observability` 又声明 Audit 直接写文件系统，不经过 persistence-host。

公共 Schema 还有两个阻断问题：

1. `durability` 存在，但不在 required 列表中，因此 `Audit` 可以在 Schema 上合法地不声明持久级别。
2. `correlation` 强制要求 `sessionId/worldId/tickId`，而进程启动、Manifest 校验、认证失败发生时这些对象可能还不存在。

**违反**

- 放行门槛 1：Audit 写入的 mutable queue 和 durable acknowledgment owner 不唯一。
- 放行门槛 4：本仓文档依赖了公共 Schema 没有保证的 durability 语义。
- ADR-011：Audit、TxnJournal、CommandLog 是独立 durable queues。

**Foundation 落地时为什么会爆**

- Maintenance 可能收到 persistence 成功回执，却没有收到 Audit durable ack。
- observability 与 persistence 可能各写一份“审计证据”。
- 启动失败和认证拒绝事件要么无法通过 Schema，要么被迫伪造 `sessionId/worldId/tickId`。
- Audit 队列饱和后，session 和 maintenance 都会轮询状态，却没有定义谁执行一次性关闭 Admission。
- Sink 失败和磁盘满可能触发两个不同维护动作。

**具体怎么改**

本仓：

1. `maintenance` 同时发起两个独立动作：
   - persistence commit；
   - Audit durable commit。
2. 只有两者都 ack 后才能离开 `Persisting`。
3. `persistence-host` 删除所有 Audit owner 表述。
4. `observability` 删除 TxnJournal/CommandLog 管道 owner 表述。
5. `modules/README.md` 增加独立队列和 ack 表。

架构源：

1. 修改 `logging-event.schema.json`：
   - 对 Audit 建立强制 durability 规则；
   - 明确 EmergencySync 条件。
2. 修改 correlation：
   - 定义基础 correlation；
   - 再按 Process/Release/Session/World/Txn 作用域增加条件字段；
   - 禁止伪造尚不存在的 ID。
3. 增加：
   - 启动期 Audit 正例；
   - 认证失败 Audit 正例；
   - Audit 缺 durability 反例；
   - 不合法作用域组合反例。
4. 发布新 BaselineId。

---

### P0-09：`coreclr-host` 私自把可捕获 Exception 判定为 Session Fault

**严重度：P0**

**证据**

公共 ADR-006 只规定：

- Rust panic 被捕获；
- Managed Exception 被捕获；
- 二者映射为稳定 Error Code。

它没有规定“所有可捕获 Gameplay Exception 都是 Session Fault”。

本仓却明确写道：

> Gameplay Exception（可捕获）→ Session Fault，隔离该 Session，Slot 与其他 Session 不受影响。

**违反**

- 放行门槛 4：不得私自扩展公共错误与故障分类语义。
- 公共 Tick 原子性：异常可能发生在一个 Tick 中间，只有 Runtime 知道是否已经发生部分写入。
- 故障域约束：Host 不能仅凭“异常可捕获”推断状态仍一致。

**Foundation 落地时为什么会爆**

Managed Exception 可能发生在：

- ECS CommandBuffer 尚未提交；
- Voxel 已生成 mutation 但未 commit；
- CrossWorldTxn 已写 CommitIntent；
- Replication projection 已部分构建；
- Gameplay hot reload 切换期间。

捕获异常只证明进程没有立即崩溃，不证明 Slot 状态可继续运行。

若错误地降级为 Session Fault，服务器可能继续运行一个已经不一致的权威 World。

**具体怎么改**

1. `coreclr-host`：
   - 只做异常捕获与稳定 Error Code 转换；
   - 删除“裁决为 Session Fault”；
   - 删除“其他 Session 不受影响”的承诺。

2. 架构源新增 Fault Classification 契约：
   - Runtime 明确返回故障域；
   - 明确当前 Tick 是否未提交、已回滚或状态不可证明；
   - 只有 Runtime 能证明 session-local 且权威状态未污染时，Host 才允许 Session Fault。

3. `world-slot`：
   - 是 SlotFault/Quiesce 的裁决执行者。
4. `process`：
   - 只处理 ProcessFault。
5. `session`：
   - 只执行已经被合法分类为 session-local 的断开动作。

---

### P0-10：RPC/消息分发没有所有者，但根 README 已宣称存在 `RPC Envelope` 与 `MessageId`

**严重度：P0**

**证据**

根 README 声称 Server 消费：

> Root ABI、Capability、Error、RPC Envelope、MessageId、Host、Voxel Port 和 Game Gameplay Contract 生成物。

公共仓库边界又规定 `LumioGame` 拥有 RPC Payload。

但 12 个模块中没有任何模块拥有：

- MessageId/RPC handler registration；
- 请求-响应 correlation；
- deadline/cancellation；
- idempotency/deduplication；
- permission-to-handler 映射；
- response routing；
- handler fault boundary；
- RPC 与 replication message 的区分。

`network` 目前同时承担 transport、Envelope、reliability、权限过滤，并直接把消息送 Simulation Owner Thread。

**违反**

- 放行门槛 3：网络线程不得直接调用 Gameplay。
- 放行门槛 4：本仓不能声称存在尚未给出的公共 RPC 契约。
- `LumioGame` 拥有 RPC Payload，但 Host 必须拥有安全分发、限流和生命周期治理。

**Foundation 落地时为什么会爆**

缺失的职责最终只会落到三个错误位置：

1. `network` 直接注册 Gameplay handler；
2. `session` 变成协议分发和业务路由中心；
3. Runtime 自己解析 transport Envelope。

这三种都会造成 network → Gameplay 反向依赖，或者让玩法语义倒灌 Host。

**具体怎么改**

1. 公共架构源先冻结：
   - RPC/Message Contract；
   - MessageId 命名和版本；
   - request/response/error/cancel 语义；
   - idempotency 与重放边界；
   - 正反 fixtures。
2. 新增 `modules/protocol-dispatch/README.md`：
   - 消费已经通过 transport/auth 校验的消息；
   - 负责 RPC/Message 路由、correlation、deadline、cancel、idempotency；
   - 只把 canonical command 写入有界 Simulation Ingress；
   - 不在 Reactor 线程调用 Gameplay；
   - 不拥有 RPC Payload Schema。
3. `network` 收缩为 transport/framing/reliability，并建议重命名为 `transport`。
4. 公共 RPC 契约未落地前，根 README 删除或明确标注 `RPC Envelope/MessageId` 为阻塞依赖，不能写成已有生成物。

---

## P1

### P1-01：缺少真正的 Host Runtime——Timer、多线程任务监督、取消与关闭屏障没有统一基础

**严重度：P1**

**证据**

现有时间功能散落在：

- `process`：进程 Watchdog 和 heartbeat channel；
- `network`：重传、分片、限流窗口；
- `auth`：30 秒防重放窗口；
- `session`：120 秒重连窗口；
- `world-slot`：5 秒 Watchdog；
- `release-router`：5 秒健康检查线程；
- `persistence-host`：5 分钟或 6000 Tick Checkpoint；
- `maintenance`：15 分钟 deadline；
- `observability`：flush、rotation、sink retry。

`pacing` 只是模拟 Tick 调度器，而且明确无自有线程、无队列，不适合作为通用 Timer。

**违反**

不直接违反公共状态机，但使所有 Watchdog、超时、关闭和重试无法共享一致的：

- 单调时钟；
- cancellation；
- task join；
- shutdown order；
- test clock；
- timeout escalation。

**Foundation 落地时为什么会爆**

每个模块各建一个线程和 Timer 后，会出现：

- 进程关闭后 Timer 仍回调已销毁对象；
- System Clock 跳变导致重连或票据错误；
- 测试无法注入统一 deterministic clock；
- Watchdog 和维护 deadline 使用不同时间源；
- thread panic 无统一监督；
- Join/flush 顺序不可证明；
- 数十个低频线程造成部署与故障诊断复杂化。

**具体怎么改**

新增 `host-runtime` 模块，且只拥有领域无关的 Host 执行基础：

- monotonic `Clock`；
- cancellable `TimerService`/deadline scheduler；
- structured concurrency；
- `Process -> Pool -> Slot -> Session -> Connection` cancellation hierarchy；
- task supervisor、panic reporting、join/shutdown barrier；
- bounded executor/channel primitives；
- retry budget、backoff/jitter；
- thread naming、affinity/priority hook；
- deterministic test clock。

硬约束：

- Timer 到期只能向目标 owner 的有界队列发 typed command；
- 禁止 Timer callback 直接调用 Gameplay；
- `pacing` 继续只处理 Simulation Tick，不变成通用 Timer；
- 不新增无边界的“多线程工具箱”。

---

### P1-02：队列矩阵不完整，Ingress 的 SPSC 结论也没有成立条件

**严重度：P1**

**证据**

`network` 声称 Reactor 和发送线程数量由配置决定，即可能有多个 Reactor；随后又直接断言每 Session Ingress 是单生产者单消费者。

只有这些队列有明确决策门：

- Ingress；
- Egress；
- Diagnostic。

以下队列没有完整容量、满载动作和 owner gate：

- Audit；
- WAL；
- TxnJournal；
- CommandLog；
- process heartbeat；
- maintenance/admin command；
- Failure Bundle assembly；
- control-plane command/ack；
- Native completion；
- Timer expiry command。

公共架构要求所有跨线程来源进入有界队列，并定义满载行为。

**Foundation 落地时为什么会爆**

多个 Reactor 是否可能向同一个 Session 队列生产，取决于连接 affinity。如果没有明确规定：

- 所谓 SPSC 可能实际是 MPSC；
- unsafe ring buffer 选择可能错误；
- 连接迁移 Reactor 后出现双生产者；
- close/bind/reconnect 与消息入队顺序无法保证。

**具体怎么改**

在 `modules/README.md` 增加统一 Queue Contract Matrix，至少包含：

- owner；
- producer 数量和线程；
- consumer 数量和线程；
- ordering key；
- capacity；
- byte/message 双预算；
- full action；
- blocking 是否允许；
- cancellation；
- shutdown flush；
- Metrics；
- 对应 decision gate。

Ingress 必须二选一并设门：

1. 固定 connection-to-reactor affinity，保证一生一 Reactor；
2. 明确使用 MPSC，并定义 per-session ordering。

不能继续直接写“SPSC 边界清晰”。

---

### P1-03：`host-profiles` 同时负责声明、装配和测试 Host，已经成为反向依赖源

**严重度：P1**

**证据**

`host-profiles` 同时拥有：

- HostCapability；
- Preset 到模块装配差异；
- Fault Decorator；
- LocalEmbedded 保真；
- Headless Test Host 组装矩阵。

而所有业务模块又依赖它查询能力。

**Foundation 落地时为什么会爆**

一个既被所有模块依赖、又知道所有模块如何组装的 crate，会产生：

```text
network -> host-profiles
host-profiles -> network constructor/type
```

最终只能通过 feature、动态类型注册或把模块类型搬进 host-profiles 解决，形成依赖倒置失败。

**具体怎么改**

- `host-profiles` 只保留：
  - immutable capability/profile snapshot；
  - required/provided capability matching；
  - fault-profile 数据；
  - preset 解析结果。
- 模块装配矩阵移动到 `process` Composition Root。
- 测试 Host 组装移动到 test-support/测试入口，不是运行时模块责任。
- LocalEmbedded 保真由 `process + transport + auth + protocol-dispatch` 的装配约束实现，而不是由 host-profiles 宣称“类型上阻止”。

---

### P1-04：Failure Bundle 只有“谁来组装”，没有一致性快照和素材提供协议

**严重度：P1**

**证据**

`observability` 声称自己接收来自所有故障路径的装配请求并产出 Bundle；`process`、`coreclr-host`、`world-slot`、`persistence-host` 又分别提供 crash marker、ALC 证据、Slot 历史、Staging 引用。

但没有定义：

- 装配触发的唯一 owner；
- 每个 provider 的 snapshot 接口；
- 已销毁模块如何提供素材；
- provider 超时；
- 部分 Bundle；
- Bundle 与 Snapshot/WAL 的 hash 关联；
- 崩溃上下文下哪些操作是 async-signal-safe。

**Foundation 落地时为什么会爆**

故障发生后，各模块可能已经处于半析构状态；observability 若回调它们收集素材，会直接产生上层回调、锁死或二次崩溃。

**具体怎么改**

- `observability` 唯一拥有 Bundle assembler。
- 各模块持续发布 immutable evidence reference/snapshot，不在故障时临时反向回调。
- `process` 只触发装配请求。
- 定义 provider 缺失、超时、部分完成和 hash 校验语义。
- 区分：
  - crash-safe 最小证据；
  - 下次启动补全；
  - 正常运行期完整 Bundle。

---

### P1-05：配置切换使用任意 callback，可能把 `process` 代码带入 Simulation Owner Thread

**严重度：P1**

**证据**

`pacing` 暴露：

```text
on_tick_boundary(callback)
```

并让 `process` 发起配置切换。

公共架构要求跨线程操作经过版本化契约，权威状态只在 owner thread/Tick Barrier 提交。

**Foundation 落地时为什么会爆**

任意 callback 允许调用者：

- 捕获 process/global mutable state；
- 在 Owner Thread 上执行 IO；
- 获取阻塞锁；
- 直接调用模块内部对象；
- 绕过 typed command、版本和超时语义。

**具体怎么改**

- 删除公开 `on_tick_boundary(callback)`。
- 改成固定类型的 `ConfigSnapshotActivationRequest`。
- WorldSlot/Runtime 在合法 barrier 应用并返回 ack。
- `process` 只负责装载并验证 generated config artifact，不实现公共 config compiler 语义。
- 配置格式、合并规则、typed reader 仍归架构源/Runtime contract。

---

### P1-06：公共 ADR 状态与“Implementation Baseline”口径冲突

**严重度：P1，外部治理阻塞**

**证据**

多个 ADR 文件仍标注：

> Status: Draft for Architecture Gate

包括 ADR-001、002、006、011。

但 Server README 已把其中多项裁决作为已冻结实现基线使用。

**Foundation 落地时为什么会爆**

当 ADR 仍为 Draft：

- 无法判断哪些条款已经批准；
- Server 私有 README 可能比公共 ADR 更“确定”；
- 后续 ADR 审核修改会导致 Server 已冻结边界失效；
- 决策门与规范条款无法区分。

**具体怎么改**

在 `LumioGameEngineArchitecture`：

- 将已随 `LGE-V1.0-2026-08-27` 批准的 ADR 状态统一改为 Accepted/Implementation Baseline；
- 未批准的内容移入 `DECISIONS_PENDING.md`；
- 每个 ADR 标明批准日期和 BaselineId；
- 新基线前，Server 不得把 Draft 条款写成永久稳定接口。

---

### P1-07：V1 “保留多 Slot 接口”没有收益，反而扩大共享故障域和并发面

**严重度：P1**

**证据**

`world-slot` 写明：

- V1 生产固定单 active Slot；
- 同时保留多 Slot 接口；
- 多 Slot 共享同一进程故障域。

**Foundation 落地时为什么会爆**

“接口保留”会迫使 Foundation 提前处理：

- 多 Owner Thread；
- per-slot Reactor 分流；
- CoreCLR/ALC 共享；
- 全局内存配额；
- Audit/WAL 多租户隔离；
- Slot 间公平性；
- 单进程崩溃的多 Session 恢复。

这正是公共架构列为 P2 的内容，不能以“接口保留”为由提前进入 P0 设计。

**具体怎么改**

- V1 API 明确 cardinality 为 1。
- 内部 ID 和集合结构可以不阻止未来扩展，但不得承诺多 active Slot 行为。
- 删除多 Slot capability、调度和测试要求。
- 未来激活多 Slot 时走单独 ADR，并补共享 CoreCLR、资源治理和故障隔离设计。

---

## P2

### P2-01：根 README、模块地图和模块正文责任摘要没有做到逐词一致

**严重度：P2**

例如根 README 的 `session` 摘要是“Admission、Connection、重连和 Session 路由”，模块地图则增加 Release 固定与 ReplicationContext，正文又明确“不拥有 Connection”。

**具体怎么改**

根 README 只保留一行精确摘要，建议统一为：

> Server-side admission、release pinning、reconnect metadata、connection/slot association；不拥有 transport connection、SimulationSession 或 replication semantics。

---

### P2-02：术语格式仍混用，容易污染后续 API 命名

**严重度：P2**

存在：

- `Command Log` / `CommandLog`
- `Txn Journal` / `TxnJournal`
- `WorldSlot` / `WorldSlotHost`
- `Session Fault` / `SessionFault`
- `Connection/Replication Context` / `ReplicationContext`

这些目前主要是文档问题，但后续会进入 crate、metrics、error code 和 generated artifacts。

**具体怎么改**

在 `modules/README.md` 增加唯一术语表，并规定：

- 公共 Schema/枚举/类型沿用原始拼写；
- 内部 Rust 标识符才按 snake_case；
- 叙述文本不能自行插空格或改写公共类型名。

---

# 4. 模块职责对打表

## 4.1 十二模块拆分裁决

| 模块 | 裁决 | 核心原因 |
|---|---|---|
| `process` | **保留但收缩** | Composition Root 合理，但不能拥有 Slot/Session/Pacing 的业务编排状态；不应被放在“基础层公共依赖”中。 |
| `host-profiles` | **保留但大幅收缩** | Capability/Profile 是独立只读状态；模块装配和测试 Host 组装必须移出。 |
| `observability` | **保留** | Diagnostic/Audit/Metrics/Trace/Failure Bundle 聚合合理；但公共 Schema 和 durable ack 必须修正。 |
| `network` | **改名并收缩为 `transport`** | Reactor、framing、reliability、bounded queues 合理；权限语义和 RPC dispatch 不应继续塞入。 |
| `auth` | **保留独立模块** | 防重放、票据、信任锚和权限裁决有独立安全状态与故障域。 |
| `pacing` | **保留** | Simulation Tick 调度是独立责任；不得承担通用 Timer，也不得拥有 Host aggregate transition。 |
| `coreclr-host` | **保留** | CoreCLR 是进程级资源，和 Slot 级状态分开是必要的；但不得裁决 Session Fault。 |
| `persistence-host` | **保留** | Snapshot/WAL/TxnJournal/CommandLog 是恢复输入，与 Audit 分开合理。 |
| `session` | **保留但重命名内部状态** | Admission 编排、重连 metadata、Release 固定需要独立状态；不得写 network registry，也不得映射 ClientReplicaSession。 |
| `release-router` | **收缩并改为 `release-agent`** | 本地 Manifest/Catalog/Serving 状态合理；跨进程 Pool desired state 和最终路由不属于单 DS 进程。 |
| `world-slot` | **保留并升级为 Host 聚合根** | 当前过薄；必须统一拥有 Host admission、pacing control、Slot lifecycle 和 owner-thread transition。 |
| `maintenance` | **收缩并改为 `maintenance-agent`** | 本地 Drain/Kick/Persist 编排合理；跨进程替换和 TargetActivated 属外部控制面。 |

---

## 4.2 关键职责冲突表

| 冲突组 | 当前重叠/真空 | 唯一所有者 | 其他模块只能做什么 |
|---|---|---|---|
| `auth / session / network` | auth 定义权限；session 写 registry；network 读并执行 | auth 拥有权限裁决；transport 拥有 registry | session 只传递不可变 grant 和绑定命令 |
| `session / world-slot` | session 拥有 Admission 开关，world-slot 又拥有 Host 状态机 | world-slot 拥有 Host Admission Gate | session 只执行候选连接接纳/拒绝 |
| `session / Runtime` | session 保存 ReplicationContext 并定义 Handshaking/Active | Runtime 拥有 ReplicationContext 语义 | session 只保存 opaque handle |
| `pacing / world-slot` | pacing 拥有 pause 状态；world-slot 拥有 Quiesce | world-slot 发起 Host transition；pacing 拥有内部 scheduler state | pacing 执行 pause/resume 命令并返回 ack |
| `world-slot / coreclr-host` | Owner Thread 与 Managed Tick 入口相邻 | world-slot 拥有线程；coreclr 拥有 ABI 入口 | world-slot 调用入口；coreclr 不调度线程 |
| `coreclr-host / Runtime` | coreclr 私自分类 Session Fault | Runtime 提供 fault classification；world-slot 执行 Slot 处置 | coreclr 只捕获与映射异常 |
| `observability / persistence-host` | Audit 写入路径冲突；Failure Bundle 保留重叠 | observability 拥有 Audit 和 Bundle；persistence 拥有恢复日志 | 两者共享底层 IO primitive，但不共享队列状态 |
| `maintenance / process / world-slot` | 三者都描述 Draining/Stopping | world-slot 拥有 Host transition；process 拥有进程 exit；maintenance 拥有 command progress | maintenance 请求；process 等待并退出 |
| `release-router / maintenance / control plane` | 本地模块同时拥有 global Pool 状态和替换 | 外部 control plane 拥有 desired state | release-agent 报告 local state；maintenance-agent 执行 local command |
| `process / host-profiles` | process 组装全部模块，host-profiles 又保存装配矩阵 | process 是唯一 Composition Root | host-profiles 只提供 immutable profile result |

---

## 4.3 应冻结的真实状态所有权

| 状态/资源 | 唯一 owner |
|---|---|
| Process lifecycle、module handles、root cancellation | `process` |
| Host capability/profile snapshot | `host-profiles` |
| Task supervisor、generic timers、cancellation tree | 新增 `host-runtime` |
| Endpoint、ConnectionRegistry、reliability buffer、Ingress/Egress | `transport` |
| Credential verifier、replay window、permission grant/revocation | `auth` |
| Server connection-session metadata、reconnect window、slot association | `session` |
| Host Admission Gate、WorldSlotHost lifecycle、Simulation Owner Thread、quota | `world-slot` |
| Wall Clock adapter、Tick scheduling internal state | `pacing`，但由 world-slot 控制生命周期 |
| CoreCLR、Runtime handle、ALC、Root API table | `coreclr-host` |
| Snapshot/WAL/TxnJournal/CommandLog queues and catalog | `persistence-host` |
| Diagnostic/Audit queues、sinks、metrics/trace、Failure Bundle assembly | `observability` |
| Local Release identity、Manifest validation、local Serving health | `release-agent` |
| Local maintenance command progress | `maintenance-agent` |
| Cluster desired state、replacement fencing、process target activation | 外部控制面，通过 `control-plane-adapter` |
| RPC/Message request lifecycle and dispatch | 新增 `protocol-dispatch` |

---

## 4.4 队列所有权矩阵

| 队列 | Owner | Producer | Consumer | 满载决策 |
|---|---|---|---|---|
| Transport Ingress | `transport` | Reactor shard | protocol-dispatch / Slot owner | Unreliable 可丢；Reliable 按门断开；必须确认 SPSC/MPSC |
| Transport Egress | `transport` | Simulation Owner Thread | sender thread | 降速、预算耗尽后断开 |
| Simulation Command Ingress | `world-slot` | protocol-dispatch | Simulation Owner Thread | 不得阻塞 Reactor；按命令类别拒绝/断开 |
| Diagnostic | `observability` | 全模块 | sink workers | 可采样丢弃并计数 |
| Audit | `observability` | 全模块 | durable audit worker | 不丢；关闭 Admission/进入维护 |
| WAL | `persistence-host` | Runtime/owner-thread adapter | persistence workers | 拒绝新权威命令或维护 |
| TxnJournal | `persistence-host` | Runtime Coordinator adapter | persistence workers | 不丢；事务不得继续提交 |
| CommandLog | `persistence-host` | Runtime adapter | persistence workers | 不丢；停止接纳新命令 |
| Process heartbeat | `process` | 各 supervised task | process watchdog | 丢失/超时触发明确 fault |
| Timer expiry | `host-runtime` 保存 timer；目标 owner 拥有命令队列 | TimerService | 对应 owner | 不允许直接 callback；队列满升级 fault |
| Maintenance/Admin command | `control-plane-adapter` | 外部签名控制面 | maintenance-agent | 有界、幂等、可重放、拒绝重复 |
| Failure Bundle request | `observability` | process/world-slot fault path | assembler | 允许 partial，但必须记录缺失 provider |
| Native Completion | Runtime/CoreEngine 契约 | Native workers | Simulation Owner Thread | 只能在合法 barrier 应用 |

**明确禁止：**

- 全局无界 EventBus；
- 所有模块都能发布任意字符串事件；
- Timer 直接回调业务对象；
- `tools` 模块持有共享可变状态；
- 用 observability event 代替控制命令或 durable journal。

ADR-011 已明确拒绝把不同 durability 语义塞入一个统一无界事件总线。

---

# 5. 依赖图审查

## 5.1 文档真实隐含的运行时图

```text
OS / Container Supervisor
    -> process
        -> 所有模块初始化与关闭

External Operations Channel
    -> maintenance
        -> release-router
        -> session
        -> persistence-host
        -> pacing              [主图漏画]
        -> world-slot          [主图漏画]
        -> network             [广播/断开]
        -> observability

network
    -> session                 [握手/连接事件，主图漏画]
session
    -> auth
    -> release-router
    -> world-slot
    -> network                 [绑定/解绑/踢出]
    -> observability

world-slot
    -> pacing
    -> coreclr-host
    -> persistence-host
    -> network                 [drain_ingress/enqueue_egress，主图漏画]
    -> observability

persistence-host
    -> observability
    -> maintenance             [磁盘满/持久队列饱和反馈，主图漏画]

auth
    -> host-profiles
    -> observability
    -> network                 [重放风暴与限流联动，未定义 port]

observability
    -> session/maintenance     [backpressure 状态反馈]

host-profiles
    -> 模块装配矩阵            [与全员依赖它的方向冲突]
```

## 5.2 当前存在的隐式反馈环

### `network ↔ session`

- network 产生连接事件；
- session 再修改 network 的连接状态。

正确处理方式不是假装没有环，而是：

- 编译依赖单向；
- `ConnectionEvent` 与 `ConnectionCommand` 分别走有界 port；
- registry 仍只由 transport 写。

### `maintenance ↔ persistence-host`

- maintenance 请求落盘；
- persistence 磁盘满又触发维护。

必须拆成：

- command：`PersistForMaintenance`
- event：`DurabilityUnavailable`
- 最终升级裁决由 WorldSlotHost/Process fault policy 执行。

### `process ↔ maintenance`

- process 收到信号后复用 maintenance；
- maintenance 又要求旧实例关闭、目标实例启动。

必须把目标实例启动移到外部 control plane；process 只能请求本地 Graceful shutdown。

### `host-profiles ↔ 所有模块`

只读 profile 可以被全员依赖；“模块装配矩阵”不能留在 host-profiles，否则它会反向知道所有模块类型。

## 5.3 `process` 上帝模块风险

`process` 当前被称为唯一知道全部模块的组装根，这本身正确；但同时它还拥有：

- 配置编译；
- Tick 边界切换；
- Watchdog；
- Crash 处置；
- 恢复；
- Graceful shutdown；
- Failure Bundle 触发；
- 所有模块初始化与析构。

必须冻结一条红线：

> `process` 可以知道所有构造函数和顶层 ports，但不得知道模块内部状态机，也不得替代 `WorldSlotHost` 执行逐步业务编排。

---

# 6. 决策门审查

## 6.1 公共门

| 门 | 裁决 | 说明 |
|---|---|---|
| D-001 一进程一 Release | **部分遵守** | 当前默认一致，但多处写成“临时默认值即本设计”，把 provisional 变成永久约束。 |
| D-002 Drain 深度 | **遵守** | 明确只做 service-level drain，不承诺在线跨 Release 迁移。 |
| D-003 维护默认模式 | **遵守** | Graceful/Forced 政策未写死为 wire 变化。 |
| D-004 Transport/Codec/压缩 | **遵守** | 未私自选定具体 Reactor/TLS/Codec 供应商。 |
| D-005 WAL durability/group commit | **部分遵守** | 承认 group-commit/sync 待测量；但需明确 Snapshot 原子 fsync 与 WAL acknowledgement 是两类契约。 |
| D-006 HybridCLR | **遵守** | Server HybridCLR 未写成 V1 前置。 |
| D-007 N/N-1 | **遵守** | `DeclaredNMinusOne` 只作为 Schema 预留，当前仍 ExactRelease。 |
| D-008 外部日志 Sink | **遵守** | 文件+控制台作为临时 Adapter，没有冻结供应商。 |

公共门确实声明“尚未冻结”，只能按临时默认推进。

## 6.2 Server 内部门

| 门 | 裁决 | 问题 |
|---|---|---|
| SRV-D-001 Ingress | **待测量** | 256 条/256 KiB 可以作为 provisional，但 SPSC/MPSC 拓扑尚未决定。 |
| SRV-D-002 Egress | **待测量** | 需要补 sender 数量、可靠消息优先级与断开前 drain 语义。 |
| SRV-D-003 Slot Watchdog | **违规复用** | `process` 把进程 Watchdog 阈值也挂到该门；Slot 与 Process Watchdog 必须分门。 |
| SRV-D-004 重连窗口 | **不完整** | 有 120 秒值，但没有 Timer owner、资源预算和过期竞态契约。 |
| SRV-D-005 认证票据 | **违规** | 一边承认没有公共 Schema，一边写“由 Release 签名密钥体系派生的签名票据 + 30 秒 nonce”。这已是具体安全设计。 |
| SRV-D-006 限流 | **待测量** | 参数 provisional；需要与 auth 重放风暴联动的 typed signal。 |
| SRV-D-007 健康检查 | **不完整** | 独立低频线程已被写成设计，但公共 Host executor/timer 模型尚未决定。 |
| SRV-D-008 Diagnostic 队列 | **基本遵守** | 仍需明确 per-producer 8192 是否会造成总内存无上限。 |
| SRV-D-009 Checkpoint | **不完整** | “5 分钟或 6000 Tick 先到”横跨 Wall Clock 和 Logical Tick，缺 Timer/owner 定义。 |
| SRV-D-010 Graceful deadline | **P0 违规** | 15 分钟与公共 `deadlineTick` 冲突。 |

## 6.3 缺失的决策门

建议新增，编号最终由仓库治理流程确认：

| 建议门 | 必须冻结的问题 |
|---|---|
| Host executor model | Reactor、IO、Sink、control、timer 使用独立线程还是共享 executor；panic 如何监督 |
| Generic Timer model | 单调时钟、取消、test clock、deadline escalation、shutdown |
| Connection affinity | 一个连接是否终生固定在一个 Reactor，Ingress 是 SPSC 还是 MPSC |
| Permission binding/revocation | grant 生命周期、connection epoch、撤销和重连竞态 |
| Durable queue capacities | Audit/WAL/TxnJournal/CommandLog 容量与饱和升级 |
| Internal command/event ports | typed ports、ordering、ack、timeout、queue-full |
| RPC/Message dispatch | Handler registry、deadline、cancel、idempotency、fault boundary |
| Control-plane protocol | desired state owner、fencing、命令签名、重放、旧实例退出后的替换 |
| Fault classification | Exception/Panic 到 Session/Slot/Process Fault 的合法映射 |
| Failure Bundle provider | evidence snapshot、partial bundle、crash-safe path |
| Process Watchdog | 与 Slot Watchdog 独立的心跳、阈值和重启政策 |

---

# 7. 公共契约漂移清单

| 契约 | 审查结果 | 漂移/缺陷 |
|---|---|---|
| Replication Envelope 字段 | **漂移** | 根 README 使用 PascalCase；Schema 是 camelCase。 |
| `messageType` 枚举 | **通过** | 未发现本仓私自增加枚举值。公共值保持 Handshake/FullSnapshot/BaselineAck/Delta/DeltaAck/ResyncRequest/MaintenanceKick/Error。 |
| `resyncReason` | **通过** | 未发现模块私自扩展，但后续 protocol-dispatch 不得新增本地枚举。 |
| ReleaseCatalog 九态 | **通过** | release-router 使用 Published/Verified/Warmup/Serving/Draining/Empty/Retired/Rollback/Faulted，与 Schema 一致。 |
| `DeclaredNMinusOne` | **通过** | 明确仅为预留，V1 未启用。 |
| MaintenanceCommand | **漂移** | 本仓校验清单漏 `issuedAt`；`broadcastCode` 责任未写完整；deadline 时钟域冲突。 |
| SnapshotHeader | **通过** | `magic=LUMIOSNP1`、`activationState=Staged/Active/Invalid` 使用正确。 |
| Logging Event | **公共基线缺陷 + 本仓依赖漂移** | durability 非 required；correlation 强制 Session/World/Tick，无法表达启动期、发布期、认证前事件。 |
| WorldSlotHost 状态机 | **所有权漂移** | world-slot 声称拥有，但 Admission/Pacing/Draining 被其他模块各自拥有。 |
| SimulationSession | **边界基本声明正确** | session/world-slot 都写了“不拥有”，但销毁和 fault 编排仍需明确由 Runtime entry 执行。 |
| ClientReplicaSession | **漂移** | Server session 私有状态机映射并引用 Client 状态机所有权。 |
| NativeManagedAbi | **命名漂移** | JSON Schema camelCase 与 C ABI snake_case 被称作同一套字段。 |
| HostCapability | **契约不足** | Schema只有 preset/roomMode/roles/capabilities/platformProfile/faultProfile；host-profiles 却承诺 `transport_profile()` 和 ClockProfile 类型级约束。 |
| Fault/Error | **漂移** | Gameplay Exception → Session Fault 不在 ADR-006 中。 |
| RPC Envelope/MessageId | **来源缺失** | 根 README 声称消费，但审查范围中的公共 Schema/ADR 和 Server 模块均未给出 owner。 |

---

# 8. 推荐的唯一修正模块地图

```text
modules/
├── README.md
│
├── process/                   # 仅 Composition Root、信号、进程生命周期、退出
├── host-runtime/              # 新增：Clock/Timer/Task Supervisor/Cancellation/Bounded Executor
├── host-profiles/             # 收缩：只读 Capability/Profile/Fault 配置
├── observability/             # Log/Metrics/Trace/Audit/Failure Bundle
│
├── transport/                 # network 重命名：Reactor/Codec/Envelope/Reliability/Queues
├── auth/                      # Credential/Replay/Permission Grant
├── protocol-dispatch/         # 新增：Message/RPC dispatch、correlation、deadline、cancel、idempotency
│
├── session/                   # ServerConnectionSession、Release pinning、reconnect、association
├── pacing/                    # 只处理 Simulation Tick scheduling
├── coreclr-host/              # CoreCLR/Runtime/ALC/ABI bridge
├── persistence-host/          # Snapshot/WAL/TxnJournal/CommandLog
├── world-slot/                # WorldSlotHost 聚合根、Owner Thread、Admission Gate、Quota
│
├── release-agent/             # release-router 收缩：本进程 Release/Manifest/Health
├── maintenance-agent/         # maintenance 收缩：本进程 Drain/Kick/Persist/ReadyToExit
└── control-plane-adapter/     # 新增：签名运维命令、desired-state、supervisor/fencing adapter
```

另设一个**无运行时状态的 generated contracts crate**，不算运行模块：

```text
contracts/
└── server-contracts-generated/
```

它只容纳从架构源生成或批准的：

- Commands；
- Events；
- IDs；
- Stable errors；
- Permission grant；
- RPC/Message descriptors；
- Port traits；
- Schema adapters。

## 为什么这是唯一推荐方案

### 日志

**不需要新增 `log` 模块。**

`observability` 已经是正确归属，缺的是：

- 公共 Logging Schema 修正；
- Audit durable acknowledgment；
- Failure Bundle provider 协议；
- queue capacity gate。

### Timer 和多线程

**必须新增 `host-runtime`。**

Timer 不属于 `pacing`：

- `pacing` 是 Simulation Tick 语义；
- `host-runtime` 是进程级单调时钟、任务监督和通用 deadline。

### 消息通知

**不新增全局 notification/event-bus 模块。**

采用 typed、owner-targeted、bounded ports：

```text
Command: 某 owner 被要求做一件事
Event:   某 owner 报告已经发生的事实
Query:   只读、同步、不得回调
Ack:     对 durable/transition 操作给出明确完成结果
```

每个事件必须有：

- 唯一 producer owner；
- 明确 consumer；
- correlation；
- ordering；
- capacity；
- full action；
- cancellation；
- version。

### RPC

**新增 `protocol-dispatch`，但必须被公共 RPC/Message Contract 阻塞。**

它不能自己发明 RPC Envelope、MessageId 或错误码。

### 通用 tools

**禁止建立万能 `tools` crate。**

只有满足以下条件的能力才可以进入 `host-runtime` 或无状态 support library：

- 领域无关；
- 不拥有 Gameplay/Session/World 状态；
- 无反向 callback；
- 有资源上限；
- 有取消和 shutdown；
- 不引入第三方类型到稳定契约。

---

# 9. 放行前必须改的最小集合

## 9.1 先改公共架构源

### `docs/adr/ADR-001-session-lifecycle.md`

- 明确 `WorldSlotHost` 是 Host 聚合根。
- 明确 Admission、Pacing、Quiesce、Snapshot、Stop 的发起权。
- 明确内部子组件可以持有局部状态，但 aggregate transition 只能由 world-slot 发起。

### `docs/adr/ADR-006-native-managed-abi.md`

- 增加 Fault Classification 边界。
- 删除“捕获异常即可降级”的可能解释。
- 明确 Runtime 需要提供未提交/回滚/污染状态证明。

### `docs/adr/ADR-011-observability.md`

- 明确 Audit 与 TxnJournal/CommandLog 的不同 owner。
- 明确 durable ack。
- 明确 Failure Bundle provider/snapshot 模型。

### `docs/adr/ADR-012-release-update-maintenance.md`

- 明确外部 control plane 与本地 DS agent 的角色。
- 明确谁拥有 Pool desired state。
- 明确 replacement fencing。
- 修正 maintenance deadline 时钟域。

### 公共 Schema

必须更新：

- `logging-event.schema.json`
- `common.schema.json`
- `maintenance-command.schema.json`
- 必要时 `host-capability.schema.json`

必须新增或冻结：

- Permission grant / connection binding contract；
- RPC/Message contract；
- fault classification contract；
- control-plane command/status contract。

必须同步新增正反 fixtures，并发布新 BaselineId。不得直接修改 LumioServer 的只读镜像或 `.baseline.sha256`。

---

## 9.2 再改 LumioServer 本仓

### `README.md`

- Envelope 字段统一 camelCase。
- 删除或阻塞尚无公共来源的 `RPC Envelope/MessageId` 既定依赖。
- 修正 session 摘要。
- 删除任何“可捕获 Gameplay Exception 必然是 Session Fault”的表述。

### `modules/README.md`

- 重画模块地图。
- 分离 compile DAG、runtime commands、runtime events。
- 增加真实状态所有权表。
- 增加完整 Queue Contract Matrix。
- 删除 PascalCase“叙述惯例”豁免。
- 修正 Audit/TxnJournal/CommandLog owner。
- 增加缺失决策门。
- 删除隐藏边和“经编排间接触达”表述。

### `modules/process/README.md`

- 收缩为 Composition Root。
- 删除对 WorldSlotHost 内部阶段的直接编排。
- 进程 Watchdog 使用独立决策门。
- 配置只调用 generated compiler/validator，不重新定义编译语义。

### `modules/host-profiles/README.md`

- 移除模块装配矩阵 owner。
- 移除测试 Host 组装 owner。
- 只保留 immutable capability/profile。
- 不得宣称 Schema 中不存在的字段已提供类型级保证。

### `modules/observability/README.md`

- 明确只拥有 Diagnostic/Audit/Metrics/Trace/Failure Bundle。
- 删除 TxnJournal/CommandLog owner 暗示。
- 增加 durable ack 和 Bundle provider 协议。
- 在公共 correlation 修复前标记启动期事件为阻塞项。

### `modules/network/README.md`

- 建议重命名为 `transport`。
- 明确 ConnectionRegistry 唯一写 owner。
- session 只能发绑定命令。
- 决定 connection affinity 与 SPSC/MPSC。
- 移除 RPC/Gameplay dispatch。
- 增加 control command ordering。

### `modules/auth/README.md`

- 保留独立模块。
- 删除“由 Release 签名密钥体系派生票据”这一未批准具体设计。
- 在公共 Schema 前不得实现私有 wire ticket。
- 明确 grant、expiry、revocation、reconnect 语义 owner。

### `modules/pacing/README.md`

- 删除任意 `on_tick_boundary(callback)`。
- 明确只服务 Simulation Tick。
- 通用 Timer 移到 `host-runtime`。
- pause/resume 只能接受 world-slot command。

### `modules/coreclr-host/README.md`

- 删除 Gameplay Exception → Session Fault。
- 区分 JSON ABI descriptor camelCase 和 C ABI table snake_case。
- 修正“全部 Managed 调用都发生在 Owner Thread”——启动、装载、卸载控制调用与 Tick 热路径必须区分。

### `modules/persistence-host/README.md`

- 删除 Audit 落盘 owner。
- 为 WAL/TxnJournal/CommandLog 分别补容量与 saturation gate。
- 磁盘压力只能发 typed event，不能直接回调 maintenance。
- 区分 Snapshot 原子 fsync 与 WAL acknowledgment policy。

### `modules/session/README.md`

- 删除 ConnectionRegistry 写入。
- 私有状态机改名为 ServerConnectionSession。
- 删除 ClientReplicaSession 映射。
- 删除 Host Admission Gate owner。
- 重连 timer 改用 host-runtime。
- ReplicationContext 只保存 opaque handle。

### `modules/release-router/README.md`

- 收缩并重命名为 `release-agent`。
- 删除跨进程最终路由和全局 Pool desired-state owner。
- 健康检查使用 host-runtime Timer，不自建低频线程。
- D-001 必须始终标注 provisional，不得写“临时默认值即本设计”。

### `modules/world-slot/README.md`

- 升级为 WorldSlotHost 聚合根。
- 统一 Host Admission、Quiesce、Pacing control、Snapshot、Stop。
- 明确所有 transition command 的幂等和 epoch。
- V1 去掉多 active Slot 行为承诺。

### `modules/maintenance/README.md`

- 收缩并重命名为 `maintenance-agent`。
- 删除 `TargetActivated`。
- 修复 MaintenanceCommand 字段清单。
- deadline 时钟域在新公共基线前标为 blocked。
- Persistence 与 Audit 使用两个独立 ack。
- 不直接依赖 world-slot 内部状态，只发送合法 Host transition command。

### 新增三个模块 README

- `modules/host-runtime/README.md`
- `modules/protocol-dispatch/README.md`
- `modules/control-plane-adapter/README.md`

---

## 文档工程检查结论

以下项目本轮通过，不是退回原因：

- 12 个模块 README 形式上都有完整分节骨架。
- 未发现 `TODO/TBD/后续补充/占位`。
- 根 README 没有复制整份 `modules/README.md`。
- commit `c5350a5` 没有修改受保护的架构镜像。
- SnapshotHeader、ReleaseCatalog 九态、D-007 ExactRelease 等部分公共契约对齐正确。

但这些形式通过不能抵消上述 P0：**在 WorldSlotHost 聚合所有权、真实依赖图、权限绑定、Maintenance 时钟、控制面、Logging Schema、故障分类和 RPC owner 全部修正之前，不得把这批文档作为 Foundation 的 crate 边界。**
