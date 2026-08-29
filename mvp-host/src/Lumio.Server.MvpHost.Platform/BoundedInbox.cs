using System;
using System.Threading.Channels;

namespace Lumio.Server.MvpHost.Platform;

/// <summary>
/// 有界收件箱。容量来自构造时传入的 <see cref="QueueBudget"/>，满载<b>绝不阻塞</b>——
/// 阻塞会把背压变成死锁：生产者线程停在入队上，消费者线程等生产者让出的锁。
/// 满载一律同步返回 <see cref="EnqueueStatus.Full"/>，由调用方按自己的错误分类决定丢弃还是断连。
/// </summary>
internal sealed class BoundedInbox<T> : IBoundedInbox<T>
{
    // 必须有界：无界通道只是把背压问题推迟到 OOM，本仓不允许任何无界队列。
    private readonly Channel<T> _channel;

    // volatile：由 Close() 写、由任意生产者线程在 TryEnqueue 中读。
    // 读到陈旧值会把 Closed 误报成 Full，而两者的处置完全不同（丢弃 vs 停止接纳）。
    private volatile bool _isClosed;

    internal BoundedInbox(in QueueBudget budget)
    {
        if (budget.MaxItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), budget.MaxItems, "MaxItems 必须为正");
        }

        if (budget.MaxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), budget.MaxBytes, "MaxBytes 必须为正");
        }

        Budget = budget;
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(budget.MaxItems)
        {
            // 必须是 Wait：DropWrite / DropOldest 下 TryWrite 满载时**返回 true 并静默丢弃**，
            // 调用方拿到 Accepted 却什么都没入队——背压变成了看不见的丢包。
            // Wait 模式下 TryWrite 同样不阻塞，只是如实返回 false。
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    public QueueBudget Budget { get; }

    public int Count => _channel.Reader.Count;

    public EnqueueResult TryEnqueue(in T item)
    {
        // 已关闭的判定必须在写入之前：关闭后仍返回 Full 会让调用方以为「稍后重试即可」。
        if (_isClosed)
        {
            return new EnqueueResult(EnqueueStatus.Closed, "ContextClosing");
        }

        // payload 自带拷贝语义时先拷贝：调用方随后改写自己持有的缓冲，不得影响已入队的值。
        var stored = item is IDefensiveCopy<T> copyable ? copyable.DefensiveCopy() : item;

        if (_channel.Writer.TryWrite(stored))
        {
            return new EnqueueResult(EnqueueStatus.Accepted, null);
        }

        // 走到这里只剩两种成因：真的满了，或与 Close() 竞态。二者的处置不同，必须区分。
        return _isClosed
            ? new EnqueueResult(EnqueueStatus.Closed, "ContextClosing")
            : new EnqueueResult(EnqueueStatus.Full, "QueueFull");
    }

    public bool TryDequeue(out T item) => _channel.Reader.TryRead(out item!);

    public void Close()
    {
        _isClosed = true;
        _channel.Writer.TryComplete();
    }
}
