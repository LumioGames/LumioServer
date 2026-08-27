---
status: pending
---
# 验收 LocalEmbedded 可运行垂直骨架

## 涉及范围

- **Wave：** 14
- **归属：** `e2e`
- **唯一目标：** 从Handshake Envelope bytes开始，完整经过codec/schema/auth/permission/session/slot queue/Tick Barrier并产生架构允许的egress。
- **文件集：
  - `tests/e2e/tests/local_embedded_vertical_test.rs`
  - `tests/e2e/fixtures/local_embedded_scenario.toml`

## 验收标准

- [ ] 输入必须是bytes+generated Envelope，禁止对象直连。
- [ ] 路径证据包含Envelope validate、opaque auth、grant ref、ServerConnectionSession admission、slot reservation、ingress queue、owner Tick、egress queue。
- [ ] 删除/绕过任一层的mutation版本必须失败。
- [ ] 权威变化只在Managed Tick Barrier outcome出现；network/control runner无Gameplay调用。
- [ ] 场景结束通过process→world-slot结构化关闭，资源归零。

## 依赖

- [`add-e2e-reference-host-shell`](./add-e2e-reference-host-shell.md)
- [`add-repository-dag-queue-source-and-license-gates`](./add-repository-dag-queue-source-and-license-gates.md)

## 接口

Consumes:
- ReferenceHost、LocalEmbedded carrier、injected verifier/managed runtime fixture

Produces:
- Foundation runnable vertical skeleton证据
