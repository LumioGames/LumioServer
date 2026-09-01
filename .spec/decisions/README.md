# Decisions(决策记录 · ADR)

用 ADR(Architecture Decision Record)记录决策:为什么这样调度、为什么定这种结构、为什么划这条边界。**本目录是全仓决策记录的唯一落点**——功能内决策与框架级决策都记这里,feature 文档只描述设计现状,不留决策记录。

> 跨仓公共语义的决策只在 `LumioGameEngineArchitecture` 维护；本目录仅记录 Server 内部实现决策，从 `0001` 开始编号。

## 怎么写一条 ADR

- 一个决策 = 一个文件 `NNNN-<slug>.md`,编号从 `0001` 递增;写完在下方索引加一行。
- **一旦记录不改写**:被推翻就新增一条,把旧的状态标成「被 NNNN 取代」,历史留痕。
- 无 frontmatter。格式照抄:

      # NNNN · <一句话决策>

      - 日期:YYYY-MM-DD
      - 状态:生效 | 被 NNNN 取代

      ## 背景
      面对什么问题。

      ## 决策
      定了什么。

      ## 后果
      接受了什么代价。

## 索引

| 编号 | 决策 | 状态 |
|------|------|------|
| [0001](0001-gate-review-remediation.md) | 按架构门审退回结论重构模块边界(聚合根收权 + 控制面收缩 + host-runtime 新设) | 生效 |
| [0002](0002-room-admission-host-binding-registry.md) | Room 准入做成 Host 绑定登记，不引入第二套 ECS | 生效 |
| [0003](0003-host-reconnect-window.md) | 五分钟重连窗由 Host Timer 持有，不用 Native Tick | 生效 |
