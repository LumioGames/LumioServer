---
status: pending
---
# 实现 CoreCLR/Runtime/Gameplay Scope 生命周期与 Witness 转交

## 涉及范围

- **Wave：** 4
- **归属：** `coreclr-host`
- **唯一目标：** 建立纯生命周期reducer、ManagedTickPort、scope load/unload效果和异常/witness passthrough。
- **文件集：
  - `modules/coreclr-host/src/host.rs`
  - `modules/coreclr-host/src/runtime_scope.rs`
  - `modules/coreclr-host/src/gameplay_scope.rs`
  - `modules/coreclr-host/src/invocation.rs`
  - `modules/coreclr-host/src/fault.rs`
  - `modules/coreclr-host/tests/fault_passthrough_test.rs`
  - `modules/coreclr-host/tests/unload_soak_test.rs`

## 验收标准

- [ ] 部分初始化失败逆序释放；Load/Stop/Unload幂等。
- [ ] ManagedTickPort只接受OwnerThreadToken；错误线程在调用前拒绝。
- [ ] Managed异常无witness时只产生raw evidence；有witness逐字段原样转交。
- [ ] 重复scope load/unload soak后handle/task/timer证据归零或明确retained。
- [ ] 不创建线程或回调Gameplay。

## 依赖

- [`implement-coreclr-generated-abi-contract-facade`](./implement-coreclr-generated-abi-contract-facade.md)
- [`implement-host-runtime-clock-and-timer-delivery`](./implement-host-runtime-clock-and-timer-delivery.md)

## 接口

Consumes:
- CoreClr commands/tokens、generated Managed API

Produces:
- `CoreClrHost` lifecycle reducer、`ManagedTickPort`
