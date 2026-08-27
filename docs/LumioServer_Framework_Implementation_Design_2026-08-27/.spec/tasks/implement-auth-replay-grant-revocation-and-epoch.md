---
status: pending
---
# 实现重放、Grant、撤销与 Epoch 竞态

## 涉及范围

- **Wave：** 5
- **归属：** `auth`
- **唯一目标：** 组合 bounded LRU+monotonic expiry，产出 immutable PermissionGrant并拒绝旧connection/grant epoch。
- **文件集：
  - `modules/auth/src/replay.rs`
  - `modules/auth/src/permission.rs`
  - `modules/auth/tests/replay_property_test.rs`
  - `modules/auth/tests/grant_epoch_race_test.rs`

## 验收标准

- [ ] replay cache items/bytes有硬上限；同fingerprint窗口内至多成功一次。
- [ ] grant含principal/permission ids/expiry/grant epoch，不含credential。
- [ ] 认证完成vsdisconnect/reconnect/revoke竞态中旧epoch永不成功绑定。
- [ ] timer generation迟到拒绝；cache清理不自建线程。
- [ ] ReplayDetected/RiskDetected只发typed event，不直接断连接。

## 依赖

- [`implement-auth-behavior-core-and-verifier-port`](./implement-auth-behavior-core-and-verifier-port.md)
- [`implement-host-runtime-clock-and-timer-delivery`](./implement-host-runtime-clock-and-timer-delivery.md)

## 接口

Consumes:
- VerifierResult、MonotonicClock

Produces:
- `PermissionGrant`、replay/revocation reducer
