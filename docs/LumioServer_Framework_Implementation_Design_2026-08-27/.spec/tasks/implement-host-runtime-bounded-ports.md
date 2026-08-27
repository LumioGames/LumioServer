---
status: pending
---
# 实现有界 MPSC/SPSC 端口原语

## 涉及范围

- **Wave：** 2
- **归属：** `host-runtime`
- **唯一目标：** 以 crossbeam-channel/rtrb 封装 supplier-neutral 点到点端口，并强制 owner/producer/consumer/capacity/full/close 元数据。
- **文件集：
  - `modules/host-runtime/Cargo.toml`
  - `modules/host-runtime/src/lib.rs`
  - `modules/host-runtime/src/port.rs`
  - `modules/host-runtime/src/spsc.rs`
  - `modules/host-runtime/src/error.rs`
  - `modules/host-runtime/tests/port_contract_test.rs`
  - `modules/host-runtime/tests/loom_port_close_test.rs`

## 验收标准

- [ ] 稳定 API 不出现 crossbeam/rtrb 类型；无 unbounded 构造函数。
- [ ] MPSC FIFO、SPSC唯一 owner、try_send/try_push满载、close/drain语义均有测试。
- [ ] 每个端口实例必须携带 `PortSpec` 六项矩阵字段并导出 depth/reject metrics。
- [ ] Loom/mutation测试证明 send-vs-close 不死锁、不双重消费。
- [ ] `cargo nextest run -p lumio-host-runtime port` 与 policy check通过。

## 依赖

- [`consume-upstream-generated-contract-artifacts`](./consume-upstream-generated-contract-artifacts.md)
- [`add-lumio-host-testkit`](./add-lumio-host-testkit.md)

## 接口

Consumes:
- Queue Contract Matrix行

Produces:
- `BoundedSender/Receiver<T>`、`SpscProducer/Consumer<T>`、`PortSpec`
