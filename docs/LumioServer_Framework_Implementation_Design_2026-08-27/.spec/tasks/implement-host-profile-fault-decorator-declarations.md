---
status: pending
---
# 实现故障 Profile 声明与生产禁用守卫

## 涉及范围

- **Wave：** 3
- **归属：** `host-profiles`
- **唯一目标：** 增加仅描述、不执行的 deterministic fault plan，并阻止测试 adapter进入生产 composition。
- **文件集：
  - `modules/host-profiles/src/fault_profile.rs`
  - `modules/host-profiles/tests/fault_profile_test.rs`

## 验收标准

- [ ] fault plan只含 seed、fault class、scope、schedule/budget，不含闭包或模块对象。
- [ ] 相同输入产生稳定plan hash；非法无界延迟/队列扩张拒绝。
- [ ] production profile引用injected/fault adapter时validation失败。
- [ ] 不新增一等模块依赖。

## 依赖

- [`implement-host-profile-resolution-and-capability-matching`](./implement-host-profile-resolution-and-capability-matching.md)

## 接口

Consumes:
- HostCompositionPlan

Produces:
- `FaultDecoratorPlan`
