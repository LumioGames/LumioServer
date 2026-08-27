---
status: pending
---
# 同步模块 README 到实现映射与已知漂移

## 涉及范围

- **Wave：** 2
- **归属：** `repository`
- **唯一目标：** 只修正文档到实现映射，不改架构：消除 process 通用回调、coreclr 全调用线程表述、host-profiles 反向依赖和旧模块名残留。
- **文件集：
  - `modules/README.md`
  - `modules/process/README.md`
  - `modules/coreclr-host/README.md`
  - `modules/host-profiles/README.md`
  - `modules/persistence-host/README.md`

## 验收标准

- [ ] process 文档只允许具体 typed components/ports，无任意 lifecycle callback。
- [ ] coreclr 文档明确 bootstrap/control 与 Managed Tick owner thread 分离。
- [ ] host-profiles 声明零一等模块依赖；具体 factory mapping归 process。
- [ ] persistence 只引用 `maintenance-agent`，protocol-dispatch 明确无 Cargo/src/依赖者。
- [ ] 公共语义、字段、队列边、BaselineId不被改写；policy check通过。

## 依赖

- [`add-architecture-policy-xtask`](./add-architecture-policy-xtask.md)

## 接口

Consumes:
- 已冻结模块 README 与 modules/README优先级规则

Produces:
- 实现映射一致的 README集
