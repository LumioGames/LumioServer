---
status: pending
---
# 封闭 Persistence Durability 故障矩阵

## 涉及范围

- **Wave：** 7
- **归属：** `persistence-host`
- **唯一目标：** 覆盖ENOSPC、short write、corruption、lock loss、queue saturation、迟到duplicate和shutdown中断的可验证终态。
- **文件集：
  - `modules/persistence-host/tests/durability_fault_matrix_test.rs`
  - `modules/persistence-host/tests/recovery_property_test.rs`

## 验收标准

- [ ] 每个故障点输出可拒绝/可重试/DurabilityUnavailable/RecoveryFailed之一，无隐式成功。
- [ ] 任意操作序列下active snapshot始终通过Header/hash/length验证。
- [ ] ack集合是实际durable records的子集且不回退。
- [ ] shutdown后所有未完成request有terminal event或明确abandoned evidence。
- [ ] 测试不依赖真实时钟/随机不可重放输入。

## 依赖

- [`implement-persistence-recovery-checkpoint-and-migration-adapter`](./implement-persistence-recovery-checkpoint-and-migration-adapter.md)

## 接口

Consumes:
- fault-injected storage、writers/recovery

Produces:
- 持久化模块Foundation退出证据
