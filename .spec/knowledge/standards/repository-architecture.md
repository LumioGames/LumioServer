---
name: repository-architecture
description: 仓库边界与架构契约——Server Host、网络和 Release 所有权;改连接、WorldSlot 或维护流程前查
metadata:
  type: doc
  status: 已交付
---

# 仓库边界与架构契约

## 规范来源与优先级

- Agent 的开发流程、测试政策和交付规则以 `.spec/` 为权威。
- 模块边界以根 [`README.md`](../../../README.md) 为本仓入口；共享架构以 `LumioGameEngineArchitecture` 的 `LGE-V1.0-2026-08-27` 为唯一来源，本仓 [`架构镜像`](../../../docs/architecture/LumioGameEngine_Architecture_v1.0.md) 只读。
- 冲突时不得在 Host 内自行改写公共 Envelope/Release/Capability；先在架构源完成 ADR、Schema、Fixture 和新 Baseline。

## 所有权边界

- 本仓拥有进程、连接、Endpoint、Admission、Release Pool 路由、WorldSlot、Host Wall Clock/pacing、CoreCLR Hosting、滚动更新、维护和资源配额。
- Runtime 拥有 Tick 内语义与 ECS/Coordinator，VoxelEngine 拥有 Voxel 状态，Game 拥有玩法；Host 只驱动与编排，不直接改权威领域状态。
- 网络回调只进入有界队列；权威变化在 Runtime 固定 Tick Barrier 应用。
- LocalEmbedded 可以绕过 Socket/TLS/OS 网络栈，但不能绕过 Schema、Codec、Envelope、权限、大小限制、队列或 Tick 交付。

## Architecture Gate

- ReleaseCatalog/Manifest、Envelope、Maintenance、Logging Event、Host Capability 与错误语义只在架构源维护；变化必须带正向/失败 Fixture。
- 网络错误明确分为可重试、可拒绝、可致命；认证、防重放、限流、背压和审计不能由本地快捷路径跳过。
- 升级不得覆盖旧 Release/Snapshot；Drain、强制维护和 Rollback 必须按目标 Pool 隔离、可审计、可恢复、可回放。
- 资源配额、有界队列、Watchdog 与维护超时必须有 Metrics、Failure Bundle 和故障测试。
- CoreCLR/ALC/Native 故障域明确隔离；可恢复 Session Fault 与进程级崩溃不能伪装成同类错误。
