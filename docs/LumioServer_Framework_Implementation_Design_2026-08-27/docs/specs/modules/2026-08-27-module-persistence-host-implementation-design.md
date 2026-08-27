# LumioServer `persistence-host` 模块实现级设架

> 架构基线：`LGE-V1.2-2026-08-27`  
> 实现优先级：**P1**  
> crate：`lumio-persistence-host`  
> 本文只定义仓内实现结构；公共 Schema / ABI / ID / 状态机仍由 `LumioGameEngineArchitecture` 维护。

## A. 一句话职责与非职责

**职责：** 拥有服务端本地 Snapshot/WAL/TxnJournal/CommandLog 的有界写入、原子文件存储、checkpoint、恢复与 persistence commit ack；不拥有 Audit durable ack。

**明确不负责：**
- 不拥有 Runtime/Voxel 数据语义、SnapshotHeader/Journal Schema、maintenance state 或 FaultClass。
- 不直接回调 maintenance-agent；磁盘压力和饱和只发 typed event。
- 不把 Snapshot 原子替换与 WAL/group-commit ack 混为同一语义。
- 不默默丢权威记录，不使用 unbounded buffer，不自行 sleep/轮询 checkpoint。

## B. crate、目录与文件清单

建议 package 名：`lumio-persistence-host`。文件按所有权/职责切分，不创建 `common.rs`、`globals.rs`、`event_bus.rs` 或大锅文件。

| 文件 | 唯一职责 |
| --- | --- |
| `modules/persistence-host/Cargo.toml` | tempfile/rustix/fs4/CRC/hash + generated contracts。 |
| `modules/persistence-host/src/lib.rs` | 导出 storage-neutral commands/events/views。 |
| `modules/persistence-host/src/config.rs` | durability/queue/checkpoint private policy。 |
| `modules/persistence-host/src/snapshot.rs` | SnapshotHeader validation、staging/activation。 |
| `modules/persistence-host/src/wal.rs` | WAL writer state与 sequence。 |
| `modules/persistence-host/src/txn_journal.rs` | Txn terminal records与 indeterminate evidence。 |
| `modules/persistence-host/src/command_log.rs` | CommandLog append/recovery。 |
| `modules/persistence-host/src/checkpoint.rs` | typed trigger/progress，不自行定时。 |
| `modules/persistence-host/src/recovery.rs` | scan→validate→select active→replay plan。 |
| `modules/persistence-host/src/commit.rs` | `PersistenceCommitAck` 与 durability evidence。 |
| `modules/persistence-host/src/pressure.rs` | disk/queue pressure typed state。 |
| `modules/persistence-host/src/queues.rs` | 各 durable queue exact spec。 |
| `modules/persistence-host/src/workers.rs` | 命名 writer runners。 |
| `modules/persistence-host/src/migration.rs` | 消费上游 migration manifest/adapter，不定义 DAG。 |
| `modules/persistence-host/src/storage/mod.rs` | `DurableStorage` SPI。 |
| `modules/persistence-host/src/storage/local_fs.rs` | 锁、tempfile、write/fsync/rename/dir fsync。 |
| `modules/persistence-host/src/storage/fault_injected.rs` | 测试故障 adapter。 |
| `modules/persistence-host/src/commands.rs` | append/snapshot/checkpoint/recover/stop。 |
| `modules/persistence-host/src/events.rs` | commit ack/pressure/recovery/snapshot activated。 |
| `modules/persistence-host/src/error.rs` | I/O/corrupt/schema/pressure/durability errors。 |
| `modules/persistence-host/tests/atomic_snapshot_test.rs` | crash points and activation state。 |
| `modules/persistence-host/tests/journal_ack_test.rs` | ack 不早于 policy。 |
| `modules/persistence-host/tests/recovery_fixture_test.rs` | snapshot/header/journal fixtures。 |
| `modules/persistence-host/tests/queue_saturation_test.rs` | 四类队列独立上限。 |

## C. 公开/内部类型、Trait、命令、事件与端口

### C.1 类型、命令与事件
- `PersistenceHost`、`RecoveryPlan`、`RecoveryReport`。
- `SnapshotWriteCommand`、`WalAppendCommand`、`TxnJournalCommand`、`CommandLogAppendCommand`。
- `PersistenceCommitAck { requestId, stream, durableSequence, evidence }`。
- `PersistenceEvent::{CommitAck, SnapshotActivated, RecoveryCompleted, DurabilityUnavailable, PressureChanged}`。
- `DurabilityPolicy` 为私有配置快照，不是公共 Schema 常量。

### C.2 稳定仓内端口
| 接口 | 落点 | 契约 |
| --- | --- | --- |
| `PersistenceCommandPort::try_send(PersistenceCommand)` | `commands.rs` | world-slot/maintenance 唯一入口；每条命令显式 request/sequence。 |
| `PersistenceEventPort::try_recv()` | `events.rs` | commit ack 与 pressure event。 |
| `RecoveryPort::recover(VerifiedReleaseBundle)` | `recovery.rs` | 启动期同步编排，I/O 由 storage adapter；listener 未开放。 |
| `DurableStorage` | `storage/mod.rs` | supplier-neutral read/write/sync/replace/lock；公开模块不暴露 file descriptor。 |

### C.3 Rust 签名草案

以下签名是**仓内实现接口**，不是新增公共 ABI；实现时只能引用上游 generated 类型，不能复制公共字段。

```rust
impl PersistenceCommandPort {
    pub fn try_send(&self, command: PersistenceCommand) -> Result<(), PersistencePortError>;
}

impl RecoveryPort {
    pub fn recover(
        &mut self,
        release: &VerifiedReleaseBundleRef,
    ) -> Result<RecoveryReport, RecoveryError>;
}

pub(crate) trait DurableStorage: Send {
    fn acquire_root_lock(&mut self) -> Result<StorageLock, StorageError>;
    fn write_staged(&mut self, request: StagedWriteRequest) -> Result<StagedObject, StorageError>;
    fn sync_staged(&mut self, staged: &StagedObject) -> Result<SyncEvidence, StorageError>;
    fn replace_active(&mut self, staged: StagedObject, active: &StorageKey) -> Result<ReplaceEvidence, StorageError>;
    fn sync_parent(&mut self, active: &StorageKey) -> Result<DirectorySyncEvidence, StorageError>;
}
```

## D. 状态、资源与生命周期所有权

- Snapshot staging/active file state、WAL/TxnJournal/CommandLog writers 与各自 durability sequence。
- 本地 storage root lock、atomic replace/fsync adapter、recovery scan/result。
- 每条 durable queue 的容量、writer lifecycle、pressure state 和 commit ack。
- checkpoint timer token/progress；公共数据内容仍由生成契约定义。

### D.1 模块红线
- Audit durable ack 不在本模块。
- 磁盘压力只能发 typed event，不直接调用 maintenance-agent。

## E. 线程、队列、背压与关闭

### E.1 线程/执行上下文
- 每类 durable writer 使用 host-runtime 监督的命名 worker，是否合并 worker 由测量决定但队列语义独立。
- Simulation Owner Thread 只 try_send durable commands，不执行 I/O。
- checkpoint 由 TimerFired 或 Logical Tick evidence 命令触发；不混合未定义时钟。
- 恢复发生在 listener/Admission 开放前的 bootstrap 阶段。

### E.2 队列合同
| 队列 | 元素 | 所有者 | 生产者 | 消费者 | 顺序 | 容量配置 | 满载动作 | 关闭语义 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `WalWriteQueue` | `WalAppendCommand` | persistence-host | world-slot/Runtime bridge | WAL worker | sequence FIFO | `persistence.wal.capacity.items/bytes` | 返回 `DurabilityBackpressured`；不得丢 | close 时按 policy flush/ack/abort |
| `TxnJournalQueue` | `TxnJournalCommand` | persistence-host | world-slot/Runtime bridge | Txn worker | txn sequence FIFO | `persistence.txn.capacity.items/bytes` | 同上，Prepared/CommitIntent 后失败升级 | close 前完成终态或明确 Indeterminate evidence |
| `CommandLogQueue` | `CommandLogAppendCommand` | persistence-host | world-slot | CommandLog worker | tick/order FIFO | `persistence.command_log.capacity.items/bytes` | 返回 backpressure；不得覆盖 | close flush/terminal ack |
| `SnapshotQueue` | `SnapshotWriteCommand` | persistence-host | world-slot/maintenance-agent | snapshot worker | request FIFO | `persistence.snapshot.capacity` | 拒绝新 snapshot；活动写继续/明确 abort | staged temp 清理并记录 |
| `PersistenceEventQueue` | `PersistenceEvent` | world-slot/maintenance/process | workers | consumers | per stream sequence | `persistence.event.capacity` | 关键 ack/pressure 不丢；无法投递升级 supervisor | 终态 drain |

这些行必须写入仓库 Queue Contract Registry；数值来自配置快照，SRV-D 默认值不得提升为公共常量。

## F. 失败分类、恢复与显式 Ack

| 分类 | 实现要求 |
| --- | --- |
| 可拒绝 | 坏 SnapshotHeader/hash/length、stale sequence、队列满；不改 active snapshot。 |
| 可重试 | 暂时 I/O/space pressure 在 policy 允许范围；重试必须有界并保留 request id。 |
| Slot/Process | Prepared/CommitIntent 后无法持久化、journal corrupt、active snapshot 不可恢复；发 DurabilityUnavailable/RecoveryFailed 由 owner 裁决。 |
| 边界规则 | PersistenceCommitAck 只证明 persistence；绝不代表 Audit durable ack。 |

## G. 编译依赖、成熟方案与 Adapter 隔离

### G.1 编译依赖
**允许：**
- host-runtime
- observability
- host-profiles
- generated Snapshot/Txn/Migration/Error contracts
- `tempfile`、`rustix`、`fs4`、`crc32fast`/schema-specified hash

**禁止：**
- maintenance-agent/process/world-slot 反向实现
- observability Audit writer
- cloud vendor SDK type in stable API
- unbounded buffer

### G.2 成熟方案选择
| 方案 | 用途 | 成熟性、许可证与隔离 |
| --- | --- | --- |
| `tempfile 3.27` | 同目录临时文件 | 成熟、MIT/Apache-2.0；与显式 fsync/rename 组合。 |
| `rustix 1.1` | fsync/rename/目录同步 | 活跃、Apache-2.0/MIT；系统调用只在 local_fs adapter。 |
| `fs4 1.1` | storage root lock | 成熟、宽松；不自研 lock file protocol。 |
| `crc32fast`/RustCrypto hash | 损坏检测 | 算法必须由上游格式指定；不发明 wire hash。 |

### G.3 明确拒绝的自研项
- 不自研 filesystem、数据库、WAL 通用引擎、云存储 SDK、文件锁或原子替换算法。
- 自有 append/recovery reducer 只覆盖 Lumio generated records 与双阶段语义；若未来选 RocksDB/SQLite，必须封在 `DurableStorage` 后且许可证/确定性重新审查。

## H. 测试面与 Fixture

- Crash matrix：write/temp fsync/rename/dir fsync/ack 每点断电，恢复只选合法 active。
- Golden：SnapshotHeader、cross-world txn、migration fixtures。
- Property：ack sequence 单调且不早于 configured durability；queue memory bounded。
- 故障：ENOSPC、short write、corruption、lock contention、迟到 duplicate append。
- 恢复：最后合法 snapshot + logs 重放计划可重复，坏尾部不静默接受。

## I. 决策门与配置默认

- D-005 决定 WAL durability/group commit；实现支持 policy 注入，默认值不升格为契约。
- SRV-D-009 checkpoint 默认仅配置；触发源必须是明确 TimerCommand 或 Runtime Tick evidence。
- 所有 durable queue 容量均需 benchmark/故障测试后标记 measured。

## J. 本模块任务卡

| 任务 | Wave | 单一目标 | 依赖 |
| --- | --- | --- | --- |
| [`implement-persistence-local-filesystem-atomic-store`](../../../.spec/tasks/implement-persistence-local-filesystem-atomic-store.md) | Wave 3 | 组合tempfile/rustix/fs4实现storage root锁、同目录staging、write/fsync/replace/dir fsync和crash points。 | `implement-host-runtime-bounded-ports`, `consume-upstream-generated-contract-artifacts` |
| [`implement-persistence-durable-streams-queues-and-acks`](../../../.spec/tasks/implement-persistence-durable-streams-queues-and-acks.md) | Wave 5 | 建立Snapshot/WAL/TxnJournal/CommandLog writer状态、bounded queues、sequence和`PersistenceCommitAck`。 | `implement-persistence-local-filesystem-atomic-store`, `implement-host-runtime-supervision-cancellation-and-join` |
| [`implement-persistence-recovery-checkpoint-and-migration-adapter`](../../../.spec/tasks/implement-persistence-recovery-checkpoint-and-migration-adapter.md) | Wave 6 | 从合法active snapshot与durable logs生成可重复RecoveryPlan，并以typed timer/tick evidence触发checkpoint。 | `implement-persistence-durable-streams-queues-and-acks`, `implement-host-runtime-clock-and-timer-delivery` |
| [`implement-persistence-durability-fault-matrix`](../../../.spec/tasks/implement-persistence-durability-fault-matrix.md) | Wave 7 | 覆盖ENOSPC、short write、corruption、lock loss、queue saturation、迟到duplicate和shutdown中断的可验证终态。 | `implement-persistence-recovery-checkpoint-and-migration-adapter` |

## K. 完成定义

本模块的任务卡全部通过；模块单测/fixture/故障测试通过；Queue Registry、Cargo DAG、许可证与源码红线检查通过；没有供应商类型泄漏、无界队列、任意回调或未见证 FaultClass 推断。
