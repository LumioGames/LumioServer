---
status: pending
---
# 实现 Transport Vendor-neutral Envelope Core

## 涉及范围

- **Wave：** 3
- **归属：** `transport`
- **唯一目标：** 定义supplier-neutral连接值、generated Envelope gate、codec/carrier SPI、permission reference和无业务dispatch边界。
- **文件集：
  - `modules/transport/Cargo.toml`
  - `modules/transport/src/lib.rs`
  - `modules/transport/src/endpoint.rs`
  - `modules/transport/src/connection.rs`
  - `modules/transport/src/envelope.rs`
  - `modules/transport/src/codec.rs`
  - `modules/transport/src/permission.rs`
  - `modules/transport/src/commands.rs`
  - `modules/transport/src/events.rs`
  - `modules/transport/src/ports.rs`
  - `modules/transport/src/error.rs`
  - `modules/transport/tests/envelope_fixture_test.rs`

## 验收标准

- [ ] 只接受generated V1复制MessageTypes；未知/新增值拒绝。
- [ ] Envelope字段/大小/protocolVersion校验使用上游fixture，未手写第二enum。
- [ ] 公开API无quinn/rustls/socket/auth/session类型。
- [ ] `PermissionGrantRef`由transport拥有，只是不可变ID/epoch引用。
- [ ] 无handler registry/RPC/callback。

## 依赖

- [`consume-upstream-generated-contract-artifacts`](./consume-upstream-generated-contract-artifacts.md)
- [`implement-host-runtime-bounded-ports`](./implement-host-runtime-bounded-ports.md)

## 接口

Consumes:
- generated ReplicationEnvelope/IDs

Produces:
- Transport values、`EnvelopeCodec`/`ByteCarrier` SPI、commands/events
