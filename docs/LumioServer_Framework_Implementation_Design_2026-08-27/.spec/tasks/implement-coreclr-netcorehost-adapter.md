---
status: pending
---
# 实现官方 hostfxr/nethost Rust Adapter

## 涉及范围

- **Wave：** 5
- **归属：** `coreclr-host`
- **唯一目标：** 通过netcorehost封装CoreCLR discovery/load/function table获取，集中unsafe和供应商错误映射。
- **文件集：
  - `modules/coreclr-host/src/bootstrap.rs`
  - `modules/coreclr-host/src/adapters/netcorehost.rs`
  - `modules/coreclr-host/src/ffi/mod.rs`
  - `modules/coreclr-host/tests/netcorehost_smoke_test.rs`

## 验收标准

- [ ] 只使用官方nethost/hostfxr路径，不使用legacy COM hosting。
- [ ] 所有unsafe集中在ffi/adapter并有Safety契约、null/length/version验证。
- [ ] 供应商handle/strings/errors不泄漏稳定API。
- [ ] 找不到runtime/config/entry时返回可诊断启动失败，无fallback私有ABI。
- [ ] 在支持平台运行smoke；不支持平台显式skip并验证capability拒绝。

## 依赖

- [`implement-coreclr-lifecycle-and-fault-passthrough`](./implement-coreclr-lifecycle-and-fault-passthrough.md)
- [`implement-host-runtime-supervision-cancellation-and-join`](./implement-host-runtime-supervision-cancellation-and-join.md)

## 接口

Consumes:
- CoreClrHost lifecycle、generated ABI

Produces:
- `NetCoreHostAdapter`
