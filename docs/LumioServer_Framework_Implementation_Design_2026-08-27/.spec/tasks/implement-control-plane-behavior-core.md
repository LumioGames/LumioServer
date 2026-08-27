---
status: pending
---
# 实现 Control Plane 验证、Fencing 与幂等行为 Core

## 涉及范围

- **Wave：** 5
- **归属：** `control-plane-adapter`
- **唯一目标：** 在不选择D-010通道/wire/算法的前提下，定义opaque frame、authenticator SPI、fencing/idempotency和verified typed output。
- **文件集：
  - `modules/control-plane-adapter/Cargo.toml`
  - `modules/control-plane-adapter/src/lib.rs`
  - `modules/control-plane-adapter/src/frame.rs`
  - `modules/control-plane-adapter/src/authenticator.rs`
  - `modules/control-plane-adapter/src/fencing.rs`
  - `modules/control-plane-adapter/src/idempotency.rs`
  - `modules/control-plane-adapter/src/commands.rs`
  - `modules/control-plane-adapter/src/channel.rs`
  - `modules/control-plane-adapter/src/service.rs`
  - `modules/control-plane-adapter/src/error.rs`
  - `modules/control-plane-adapter/tests/fencing_test.rs`
  - `modules/control-plane-adapter/tests/verification_order_test.rs`

## 验收标准

- [ ] 未通过auth→fence→idempotency的frame永不进入VerifiedControlQueue。
- [ ] old fence、duplicate same payload、duplicate conflicting payload终态精确。
- [ ] frame/signature禁止Debug/Serialize明文和日志；无key示例。
- [ ] 公开API无gRPC/HTTP/cloud SDK/crypto算法类型。
- [ ] D-010前production channel factory只能返回精确unavailable capability，不伪造wire。

## 依赖

- [`implement-host-runtime-bounded-ports`](./implement-host-runtime-bounded-ports.md)
- [`implement-observability-audit-durable-pipeline`](./implement-observability-audit-durable-pipeline.md)
- [`consume-upstream-generated-contract-artifacts`](./consume-upstream-generated-contract-artifacts.md)

## 接口

Consumes:
- generated MaintenanceCommand、opaque channel frame

Produces:
- `VerifiedControlCommand`、Authenticator/Channel SPI、fencing reducer
