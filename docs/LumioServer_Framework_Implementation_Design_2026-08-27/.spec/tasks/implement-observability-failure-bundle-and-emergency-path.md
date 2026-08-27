---
status: pending
---
# 实现 Failure Bundle 与 Crash-safe Emergency Path

## 涉及范围

- **Wave：** 5
- **归属：** `observability`
- **唯一目标：** 以固定 typed evidence ports 汇集 generated FailureBundle，支持partial/missing provider并提供最小崩溃写入路径。
- **文件集：
  - `modules/observability/src/evidence.rs`
  - `modules/observability/src/bundle.rs`
  - `modules/observability/src/emergency.rs`
  - `modules/observability/tests/failure_bundle_test.rs`
  - `modules/observability/tests/emergency_path_test.rs`

## 验收标准

- [ ] 不存在任意 provider closure registry；source列表在composition时静态给定。
- [ ] provider超时/关闭生成合法partial bundle，明确missing source/reason。
- [ ] bundle hash/长度/字段通过上游正反fixture；不伪造缺失证据。
- [ ] emergency path不依赖普通diagnostic queue，不格式化secret，不无限分配。
- [ ] 同correlation重复请求可合并且均收到completion。

## 依赖

- [`implement-observability-audit-durable-pipeline`](./implement-observability-audit-durable-pipeline.md)
- [`implement-host-runtime-supervision-cancellation-and-join`](./implement-host-runtime-supervision-cancellation-and-join.md)

## 接口

Consumes:
- generated FailureBundle、typed evidence fragments

Produces:
- `FailureBundlePort`、`EvidenceRequest/Fragment`、`BundleCompletion`
