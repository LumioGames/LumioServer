---
status: pending
---
# 实现 Profile 解析、Capability 匹配和 Composition Plan

## 涉及范围

- **Wave：** 2
- **归属：** `host-profiles`
- **唯一目标：** 将 generated HostCapability、配置和 preset 纯函数化为 immutable plan，零一等模块依赖。
- **文件集：
  - `modules/host-profiles/Cargo.toml`
  - `modules/host-profiles/src/lib.rs`
  - `modules/host-profiles/src/preset.rs`
  - `modules/host-profiles/src/capability.rs`
  - `modules/host-profiles/src/budget.rs`
  - `modules/host-profiles/src/composition.rs`
  - `modules/host-profiles/src/validation.rs`
  - `modules/host-profiles/src/error.rs`
  - `modules/host-profiles/tests/capability_fixture_test.rs`
  - `modules/host-profiles/tests/composition_matrix_test.rs`
  - `modules/host-profiles/tests/no_module_dependency_test.rs`

## 验收标准

- [ ] RemoteDS/LocalEmbedded/LocalSplitProcess/headless均生成明确 adapter class和requirements。
- [ ] LocalEmbedded计划必须包含 Schema/Codec/Envelope/auth/permission/size/queue/Tick delivery全部层。
- [ ] 静态 capability、configured limits分离，无动态queue depth/health字段。
- [ ] RemoteDS缺 D-010 production channel capability时精确拒绝。
- [ ] cargo metadata证明该crate不依赖任何一等模块。

## 依赖

- [`consume-upstream-generated-contract-artifacts`](./consume-upstream-generated-contract-artifacts.md)

## 接口

Consumes:
- generated HostCapability、配置输入

Produces:
- `ValidatedHostCompositionPlan`、`ConfiguredBudgets`
