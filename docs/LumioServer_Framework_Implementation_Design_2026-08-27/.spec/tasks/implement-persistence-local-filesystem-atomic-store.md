---
status: pending
---
# 实现本地文件原子存储 Adapter

## 涉及范围

- **Wave：** 3
- **归属：** `persistence-host`
- **唯一目标：** 组合tempfile/rustix/fs4实现storage root锁、同目录staging、write/fsync/replace/dir fsync和crash points。
- **文件集：
  - `modules/persistence-host/Cargo.toml`
  - `modules/persistence-host/src/lib.rs`
  - `modules/persistence-host/src/config.rs`
  - `modules/persistence-host/src/storage/mod.rs`
  - `modules/persistence-host/src/storage/local_fs.rs`
  - `modules/persistence-host/src/storage/fault_injected.rs`
  - `modules/persistence-host/src/error.rs`
  - `modules/persistence-host/tests/atomic_snapshot_test.rs`

## 验收标准

- [ ] 单进程独占root lock；竞争启动明确失败。
- [ ] atomic replace顺序可注入每个crash point，恢复后只见旧或新完整文件。
- [ ] 文件/目录fsync证据独立记录；不把persist返回当durability ack。
- [ ] fd/path/vendor类型不泄漏公开API；路径逃逸/符号链接策略有测试。
- [ ] 不自研lock/rename协议。

## 依赖

- [`implement-host-runtime-bounded-ports`](./implement-host-runtime-bounded-ports.md)
- [`consume-upstream-generated-contract-artifacts`](./consume-upstream-generated-contract-artifacts.md)

## 接口

Consumes:
- DurableStorage SPI需求、SnapshotHeader

Produces:
- `LocalFsStorage` 与 fault-injected adapter
