namespace Lumio.Server.MvpHost.Platform;

/// <summary>
/// 有界发件箱：只是目标收件箱的只写投影。
///
/// 它的存在是为了让跨模块事件<b>不产生反向程序集引用</b>——生产方只见
/// <see cref="IBoundedOutbox{T}"/>，消费方持有对应的 <see cref="IBoundedInbox{T}"/>，
/// 两端在组装根接线。这是「事件不产生反向边」的机制保证，不是纪律。
/// </summary>
internal sealed class BoundedOutbox<T> : IBoundedOutbox<T>
{
    private readonly IBoundedInbox<T> _target;

    internal BoundedOutbox(IBoundedInbox<T> target) => _target = target;

    public EnqueueResult TryPublish(in T item) => _target.TryEnqueue(in item);
}
