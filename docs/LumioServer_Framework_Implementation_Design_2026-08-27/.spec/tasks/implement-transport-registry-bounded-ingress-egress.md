---
status: pending
---
# 实现 ConnectionRegistry 与每连接有界队列

## 涉及范围

- **Wave：** 5
- **归属：** `transport`
- **唯一目标：** 建立transport单写registry、connection epoch、Ingress/Egress/Command queues、可靠/分片/限流状态。
- **文件集：
  - `modules/transport/src/registry.rs`
  - `modules/transport/src/rate_limit.rs`
  - `modules/transport/src/reliability.rs`
  - `modules/transport/src/fragment.rs`
  - `modules/transport/src/ingress.rs`
  - `modules/transport/src/egress.rs`
  - `modules/transport/src/runner.rs`
  - `modules/transport/tests/registry_owner_test.rs`
  - `modules/transport/tests/bounded_backpressure_test.rs`

## 验收标准

- [ ] 只有registry runner处理ConnectionCommand可写记录；无外借mutable reference。
- [ ] 三类队列items+bytes均有上限、FIFO/满载/close语义与metrics。
- [ ] stale connection epoch命令/完成全拒绝。
- [ ] bounded reassembly总内存受限；governor参数来自配置。
- [ ] Simulation side API仅try drain/enqueue，不阻塞I/O。

## 依赖

- [`implement-transport-vendor-neutral-envelope-core`](./implement-transport-vendor-neutral-envelope-core.md)
- [`implement-host-runtime-supervision-cancellation-and-join`](./implement-host-runtime-supervision-cancellation-and-join.md)

## 接口

Consumes:
- Transport commands、PortSpec

Produces:
- `ConnectionRegistry`、IngressReader、EgressWriter、runner
