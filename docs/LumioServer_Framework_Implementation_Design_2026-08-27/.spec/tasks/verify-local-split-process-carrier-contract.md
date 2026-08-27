---
status: pending
---
# 验收 LocalSplitProcess Carrier 合同

## 涉及范围

- **Wave：** 15
- **归属：** `e2e`
- **唯一目标：** 在D-004 adapter可用时以两个进程/loopback carrier运行同一垂直场景，并与LocalEmbedded结果做contract diff。
- **文件集：
  - `tests/e2e/tests/local_split_process_vertical_test.rs`
  - `tests/e2e/src/contract_diff.rs`
  - `tests/e2e/fixtures/local_split_process_scenario.toml`

## 验收标准

- [ ] D-004未满足时测试验证profile精确拒绝而非静默fallback；满足时执行完整场景。
- [ ] wire bytes/Envelope/reject/queue/Tick结果与LocalEmbedded在允许差异外一致。
- [ ] 两进程无共享mutable state/secret，端口关闭和child exit有界。
- [ ] 第三方carrier类型不出e2e scenario API。
- [ ] 故障注入断链只fault对应connection/session。

## 依赖

- [`verify-local-embedded-vertical-skeleton`](./verify-local-embedded-vertical-skeleton.md)
- [`implement-transport-remote-and-fault-adapters`](./implement-transport-remote-and-fault-adapters.md)
- 外部依赖：D-004满足时运行正向场景；未满足时运行拒绝场景。

## 接口

Consumes:
- Remote carrier gate、ReferenceHost contract recorder

Produces:
- LocalSplitProcess contract parity报告
