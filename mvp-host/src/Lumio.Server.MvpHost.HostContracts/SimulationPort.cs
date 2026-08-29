using System;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.HostContracts;

/// <summary>
/// 宿主 ↔ Runtime 的唯一端口。**恰 6 个成员**，每逻辑 Tick 恰好一次跨界调用。
///
/// 签名里刻意不出现 <c>Phase</c> / <c>Clock</c> / <c>Revision</c> / <c>Commit</c> 这些词——
/// 它们属于 Runtime 内部的编排概念，出现在宿主端口上就意味着宿主开始复制 Runtime 的内部状态机。
/// 参数与返回类型也只来自 <c>HostContracts</c> / <c>Wire</c> / <c>Platform</c> / <c>System.*</c>，
/// 零 <c>Lumio.GameRuntime.*</c> 类型：Adapter 缺席时本端口仍然成立。
///
/// **不含** <c>TryApplyOpaqueMutation</c> 或任何 <c>Inject*</c> 成员——带外世界变更入口
/// 由独立的 <see cref="IWorldMutationSink"/> 承担，不给这个冻结端口开例外。
/// </summary>
public interface IWorldSimulationPort : IDisposable
{
    HostSimulationState State { get; }

    HostLifecycleResult Initialize(in HostSessionInit init);

    HostLifecycleResult Ready();

    HostTickOutcome RunTick(in HostTickRequest request);

    HostLifecycleResult Drain();

    HostLifecycleResult Snapshot(out ReadOnlyMemory<byte> opaqueSnapshot);
}

/// <summary>
/// 带外世界变更汇聚端口。**不经任何 Envelope、不经 <c>RunTick</c> 的 Ingress。**
///
/// 实现方只入队，由 Owner Thread 在下一次 <c>RunTick</c> 开头排空——因此它
/// **不违反**「每逻辑 Tick 恰好一次跨界调用」，也不破坏「Owner Thread 是唯一
/// 触碰仿真状态的线程」这两条不变量。只在 <c>--enable-test-control</c> 下被装配。
///
/// 依赖方向：Layer 2 定义接口 → Layer 4 实现 → Layer 6 组装根注入，<c>Session</c> 只见接口。
/// </summary>
public interface IWorldMutationSink
{
    EnqueueResult TryEnqueueOpaqueMutation(ReadOnlyMemory<byte> opaqueCommand);
}
