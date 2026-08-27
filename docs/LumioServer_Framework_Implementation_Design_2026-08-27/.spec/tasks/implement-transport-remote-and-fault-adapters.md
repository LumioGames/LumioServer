---
status: pending
---
# 实现 Remote Carrier 候选与确定性 Fault Decorator

## 涉及范围

- **Wave：** 7
- **归属：** `transport`
- **唯一目标：** 在D-004满足时以Quinn/rustls实现RemoteDS carrier，并提供bounded确定性故障decorator；两者均不改变稳定API。
- **文件集：
  - `modules/transport/src/adapters/remote.rs`
  - `modules/transport/src/adapters/fault_decorator.rs`
  - `modules/transport/tests/remote_adapter_contract_test.rs`
  - `modules/transport/tests/fault_decorator_test.rs`

## 验收标准

- [ ] 任务执行前 `contracts/architecture-contracts.lock.toml` 记录D-004已满足的Baseline/decision；否则测试只验证production composition拒绝启用。
- [ ] Quinn/rustls类型仅在adapter文件；证书/key不进日志。
- [ ] reactor/send workers由host-runtime监督，queue full/close映射为仓内事件。
- [ ] fault plan固定seed时丢包/延迟/重复/重排可重放且内存有界。
- [ ] Remote与Local adapter通过同一carrier contract suite。

## 依赖

- [`implement-transport-local-embedded-fidelity-adapter`](./implement-transport-local-embedded-fidelity-adapter.md)
- [`implement-host-runtime-supervision-cancellation-and-join`](./implement-host-runtime-supervision-cancellation-and-join.md)
- [`implement-host-profile-fault-decorator-declarations`](./implement-host-profile-fault-decorator-declarations.md)
- 外部依赖：D-004已由权威架构源冻结后才能启用生产Remote adapter。

## 接口

Consumes:
- D-004 gate evidence、ByteCarrier SPI

Produces:
- RemoteDS carrier adapter、FaultDecoratedCarrier
