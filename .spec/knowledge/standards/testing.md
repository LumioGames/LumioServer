---
name: testing
description: 测试与验收——测试分层政策、TDD 时机、验收 DoD 与验证证据;实现功能/修 bug 时查
metadata:
  type: doc
  status: 已交付
---

# 测试与验收（含 TDD 政策）

> 本文定**政策**（测什么、何时测、怎么算过）；“先写失败测试再实现”的**方法**在技能 [`skills/test-driven-development`](../../skills/test-driven-development/SKILL.md)。

## 测试分层（通用政策）

- **单元测试**：默认层，随项目验证命令（`AGENTS.md`「收口门槛」）每次跑，快、无外部依赖。
- **集成测试**（真库 / 真服务）：显式触发，不进默认验证命令，保持收口快。
- **端到端 / E2E**：显式触发；关键主链路至少一条。

## 何时走 TDD

- 必须走：新功能、修 bug（先写能复现的失败测试，修完留作回归测试）、改无测试保护的关键逻辑。
- 可不走：纯文档改动、一次性脚本。豁免在交回物里声明。
- 写测试、加 mock、想给生产类加 test-only 方法前，先查反模式清单：[`testing-anti-patterns.md`](../../skills/test-driven-development/testing-anti-patterns.md)——测 mock 行为、test-only 方法入生产、不理解依赖就 mock、不完整 mock，一律禁止。

## 验证证据

形式要求以 `AGENTS.md`「交回物格式」为单一权威——「已通过」三个字不是证据。

## 验收标准（Definition of Done）

- [ ] 收口门槛命令全绿（至少执行 `node .spec/tools/spec-lint.mjs` 与 `node --test .spec/tools/spec-lint.test.mjs`；Cargo 工程还须执行本节 Rust 命令）。
- [ ] 新增 / 修改行为有测试覆盖；bug 修复留有回归测试。
- [ ] 无 lint / 类型错误、无调试残留。
- [ ] 相关知识文档已更新（见 [`workflow.md`](./workflow.md)）。

## 项目测试栈与命令

当前仓库已建立 Rust workspace 骨架；规范/结构默认验证为：

```text
node .spec/tools/spec-lint.mjs
node --test .spec/tools/spec-lint.test.mjs
```

首次引入 Cargo 工程时，必须加入 `cargo fmt --check`、`cargo clippy`、单元/集成测试、网络故障注入、资源/负载测试和供应链检查。公共 Host/Network/Release Contract 变更还必须在架构源安装依赖并运行 `python3 tools/lumio_contract.py validate`。

## Rust 验证命令与分类

Foundation 收口按以下顺序执行，所有 Cargo 命令使用锁定解析：

```text
cargo metadata --locked --no-deps
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --all-features --locked -- -D warnings
cargo nextest run --workspace --locked
cargo test --doc --workspace --locked
cargo xtask contracts verify
cargo xtask policy check
cargo deny check
cargo audit --file Cargo.lock
```

`cargo metadata`/fmt/clippy 是结构与静态质量门；`cargo nextest` 是单元/集成测试门；`cargo test --doc` 覆盖文档示例；`xtask policy` 检查仓内 DAG、源码红线、队列登记和封锁规则，`xtask contracts` 只验证架构源发布的 lock/artifact 输入，不能把缺失输入报告为成功；`cargo deny` 与 `cargo audit` 是许可证、来源和漏洞门。工具未安装或上游契约输入尚未提供时，必须在证据中记录实际退出码和 `not run/blocked` 原因，不得以“通过”替代。

测试分类纪律：新行为先写可复现失败测试再实现；故障/网络/真实文件系统测试显式触发，不把外部服务或 flaky 重试混入默认快速门；负向 fixture 必须确认稳定拒绝；性能、RSS、队列深度和 Tick p50/p95/p99 归 benchmark 波次。`unsafe`、FFI、线程取消、bounded queue 满载、epoch/ack 竞态和恢复路径必须有针对性测试，不能只依赖编译成功。

Cargo 骨架阶段没有领域行为，允许仅验证入口解析、配置继承、封锁路径和最小 smoke 测试；不得为了填充覆盖率伪造业务测试或把未来 Contract/Policy 结果硬编码成成功。

## 本仓 Headless / 契约测试面

- DS 启停、Admission、握手、重连、Session/WorldSlot、Tick pacing、Quota、Watchdog 和维护。
- Wire Envelope、可靠性、分片、Ack、限流、背压、认证、防重放和网络故障注入。
- LocalEmbedded 的同 Codec/同权限/有界队列保真度，以及 LocalSplitProcess 端口/进程隔离。
- Release Catalog、Hash/Signature/Capability 拒绝、滚动更新、Drain、强制踢人和 Rollback。
- Snapshot/WAL 恢复、磁盘满、OOM、CoreCLR/ALC/Native 故障、日志背压和 Failure Bundle。
- 1/10/25/50/100/150/200 玩家 Workload，记录 Tick p50/p95/p99、CPU、RSS、GC、队列和网络。
