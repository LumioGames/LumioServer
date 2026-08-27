# LumioServer `protocol-dispatch` 模块实现级设架

> 架构基线：`LGE-V1.2-2026-08-27`  
> 实现优先级：**封锁**  
> crate：`无 crate；不得创建 `Cargo.toml`、`src/` 或可编译 target`  
> 本文只定义仓内实现结构；公共 Schema / ABI / ID / 状态机仍由 `LumioGameEngineArchitecture` 维护。

## A. 一句话职责与非职责

**职责：** D-009 冻结前仅保留边界封锁说明与仓库守卫，声明 V1 wire 只包含架构源复制 Envelope MessageTypes；零实现、零 API、零依赖者。

**明确不负责：**
- 不设计 handler registry、RPC wire、correlation/deadline/cancel/idempotency协议。
- 不导出 trait/type/command/event，不成为 transport 或 world-slot 的中转层。
- 不创建测试替身来暗中固定未来接口。
- 不以内部 Message enum 扩展公共 `messageType`。

## B. crate、目录与文件清单

该目录保持非 crate。允许存在的文件如下：

| 文件 | 唯一职责 |
| --- | --- |
| `modules/protocol-dispatch/README.md` | 保留冻结原因、D-009、禁止依赖和解锁检查表。 |
| `../../.spec/guards/protocol-dispatch-blocked.toml` | 由 xtask读取：禁止 Cargo member/src/use依赖；不是模块实现。 |

## C. 公开/内部类型、Trait、命令、事件与端口

### C.1 类型、命令与事件
- 无公开或内部运行时类型。

### C.2 稳定仓内端口
| 接口 | 落点 | 契约 |
| --- | --- | --- |
| 无 | — | 任何提议端口必须先进入架构源和 D-009 ADR，而不是本仓。 |

### C.3 Rust 签名草案

以下签名是**仓内实现接口**，不是新增公共 ABI；实现时只能引用上游 generated 类型，不能复制公共字段。

```rust
// D-009 解锁前：无 Rust API、无 Cargo target、无 src 目录。
```

## D. 状态、资源与生命周期所有权

- 仅 README 中的封锁状态、解锁条件和 xtask policy manifest。
- 无运行时状态、线程、队列、资源或错误。
- 无 Cargo package identity。

### D.1 模块红线
- 任何 crate 对它的依赖都是架构违规。
- V1 vertical skeleton直接把已验证复制 Envelope交给 Runtime Tick入口，不经过私造 dispatch。

## E. 线程、队列、背压与关闭

### E.1 线程/执行上下文
- 无。

### E.2 队列合同
本模块无运行时队列；如实现中出现队列，必须先更新 `modules/README.md` Queue Contract Matrix 与 policy manifest。

## F. 失败分类、恢复与显式 Ack

| 分类 | 实现要求 |
| --- | --- |
| 构建守卫 | 发现 `modules/protocol-dispatch/Cargo.toml`、`src/` 或 crate依赖时，policy test失败。 |
| Schema守卫 | 发现本仓新增 messageType/RPC envelope字段时，contract sync失败。 |
| 解锁前置 | 未满足全部解锁条件时没有“可重试实现”，只能保持封锁。 |

## G. 编译依赖、成熟方案与 Adapter 隔离

### G.1 编译依赖
**允许：**


**禁止：**
- 所有 crate依赖它
- 所有生产/测试源码
- 任何 wire/API生成

### G.2 成熟方案选择
| 方案 | 用途 | 成熟性、许可证与隔离 |
| --- | --- | --- |
| 无 | 封锁模块不选库 | 避免库选型反向冻结尚未批准协议。 |

### G.3 明确拒绝的自研项
- 明确不自研 RPC/dispatch framework，也不提前选择 tonic/tower/axum handler模型。

## H. 测试面与 Fixture

- xtask：目录不得有 Cargo.toml/src。
- cargo metadata：workspace无该 package，依赖图无名字匹配。
- contract diff：V1 messageType只等于上游生成集合。
- grep/AST policy：禁止 `protocol_dispatch` module/use/token进入生产源码。

## I. 决策门与配置默认

- 解锁必须同时具备：D-009 accepted、架构源新 BaselineId、公共 message/RPC descriptors、错误/超时/取消/幂等语义、Queue Contract Matrix 行、正反 fixtures、允许的编译/命令/事件边。
- 只满足“需要RPC”或选定某个库不构成解锁。

## J. 本模块任务卡

| 任务 | Wave | 单一目标 | 依赖 |
| --- | --- | --- | --- |
| [`guard-protocol-dispatch-block`](../../../.spec/tasks/guard-protocol-dispatch-block.md) | Wave 2 | 以README+policy manifest+mutation fixture禁止创建crate/src/API/依赖边，直到D-009完整解锁条件成立。 | `add-architecture-policy-xtask`, `consume-upstream-generated-contract-artifacts` |

## K. 完成定义

完成定义是封锁守卫持续通过，而不是出现可编译代码。
