---
status: pending
---
# 测量并分类 SRV-D-001..017 临时默认

## 涉及范围

- **Wave：** 16
- **归属：** `benchmark`
- **唯一目标：** 以固定workload/hardware/build metadata运行容量、延迟、jitter、durability与shutdown基准，把结果标记为measured/retain/change候选而非公共常量。
- **文件集：
  - `benches/Cargo.toml`
  - `benches/src/main.rs`
  - `benches/src/workload.rs`
  - `benches/src/report.rs`
  - `benches/workloads/foundation-vertical.toml`
  - `manifests/provisional-defaults-measurement.json`
  - `docs/specs/2026-08-27-foundation-measurement-report.md`

## 验收标准

- [ ] 报告记录BaselineId、git/toolchain/target/CPU、profile、dataset hash、p50/p95/p99/max、RSS、alloc/copy bytes、queue depth。
- [ ] 覆盖Ingress/Egress、diagnostic/audit/durable queues、tick jitter、watchdogs、reconnect、checkpoint、grace shutdown等适用默认。
- [ ] 数值只写measurement manifest/config建议，不生成pub const或改公共Schema。
- [ ] 相同workload可重复并能比较差异；未测项明确标记`unmeasured-blocked-by-<decision>`而非猜值。
- [ ] CI只做smoke，正式结果有独立可审计命令。

## 依赖

- [`verify-maintenance-dual-ack-fault-domain-and-stale-epoch`](./verify-maintenance-dual-ack-fault-domain-and-stale-epoch.md)
- [`verify-local-split-process-carrier-contract`](./verify-local-split-process-carrier-contract.md)

## 接口

Consumes:
- E2E workloads、所有metrics

Produces:
- provisional defaults measurement report/manifest
