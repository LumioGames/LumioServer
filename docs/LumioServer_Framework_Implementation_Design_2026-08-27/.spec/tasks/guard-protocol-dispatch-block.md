---
status: pending
---
# 固化 protocol-dispatch 零实现封锁

## 涉及范围

- **Wave：** 2
- **归属：** `protocol-dispatch`
- **唯一目标：** 以README+policy manifest+mutation fixture禁止创建crate/src/API/依赖边，直到D-009完整解锁条件成立。
- **文件集：
  - `modules/protocol-dispatch/README.md`
  - `.spec/guards/protocol-dispatch-blocked.toml`
  - `tests/policy/invalid_protocol_dispatch.toml`

## 验收标准

- [ ] 目录下不存在Cargo.toml/src；workspace metadata无package。
- [ ] 任意crate dependency/use/module token mutation均被policy check拒绝。
- [ ] V1允许MessageTypes与上游generated集合完全相等，本仓不能增加RPC/dispatch值。
- [ ] README列出D-009、新Baseline、Schema/ID/fixture/三图/Queue Matrix全部解锁条件。
- [ ] 不选择或评估具体RPC框架作为预接口。

## 依赖

- [`add-architecture-policy-xtask`](./add-architecture-policy-xtask.md)
- [`consume-upstream-generated-contract-artifacts`](./consume-upstream-generated-contract-artifacts.md)

## 接口

Consumes:
- D-009 gate、generated V1 messageType set

Produces:
- 机器可验证的零实现封锁
