---
status: pending
---
# 实现四类 Durable Stream、独立队列与 Commit Ack

## 涉及范围

- **Wave：** 5
- **归属：** `persistence-host`
- **唯一目标：** 建立Snapshot/WAL/TxnJournal/CommandLog writer状态、bounded queues、sequence和`PersistenceCommitAck`。
- **文件集：
  - `modules/persistence-host/src/snapshot.rs`
  - `modules/persistence-host/src/wal.rs`
  - `modules/persistence-host/src/txn_journal.rs`
  - `modules/persistence-host/src/command_log.rs`
  - `modules/persistence-host/src/commit.rs`
  - `modules/persistence-host/src/pressure.rs`
  - `modules/persistence-host/src/queues.rs`
  - `modules/persistence-host/src/workers.rs`
  - `modules/persistence-host/src/commands.rs`
  - `modules/persistence-host/src/events.rs`
  - `modules/persistence-host/tests/journal_ack_test.rs`
  - `modules/persistence-host/tests/queue_saturation_test.rs`

## 验收标准

- [ ] 四类queue各有items/bytes、owner/order/full/close配置和metrics，互不借容量形成无界。
- [ ] ack只在对应DurabilityPolicy证据成立后发；sequence单调、duplicate幂等。
- [ ] Prepared/CommitIntent后写失败产生明确DurabilityUnavailable/Indeterminate evidence，不静默drop。
- [ ] PersistenceCommitAck类型/API不包含Audit含义。
- [ ] writer均由host-runtime supervisor启动，无模块spawn/sleep。

## 依赖

- [`implement-persistence-local-filesystem-atomic-store`](./implement-persistence-local-filesystem-atomic-store.md)
- [`implement-host-runtime-supervision-cancellation-and-join`](./implement-host-runtime-supervision-cancellation-and-join.md)

## 接口

Consumes:
- DurableStorage、generated records

Produces:
- PersistenceCommand/Event ports、四类writer、CommitAck
