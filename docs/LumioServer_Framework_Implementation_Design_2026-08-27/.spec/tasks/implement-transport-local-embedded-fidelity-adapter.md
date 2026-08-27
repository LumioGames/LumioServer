---
status: pending
---
# 实现 LocalEmbedded Byte Carrier 与保真测试

## 涉及范围

- **Wave：** 6
- **归属：** `transport`
- **唯一目标：** 以内存byte carrier替代OS网络层，但复用同一codec/envelope/permission/size/queue路径。
- **文件集：
  - `modules/transport/src/adapters/local_embedded.rs`
  - `modules/transport/tests/local_embedded_fidelity_test.rs`

## 验收标准

- [ ] local adapter输入先形成bytes，再经生产同一codec/validator；不传对象引用。
- [ ] 缺Schema/permission/size/queue任一层的mutation测试失败。
- [ ] Server/Client side state/queue完全独立，不共享mutable buffer owner。
- [ ] 与reference remote carrier对相同byte序列产出相同ValidatedEnvelopeBytes/拒绝。
- [ ] fault decorator未启用时不改变顺序/内容。

## 依赖

- [`implement-transport-registry-bounded-ingress-egress`](./implement-transport-registry-bounded-ingress-egress.md)
- [`implement-host-profile-resolution-and-capability-matching`](./implement-host-profile-resolution-and-capability-matching.md)

## 接口

Consumes:
- ByteCarrier SPI、LocalEmbedded plan

Produces:
- `LocalEmbeddedCarrier`
