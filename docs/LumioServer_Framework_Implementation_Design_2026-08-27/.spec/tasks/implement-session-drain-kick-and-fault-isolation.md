---
status: pending
---
# 实现 Session Drain/Kick 与故障隔离

## 涉及范围

- **Wave：** 10
- **归属：** `session`
- **唯一目标：** 消费maintenance/world-slot命令，停止新接纳、drain/close连接并保证单Session故障不污染其他Session。
- **文件集：
  - `modules/session/src/drain.rs`
  - `modules/session/src/fault.rs`
  - `modules/session/tests/drain_kick_test.rs`
  - `modules/session/tests/fault_isolation_test.rs`

## 验收标准

- [ ] BeginDrain只影响session admission执行，不修改world-slot gate owner。
- [ ] 每Session drain/kick有显式terminal event；重复命令幂等。
- [ ] 单连接queue/auth/reconnect故障只关闭对应ServerConnectionSession。
- [ ] Slot fault event按association传播，旧slot epoch关联拒绝。
- [ ] drain完成条件可被maintenance精确查询，不依赖日志。

## 依赖

- [`implement-session-reconnect-window-and-epoch-races`](./implement-session-reconnect-window-and-epoch-races.md)
- [`implement-world-slot-resource-and-watchdog-soak`](./implement-world-slot-resource-and-watchdog-soak.md)

## 接口

Consumes:
- Maintenance command、WorldSlot fault event、Transport close ack

Produces:
- Session Drained/Kicked/Faulted terminal events
