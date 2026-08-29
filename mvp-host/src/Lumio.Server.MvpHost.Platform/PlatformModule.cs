namespace Lumio.Server.MvpHost.Platform;

/// <summary>
/// 具体实现的构造入口。下游组装根显式 <c>new</c> 接线时只经本类，
/// 全仓没有 DI 容器、没有 service locator、没有全局 EventBus。
/// </summary>
public static class PlatformModule
{
    public static IMonotonicClock CreateClock() => new MonotonicClock();

    public static IWallClock CreateWallClock() => new SystemWallClock();

    public static ITimerService CreateTimerService(IMonotonicClock clock) => new MvpTimerService(clock);

    public static IBoundedInbox<T> CreateInbox<T>(in QueueBudget budget) => new BoundedInbox<T>(in budget);

    public static IBoundedOutbox<T> CreateOutbox<T>(IBoundedInbox<T> target) => new BoundedOutbox<T>(target);

    public static INamedThreadSupervisor CreateThreadSupervisor() => new NamedThreadSupervisor();
}
