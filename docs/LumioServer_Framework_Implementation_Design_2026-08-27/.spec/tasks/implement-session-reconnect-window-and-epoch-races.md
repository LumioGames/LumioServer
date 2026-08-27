---
status: pending
---
# 实现重连窗口、Timer 与旧完成拒绝

## 涉及范围

- **Wave：** 8
- **归属：** `session`
- **唯一目标：** 为断开Session保留有界metadata/opaque handle，使用host-runtime timer并处理disconnect/reconnect/expiry/kick竞态。
- **文件集：
  - `modules/session/src/reconnect.rs`
  - `modules/session/tests/reconnect_race_test.rs`
  - `modules/session/tests/reconnect_budget_test.rs`

## 验收标准

- [ ] 重连窗口数字仅配置默认；每个timer有generation。
- [ ] 同Session至多一个active connection，旧connection/auth/bind completion全拒绝。
- [ ] expiry与new connection同时发生时有确定线性化点并不泄漏slot reservation。
- [ ] 保留Session数/bytes受预算；满载明确拒绝新重连保留或淘汰已终态记录，不能无界。
- [ ] 不自建timer/thread。

## 依赖

- [`implement-session-registry-state-and-admission-saga`](./implement-session-registry-state-and-admission-saga.md)
- [`implement-host-runtime-clock-and-timer-delivery`](./implement-host-runtime-clock-and-timer-delivery.md)

## 接口

Consumes:
- Disconnected session、TimerFired、new candidate

Produces:
- Reconnect reducer/metadata
