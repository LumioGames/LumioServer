---
status: pending
---
# 实现 CoreCLR Generated ABI Facade 与 Thread Tokens

## 涉及范围

- **Wave：** 3
- **归属：** `coreclr-host`
- **唯一目标：** 只读消费Managed/Core generated contracts，定义host state、control/owner-thread token和无fault裁决的结果类型。
- **文件集：
  - `modules/coreclr-host/Cargo.toml`
  - `modules/coreclr-host/src/lib.rs`
  - `modules/coreclr-host/src/state.rs`
  - `modules/coreclr-host/src/contracts.rs`
  - `modules/coreclr-host/src/thread_affinity.rs`
  - `modules/coreclr-host/src/commands.rs`
  - `modules/coreclr-host/src/events.rs`
  - `modules/coreclr-host/src/error.rs`
  - `modules/coreclr-host/tests/abi_conformance_test.rs`
  - `modules/coreclr-host/tests/thread_affinity_test.rs`

## 验收标准

- [ ] ABI version/layout/table length/calling convention正反fixture通过。
- [ ] 不复制JSON/C ABI字段；生成crate hash不匹配硬失败。
- [ ] bootstrap/control token与Tick owner token类型/检查分离。
- [ ] 公开API无netcorehost/hostfxr pointer类型，unsafe尚未散布。
- [ ] CoreClrEvent可携raw invocation evidence/optional witness但不输出自判FaultClass。

## 依赖

- [`consume-upstream-generated-contract-artifacts`](./consume-upstream-generated-contract-artifacts.md)
- [`implement-host-runtime-bounded-ports`](./implement-host-runtime-bounded-ports.md)

## 接口

Consumes:
- generated NativeManagedAbi/Core contract

Produces:
- CoreClr state/commands/events/tokens、ABI validator
