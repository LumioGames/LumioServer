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
- 当前仓库尚未提交 Cargo 工程。首次引入代码时必须固定 Rust toolchain、`rustfmt`、`clippy`、依赖/漏洞审计与可复现验证命令，并更新本文和 [`testing.md`](./testing.md)。
- Rust 模块、文件、函数与局部变量使用 `snake_case`，类型与 trait 使用 `PascalCase`，常量使用 `SCREAMING_SNAKE_CASE`；已发布 Envelope/Manifest/Schema 标识符保持原拼写。
- 规范正文使用中文，代码标识符、协议字段和命令保留原始英文；Markdown 与结构化文本保持 LF（见根 `.gitattributes`）。
- Envelope、Endpoint、ReleaseCatalog、Maintenance 与日志 Contract 的生成物只从架构源生成，记录 Compiler/Input/Output Hash，不维护第二套手写协议。
