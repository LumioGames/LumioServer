---
status: pending
---
# 实现 Process 配置、生命周期与显式 Components

## 涉及范围

- **Wave：** 4
- **归属：** `process`
- **唯一目标：** 建立配置合并/schema校验、ProcessLifecycle和具体Components/Factories结构，禁止通用hook/service locator。
- **文件集：
  - `modules/process/src/application.rs`
  - `modules/process/src/components.rs`
  - `modules/process/src/config.rs`
  - `modules/process/src/exit.rs`
  - `modules/process/src/error.rs`
  - `modules/process/tests/startup_order_test.rs`

## 验收标准

- [ ] 配置来源优先级/unknown field/secret reference校验明确，启动后snapshot不可变。
- [ ] Components字段逐个命名，构造/析构顺序显式；不存在Vec<dyn Service>/callback map。
- [ ] 任一构造失败逆序释放且listener未开放。
- [ ] ProcessLifecycle合法转移/幂等stop/exit mapping有测试。
- [ ] 公共ErrorCode不被重定义；ProcessExitCode仅OS adapter私有。

## 依赖

- [`implement-host-profile-resolution-and-capability-matching`](./implement-host-profile-resolution-and-capability-matching.md)
- [`implement-observability-diagnostic-metrics-trace-pipeline`](./implement-observability-diagnostic-metrics-trace-pipeline.md)
- [`implement-coreclr-generated-abi-contract-facade`](./implement-coreclr-generated-abi-contract-facade.md)
- [`implement-release-catalog-manifest-verification`](./implement-release-catalog-manifest-verification.md)

## 接口

Consumes:
- CLI/config/profile/generated contract locks

Produces:
- ProcessApplication core、ProcessConfigSnapshot、Components
