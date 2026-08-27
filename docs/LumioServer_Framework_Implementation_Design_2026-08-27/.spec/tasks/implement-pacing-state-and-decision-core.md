---
status: pending
---
# 实现 Pacing 状态机与纯决策函数

## 涉及范围

- **Wave：** 3
- **归属：** `pacing`
- **唯一目标：** 定义不含Logical TickId的 scheduler state、deadline/overrun纯函数和typed commands。
- **文件集：
  - `modules/pacing/Cargo.toml`
  - `modules/pacing/src/lib.rs`
  - `modules/pacing/src/config.rs`
  - `modules/pacing/src/state.rs`
  - `modules/pacing/src/decision.rs`
  - `modules/pacing/src/commands.rs`
  - `modules/pacing/src/permit.rs`
  - `modules/pacing/src/error.rs`
  - `modules/pacing/tests/decision_property_test.rs`

## 验收标准

- [ ] pause/resume/stop合法转移完整；只有world-slot command可改变运行状态。
- [ ] `TickPermit`不含Logical TickId和业务引用。
- [ ] deadline单调、debt有上限、相同输入相同action的property通过。
- [ ] API无callback/Tokio/world-slot类型；使用pacing-owned epoch value。
- [ ] 配置数字非pub const。

## 依赖

- [`implement-host-runtime-bounded-ports`](./implement-host-runtime-bounded-ports.md)
- [`implement-host-profile-resolution-and-capability-matching`](./implement-host-profile-resolution-and-capability-matching.md)

## 接口

Consumes:
- PacingConfig、MonotonicInstant

Produces:
- `PacingCommand`、`TickPermit`、`PacingDecision`
