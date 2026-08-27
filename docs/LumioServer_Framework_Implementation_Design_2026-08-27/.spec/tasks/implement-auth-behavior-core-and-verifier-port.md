---
status: pending
---
# 实现 Auth 行为 Core 与不透明 Verifier Port

## 涉及范围

- **Wave：** 4
- **归属：** `auth`
- **唯一目标：** 定义 opaque credential、auth request/result、secret-safe verifier SPI和串行服务，不选择D-011 wire/算法。
- **文件集：
  - `modules/auth/Cargo.toml`
  - `modules/auth/src/lib.rs`
  - `modules/auth/src/credential.rs`
  - `modules/auth/src/verifier.rs`
  - `modules/auth/src/identity.rs`
  - `modules/auth/src/commands.rs`
  - `modules/auth/src/events.rs`
  - `modules/auth/src/service.rs`
  - `modules/auth/src/audit.rs`
  - `modules/auth/src/error.rs`
  - `modules/auth/src/adapters/injected.rs`
  - `modules/auth/tests/behavior_contract_test.rs`
  - `modules/auth/tests/secret_redaction_test.rs`

## 验收标准

- [ ] `OpaqueCredentialInput`无Serialize/Debug明文；drop zeroize。
- [ ] Verifier SPI输入/输出supplier-neutral，D-011前只存在injected test adapter。
- [ ] 认证completion只发AuthEvent，不修改transport/session。
- [ ] invalid/expired/verifier-unavailable/busy均有typed result与audit fact。
- [ ] source scan和secret corpus测试通过；无样例key/token。

## 依赖

- [`implement-host-runtime-bounded-ports`](./implement-host-runtime-bounded-ports.md)
- [`implement-observability-diagnostic-metrics-trace-pipeline`](./implement-observability-diagnostic-metrics-trace-pipeline.md)

## 接口

Consumes:
- opaque handshake body、AuthRequest

Produces:
- `CredentialVerifier`、`AuthenticateCommand`、`AuthEvent`
