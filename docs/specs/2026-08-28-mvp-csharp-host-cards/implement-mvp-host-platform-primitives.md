---
status: pending
---

# 实现 host-runtime 等价最小面：单调时钟、Timer 类型化投递、有界端口、具名受监督线程

Rust 侧所有模块纪律（「任何模块不得自建 sleep / 轮询线程」「定时语义全部经 Timer 以命令投递实现」「全部线程经 host-runtime 受监督创建并具名」）都建立在 `modules/host-runtime` 之上。没有 C# 等价物，重连窗口、防重放窗口、ack 超时会散落成 `Task.Delay` 与自建轮询，将来替换成 Rust 时是结构性返工。本工程是全仓**唯一**允许出现等待 / 定时语义的地方。

设计出处：`docs/specs/2026-08-28-mvp-csharp-host-design.md` §6.0 与 §6.6 的 Platform 段。

## 涉及范围

- `mvp-host/src/Lumio.Server.MvpHost.Platform/**`（含 `mvp-host/src/Lumio.Server.MvpHost.Platform/queues.json`）
- `mvp-host/tests/Lumio.Server.MvpHost.Platform.Tests/**`

## 验收标准

- [ ] **先失败证据**：先提交 `Lumio.Server.MvpHost.Platform.Tests` 的全部测试（此时 `Lumio.Server.MvpHost.Platform` 只有空的类型骨架），执行 `cd mvp-host && dotnet test tests/Lumio.Server.MvpHost.Platform.Tests/Lumio.Server.MvpHost.Platform.Tests.csproj -c Release`，记录 `Failed!` 汇总行与失败用例数；实现完成后重跑记录 `Passed!  - Failed: 0`。两次输出写进交回物。
- [ ] `Lumio.Server.MvpHost.Platform` 工程声明 `<MvpHostLayer>1</MvpHostLayer>`，**零 `ProjectReference`**，唯一 `PackageReference` 是 `System.Threading.Channels`（不带 `Version`）。
- [ ] 公开以下类型与成员，签名逐字与设计 §6.0 / §6.6 相同：`MonotonicInstant(long Ticks)`、`IMonotonicClock { MonotonicInstant Now { get; } }`、`TimerId(ulong Value)`、`ITimerService { TimerId Schedule<TCommand>(MonotonicInstant dueAt, IBoundedInbox<TCommand> target, in TCommand command); bool Cancel(TimerId id); }`、`EnqueueStatus { Accepted, Full, Closed }`、`EnqueueResult(EnqueueStatus Status, string? StableErrorId)`、`QueueBudget(int MaxItems, long MaxBytes)`、`IBoundedInbox<T> { QueueBudget Budget { get; } EnqueueResult TryEnqueue(in T item); bool TryDequeue(out T item); int Count { get; } void Close(); }`、`IBoundedOutbox<T> { EnqueueResult TryPublish(in T item); }`、`IThreadBody { ThreadStepResult Step(CancellationToken ct); }`、`INamedThreadSupervisor { ThreadHandle Start(string name, IThreadBody body); bool TryDrainEvent(out SupervisionEvent evt); }`、`ThreadStepResult(bool Continue, string? StableErrorId)`、`ThreadHandle(string Name, int ManagedThreadId)`、`SupervisionEvent(string ThreadName, bool Faulted, string? StableErrorId)`、`IWallClock { string UtcIso8601Now(); }`。其中 `IWallClock` 的返回值必须匹配 `common.schema.json#/$defs/timestamp` 的正则 `^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,9})?Z$`（实测取自架构源 `schemas/common.schema.json` 的 `$defs.timestamp.pattern`），它是全仓唯一墙钟出口、与 `IMonotonicClock` 严格分域（设计 §6.0）。
- [ ] `ITimerService` 的实现只接受 `IBoundedInbox<TCommand>` 作为投递目标：`Schedule` 到期时把预置的 `TCommand` 投进目标收件箱。**不存在任何接受 `Action` / `Func<>` / `delegate` 参数的公开重载**——由一条反射测试 `TimerServiceTakesNoCallbackTest` 断言 `ITimerService` 与其实现类型的全部公开方法参数中不出现任何委托类型。
- [ ] 有界队列实现基于 `Channel.CreateBounded`，容量与字节上限来自构造时传入的 `QueueBudget`；满载时 `TryEnqueue` 返回 `EnqueueStatus.Full` 且**绝不阻塞**（测试 `BoundedInboxFullNeverBlocksTest` 用 `MaxItems=2` 填满后断言第三次调用在同步返回且 `Status == Full`）；`Close()` 之后 `TryEnqueue` 返回 `EnqueueStatus.Closed`，`TryDequeue` 仍能取出关闭前已入队的元素直到为空。
- [ ] 入队对引用型 payload 做防御性拷贝：测试 `BoundedInboxDefensiveCopyTest` 入队一个含 `ReadOnlyMemory<byte>` 字段的元素，随后改写调用方持有的底层数组，断言出队值不受影响。
- [ ] 本工程内的等待与墙钟收敛（**只做本工程内的两件事**）：① `Task.Delay` 的唯一使用点收敛到单个 internal 源文件，在该文件头注明它是全仓唯一等待语义落点；② 本工程内除 `IWallClock` 实现文件外**零** `System.DateTime` / `System.DateTimeOffset`，且**零** `Thread.Sleep`。验证方式为本工程内的源码扫描测试（逐文件统计命中数）加 `cd mvp-host && dotnet build src/Lumio.Server.MvpHost.Platform/Lumio.Server.MvpHost.Platform.csproj -c Release` 通过（只构建本工程，不用 `build.proj`——同 wave 的 `vendor-architecture-contracts-and-fixture-mirror` 的工程此刻可能尚未落地），两段输出写进交回物。**分析器接线不属本卡**：`Microsoft.CodeAnalysis.BannedApiAnalyzers` 必须逐工程引用才生效，「在所有生产工程生效」只能写在 `mvp-host/Directory.Build.props`，该文件归 `scaffold-mvp-host-build-baseline` 独占（`PackageReference` + `AdditionalFiles` + `Platform` 的 `IWallClock` 单点例外三件事一次写全），而本卡最后一条又要求 `git status --porcelain` 无其他路径，两者互斥。**全仓 `RS0030` 探针证据**下沉到 `implement-mvp-transport-core-and-bounded-queues`——本卡落地时 `Platform` 之外只有同 wave 并行的 `vendor-architecture-contracts-and-fixture-mirror` 的工程，往里塞探针即破坏 wave 2 的文件集互斥（设计 §7.4）。
- [ ] `INamedThreadSupervisor.Start` 创建的线程带传入的名字（测试 `NamedThreadCarriesNameTest` 断言 `Thread.CurrentThread.Name` 在 `IThreadBody.Step` 内等于传入名）；`Step` 抛出异常时线程终止并通过 `TryDrainEvent` 产出 `SupervisionEvent { Faulted = true }`；`Dispose` / 取消令牌触发时线程可 join 且不再产出事件。
- [ ] 单调性：测试 `MonotonicClockNeverGoesBackwardTest` 连续采样 10000 次断言序列非递减，且单调时钟实现**不读任何 wall clock**——`Platform` 内 `System.DateTimeOffset` 的唯一出现点是 `IWallClock` 的实现文件（由下一条 `WallClockShapeTest` 的单文件断言锁定），单调时钟实现文件不在其中；`Platform` 之外由禁用面保证。
- [ ] `WallClockShapeTest`：断言 `IWallClock.UtcIso8601Now()` 的返回值匹配 `^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,9})?Z$` 并以 `Z` 结尾；断言 `IWallClock` 与 `IMonotonicClock` 是**两个互不继承的接口**（任一方不可赋值给另一方）；断言 `Lumio.Server.MvpHost.Platform` 内使用 `System.DateTimeOffset` 的源文件**恰有一个**，即 `IWallClock` 的实现文件，且该文件头注释写明它是全仓唯一墙钟出口、**不得用于任何超时 / 窗口 / 间隔 / 顺序判定**（设计 §6.0）。存在理由同时写进注释：`logging-event.schema.json` 的 `required` 含 `timestamp` 且 `additionalProperties:false`，没有这个出口本仓产不出一条合法的 logging-event。
- [ ] `mvp-host/src/Lumio.Server.MvpHost.Platform/queues.json` 存在且是合法 JSON，为本工程提供的每一类有界队列原语写满七项合同字段：`owner` / `producer` / `consumer` / `ordering` / `budget` / `onFull` / `onClose`。本工程只提供队列原语、不持有任何具体业务队列，因此文件内容为 `{"queues": [], "note": "Platform 只提供 IBoundedInbox/IBoundedOutbox 原语，具体队列由各业务工程在自己的 queues.json 登记"}`。
- [ ] 全仓不存在 `Channel.CreateUnbounded` 与裸 `ConcurrentQueue` 的使用（本卡在自身工程内先落实，全仓聚合断言由 `define-mvp-host-contracts-and-audit-surface` 的架构测试承接）。
- [ ] `cd /Users/cui/LumioGames/LumioServer/mvp-host && bash eng/verify-all.sh` 退出码 0，末行 `MVP_HOST_VERIFY_OK`。
- [ ] `cd mvp-host && dotnet test tests/Lumio.Server.MvpHost.Platform.Tests/Lumio.Server.MvpHost.Platform.Tests.csproj -c Release --no-build` 输出 `Passed!  - Failed: 0`。
- [ ] 仓级收口门槛：`node .spec/tools/spec-lint.mjs` → `spec-lint: OK` 退出码 0；`node --test .spec/tools/spec-lint.test.mjs` → `fail 0` 退出码 0。
- [ ] 本卡只改动 `mvp-host/src/Lumio.Server.MvpHost.Platform/**` 与 `mvp-host/tests/Lumio.Server.MvpHost.Platform.Tests/**`；`git status --porcelain` 无其他路径（与同 wave 的 `vendor-architecture-contracts-and-fixture-mirror` 文件集交集为空）。
- [ ] 未越界实现任何 `mvp-host/absences.json` 列出的条目；未向该文件追加条目。

## 依赖

`scaffold-mvp-host-build-baseline`

## 接口

Consumes:

- 来自 `scaffold-mvp-host-build-baseline`：工程目录布局与 `<MvpHostLayer>` / `<MvpHostProductionProject>` 属性契约；`PackageReference` 不带 `Version`（版本在 `Directory.Packages.props`，已含 `System.Threading.Channels` 10.0.0）；`bash eng/verify-all.sh` 成功末行 `MVP_HOST_VERIFY_OK`；`mvp-host/eng/banned-public-api.txt` 已有且恰有四条禁用条目 `T:System.Net.Sockets.Socket` / `T:System.DateTime` / `T:System.DateTimeOffset` / `M:System.Threading.Thread.Sleep(System.Int32)`（`Task.Delay` **不在**表内，只受本卡「唯一落点在 `Platform` 内单个 internal 文件」的工程内断言约束）；以及该卡在 `mvp-host/Directory.Build.props` 中完成的 BannedApiAnalyzers 接线（`PackageReference` 不带 `Version` + `AdditionalFiles` 指向 `eng/banned-public-api.txt`）与 `Platform` 工程的 `IWallClock` 单点例外（工程级 `NoWarn` + 单文件 pragma，允许该实现文件使用 `System.DateTimeOffset`）。

Produces（命名空间 `Lumio.Server.MvpHost.Platform`，供 `HostContracts` 及其下游全部工程消费）:

- `public readonly record struct MonotonicInstant(long Ticks);`
- `public interface IMonotonicClock { MonotonicInstant Now { get; } }`
- `public interface IWallClock { string UtcIso8601Now(); }`（全仓唯一墙钟出口，与 `IMonotonicClock` 严格分域、互不继承；返回值匹配 `common.schema.json#/$defs/timestamp`，唯一用途是产出 `logging-event` 的 `timestamp` 字段，由 `Observability` 消费）
- `public readonly record struct TimerId(ulong Value);`
- `public interface ITimerService { TimerId Schedule<TCommand>(MonotonicInstant dueAt, IBoundedInbox<TCommand> target, in TCommand command); bool Cancel(TimerId id); }`
- `public enum EnqueueStatus { Accepted, Full, Closed }`
- `public readonly record struct EnqueueResult(EnqueueStatus Status, string? StableErrorId);`
- `public readonly record struct QueueBudget(int MaxItems, long MaxBytes);`
- `public interface IBoundedInbox<T> { QueueBudget Budget { get; } EnqueueResult TryEnqueue(in T item); bool TryDequeue(out T item); int Count { get; } void Close(); }`
- `public interface IBoundedOutbox<T> { EnqueueResult TryPublish(in T item); }`
- `public readonly record struct ThreadStepResult(bool Continue, string? StableErrorId);`
- `public readonly record struct ThreadHandle(string Name, int ManagedThreadId);`
- `public readonly record struct SupervisionEvent(string ThreadName, bool Faulted, string? StableErrorId);`
- `public interface IThreadBody { ThreadStepResult Step(System.Threading.CancellationToken ct); }`
- `public interface INamedThreadSupervisor { ThreadHandle Start(string name, IThreadBody body); bool TryDrainEvent(out SupervisionEvent evt); }`
- 具体实现的构造入口（下游组装根显式 `new` 时使用）：`public static class PlatformModule { public static IMonotonicClock CreateClock(); public static IWallClock CreateWallClock(); public static ITimerService CreateTimerService(IMonotonicClock clock); public static IBoundedInbox<T> CreateInbox<T>(in QueueBudget budget); public static IBoundedOutbox<T> CreateOutbox<T>(IBoundedInbox<T> target); public static INamedThreadSupervisor CreateThreadSupervisor(); }`
