# 0003 · 五分钟重连窗由 Host Timer 持有，不用 Native Tick

- 日期：2026-09-01
- 状态：生效

## 背景

R-00350 要求断线保留实体五分钟、窗内重绑同一 `NetEntityId`、到期墓碑化。C-4（`lumio.native-timer-abi.v1`）把墙钟 deadline 与 Tick/Frame 调度分成两层。R-00279 的 session saga 已有连接重连，不能改写成第二套定时语义。

## 决策

1. 实体保留窗放在 `RoomAdmissionRegistry`：断线、输入拒绝、窗内重绑、到期墓碑都在绑定登记上完成。
2. 五分钟 deadline 经 R-00272 `ITimerService` 投递类型化 `ReconnectExpiryCommand`；时钟是进程本地 `IMonotonicClock`。过期不读墙钟、不读 Native Tick。
3. 生产默认 300 秒；10 秒只作为标明的测试覆写。Session 生产默认与此对齐，saga 步骤不改。
4. 进程重启不保留窗口。`NetEntityId` 带进程实例前缀，避免新进程序号与旧引用撞车。

## 后果

- 不引入第二套 ECS，不把重连窗迁入 Native Timer Manager。
- 复制 FullSnapshot 由登记输出当前纪元绑定；持久化 Snapshot/Restore 仍归 R-00353。
