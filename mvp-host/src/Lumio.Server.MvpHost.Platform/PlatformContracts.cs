using System;
using System.Threading;

namespace Lumio.Server.MvpHost.Platform;

/// <summary>
/// 单调时刻。与墙钟严格分域：只用于超时、窗口、间隔与顺序判定。
/// </summary>
/// <remarks>
/// <b><see cref="Ticks"/> 的单位是 <see cref="TimeSpan"/> tick（100 ns，即
/// <see cref="TimeSpan.TicksPerSecond"/> = 10,000,000），不是 <c>Stopwatch</c> 的原始计数。</b>
/// 因此 <c>new MonotonicInstant(clock.Now.Ticks + TimeSpan.FromSeconds(30).Ticks)</c>
/// 恒表示「30 秒后」，跨平台一致。这条单位约定是契约的一部分，实现不得改用其他刻度。
/// </remarks>
public readonly record struct MonotonicInstant(long Ticks);

/// <summary>单调时钟。不读任何墙钟，进程内单调不回退。</summary>
public interface IMonotonicClock
{
    MonotonicInstant Now { get; }
}

/// <summary>
/// 全仓唯一墙钟出口。返回值匹配架构源 <c>common.schema.json#/$defs/timestamp</c>，
/// 唯一用途是产出 <c>logging-event</c> 的 <c>timestamp</c> 字段。
/// <b>不得用于任何超时 / 窗口 / 间隔 / 顺序判定</b>——那些一律走 <see cref="IMonotonicClock"/>。
/// </summary>
public interface IWallClock
{
    string UtcIso8601Now();
}

public readonly record struct TimerId(ulong Value);

/// <summary>
/// 定时服务。到期时把<b>预置的类型化命令</b>投进目标收件箱——
/// 刻意不接受任何委托 / 回调，否则等待语义会随回调散播到各模块。
/// </summary>
public interface ITimerService : IDisposable
{
    TimerId Schedule<TCommand>(MonotonicInstant dueAt, IBoundedInbox<TCommand> target, in TCommand command);

    bool Cancel(TimerId id);
}

public enum EnqueueStatus
{
    Accepted,
    Full,
    Closed,
}

public readonly record struct EnqueueResult(EnqueueStatus Status, string? StableErrorId);

/// <summary>队列预算。</summary>
/// <remarks>
/// <b><see cref="MaxBytes"/> 在 MVP 期只是 provisional 声明值，不被执行</b>（登记项，非疏漏）：
/// 泛型队列无法在不知道 payload 编码的前提下计量字节。<see cref="MaxItems"/> 是唯一真实生效的上限。
/// 需要字节级背压的下游（ingress / egress）必须在自己的层面计量，
/// 不得假设本队列会按 <see cref="MaxBytes"/> 封顶。
/// </remarks>
public readonly record struct QueueBudget(int MaxItems, long MaxBytes);

/// <summary>
/// 入队时希望做防御性拷贝的 payload 由自身实现本接口。
/// 泛型队列无法在不反射的前提下自动深拷任意 <c>T</c>；由 payload 显式声明拷贝语义，
/// 既避免反射魔法，也让「这个类型持有外部拥有的缓冲」成为类型上可见的事实。
/// </summary>
/// <remarks>
/// <b>已知缺口（登记项，非疏漏）</b>：防御性拷贝因此是 payload <b>opt-in</b> 而非队列无条件执行。
/// 一个持有外部缓冲却忘记实现本接口的 payload 类型<b>得不到任何保护</b>，且没有任何机制会提示——
/// 不编译错、不警告、不失败，只在运行时表现为「入队后调用方改写缓冲，队列里的值跟着变」。
/// 之所以不用 <c>where T : IDefensiveCopy&lt;T&gt;</c> 把缺口移到编译期：那会让
/// <c>IBoundedInbox&lt;int&gt;</c> 这类原始 payload 的队列无法成立，而本仓大量控制命令队列正是这种。
/// 新增持有 <see cref="ReadOnlyMemory{T}"/> / 数组 / 其他可变引用的 payload 类型时，
/// <b>必须</b>实现本接口——这条属评审项。
/// </remarks>
public interface IDefensiveCopy<out T>
{
    T DefensiveCopy();
}

public interface IBoundedInbox<T>
{
    QueueBudget Budget { get; }

    EnqueueResult TryEnqueue(in T item);

    bool TryDequeue(out T item);

    int Count { get; }

    void Close();
}

public interface IBoundedOutbox<T>
{
    EnqueueResult TryPublish(in T item);
}

public readonly record struct ThreadStepResult(bool Continue, string? StableErrorId);

public readonly record struct ThreadHandle(string Name, int ManagedThreadId);

public readonly record struct SupervisionEvent(string ThreadName, bool Faulted, string? StableErrorId);

// CA1716：Step 是 VB 的保留字。此处定点抑制而非改名——该签名由设计 §6.6 逐字定死，
// 是跨模块契约面的一部分，改名会让下游卡的「签名逐字相同」验收不可满足；
// 且本仓不存在 VB 消费方。抑制范围只覆盖这一个接口，不放宽全工程。
#pragma warning disable CA1716
public interface IThreadBody
{
    ThreadStepResult Step(CancellationToken ct);
}
#pragma warning restore CA1716

public interface INamedThreadSupervisor : IDisposable
{
    ThreadHandle Start(string name, IThreadBody body);

    bool TryDrainEvent(out SupervisionEvent evt);
}
