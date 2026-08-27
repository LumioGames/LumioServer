---
status: pending
---
# 实现 ServerConnectionSession 与接纳 Saga

## 涉及范围

- **Wave：** 7
- **归属：** `session`
- **唯一目标：** 建立单写SessionRegistry及transport candidate→auth→exact release→slot reservation→transport bind的显式effect/compensation链。
- **文件集：
  - `modules/session/Cargo.toml`
  - `modules/session/src/lib.rs`
  - `modules/session/src/id.rs`
  - `modules/session/src/state.rs`
  - `modules/session/src/session.rs`
  - `modules/session/src/registry.rs`
  - `modules/session/src/admission.rs`
  - `modules/session/src/binding.rs`
  - `modules/session/src/commands.rs`
  - `modules/session/src/events.rs`
  - `modules/session/src/service.rs`
  - `modules/session/src/error.rs`
  - `modules/session/tests/admission_saga_test.rs`
  - `modules/session/tests/server_name_guard_test.rs`

## 验收标准

- [ ] 服务端记录/类型/状态统一命名`ServerConnectionSession`；source guard拒绝ClientReplicaSession。
- [ ] 每一步effect有attempt id/epoch和显式ack；任一点失败恰好执行一次补偿。
- [ ] session不写ConnectionRegistry/Admission Gate，只发送transport/world-slot commands。
- [ ] 只有auth grant成功、ExactRelease、slot commit、transport bind全部ack后进入Active。
- [ ] ReplicationContext只保存opaque handle，不解释Runtime语义。

## 依赖

- [`implement-auth-replay-grant-revocation-and-epoch`](./implement-auth-replay-grant-revocation-and-epoch.md)
- [`implement-release-local-member-state-health-and-reporting`](./implement-release-local-member-state-health-and-reporting.md)
- [`implement-world-slot-aggregate-epoch-admission-and-quota`](./implement-world-slot-aggregate-epoch-admission-and-quota.md)
- [`implement-transport-local-embedded-fidelity-adapter`](./implement-transport-local-embedded-fidelity-adapter.md)

## 接口

Consumes:
- transport/auth/release/world typed commands/events

Produces:
- SessionRegistry、AdmissionReducer、Session ports
