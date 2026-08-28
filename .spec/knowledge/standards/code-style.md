---
name: code-style
description: 代码与文档风格——语言约定、命名、注释原则、生成物纪律;写代码/建文档时查
metadata:
  type: doc
  status: 已交付
---

# 代码与文档风格

> 能交给工具（formatter / linter）强制的，优先交给工具；本文只写工具管不了、需要人 / Agent 判断的部分。

## 语言与文件命名（通用）

- **规范主体使用中文**（`.spec/` 下全部文档）；例外是根 `CLAUDE.md` 与既有英文 Skill。单份文档内保持语言一致，状态枚举沿用本仓中文定义。
- 文件与目录命名一律 **kebab-case**；agent 文件 `<name>.agent.md`、skill 目录 `skills/<name>/`、ADR `NNNN-<slug>.md`。

## 注释原则（通用）

- 注释只写**代码表达不了的约束**（为什么这样做、边界条件、外部依赖的坑）。
- 不写「改动说明」式注释（改了什么、为什么正确）——那是给评审人的话，进交回物或提交信息，不进代码。
- 注释密度、命名、习语向**周边既有代码**看齐。

## 生成物纪律（通用）

- 生成物不得手改，只能经生成源与生成命令更新，并与生成源一起提交（红线见 [`rules/system.md`](../../rules/system.md)）。

## 语言 / 框架特定风格

- Server Host 与网络基础设施使用 Rust；CoreCLR/Gameplay 只能经版本化 Host/Runtime Contract 和隔离 Adapter 加载。
- Rust 工程固定在 `rust-toolchain.toml` 的 `1.98.0`（`rustfmt` 与 `clippy` 组件）；workspace package 必须继承 `edition`、`rust-version`、`license` 与 `[lints] workspace = true`，不得在 crate 内漂移。
- Rust 模块、文件、函数与局部变量使用 `snake_case`，类型与 trait 使用 `PascalCase`，常量使用 `SCREAMING_SNAKE_CASE`；已发布 Envelope/Manifest/Schema 标识符保持原拼写。
- 规范正文使用中文，代码标识符、协议字段和命令保留原始英文；Markdown 与结构化文本保持 LF（见根 `.gitattributes`）。
- Envelope、Endpoint、ReleaseCatalog、Maintenance 与日志 Contract 的生成物只从架构源生成，记录 Compiler/Input/Output Hash，不维护第二套手写协议。

## Rust 工具链与 lint 纪律

- 格式统一由 `cargo fmt --all -- --check` 判定；不得以手工格式差异绕过检查。
- 质量门统一使用 `cargo clippy --workspace --all-targets --all-features --locked -- -D warnings`。新增 lint 例外必须有明确的代码边界和评审依据，不能用全局 `allow` 掩盖问题。
- `unsafe` 默认禁止（workspace `unsafe_code = "deny"`）；只有 FFI/平台 Adapter 的最小边界可在后续任务提出局部、可审计的例外，并附安全不变量和测试。
- 依赖版本由 workspace 集中管理；Cargo.lock 必须提交，CI 与本地收口命令使用 `--locked`。未经批准的网络、日志、CoreCLR 或协议供应商不得进入骨架。
- 生产代码不得直接 `spawn`、`sleep`、轮询或构造无界 channel；线程、Timer、异步任务和端口统一经 `host-runtime` 的监督 API。文档中的反例文字不构成实现许可。

## 骨架阶段入口

当前 workspace 只包含无生产行为的 `process` 与 `xtask` 骨架。`process` 的 binary 必须保持薄入口，业务组装、协议和模块实现由后续任务按所有权加入；`protocol-dispatch` 永不因 workspace glob 自动成为 package。
