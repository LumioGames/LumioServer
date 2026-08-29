using System;
using System.Collections.Generic;
using System.Threading;

namespace Lumio.Server.MvpHost.Platform;

/// <summary>
/// 定时服务。到期时把<b>预置的类型化命令</b>投进目标收件箱。
///
/// 刻意不接受任何委托：`ITimerService` 的公开面上没有 <c>Action</c> / <c>Func</c> / delegate 参数
/// （由 <c>TimerServiceTakesNoCallbackTest</c> 反射断言）。回调一旦被允许，等待与重入语义
/// 就会随回调散播进各模块，而本工程存在的意义正是把它们收在一处。
///
/// 驱动方式是一条具名受监督线程按单调时钟轮询到期项——不是墙钟，因此不受系统时间调整影响。
/// </summary>
internal sealed class MvpTimerService : ITimerService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(1);

    private readonly IMonotonicClock _clock;
    private readonly NamedThreadSupervisor _supervisor;
    private readonly Dictionary<ulong, PendingTimer> _pending = [];
    private readonly Lock _gate = new();
    private ulong _nextId;
    private bool _disposed;

    internal MvpTimerService(IMonotonicClock clock)
    {
        _clock = clock;
        _supervisor = new NamedThreadSupervisor();
        _supervisor.Start("platform-timer", new TimerBody(this));
    }

    public TimerId Schedule<TCommand>(MonotonicInstant dueAt, IBoundedInbox<TCommand> target, in TCommand command)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 命令在 Schedule 期就被捕获成一个无参投递闭包的等价物（一个小状态对象），
        // 因此到期路径上没有任何类型擦除的委托暴露到公开面。
        var entry = new TypedDelivery<TCommand>(target, command);

        lock (_gate)
        {
            var id = ++_nextId;
            _pending[id] = new PendingTimer(dueAt, entry);
            return new TimerId(id);
        }
    }

    public bool Cancel(TimerId id)
    {
        lock (_gate)
        {
            return _pending.Remove(id.Value);
        }
    }

    private void PumpDueTimers()
    {
        var now = _clock.Now;
        List<ITypedDelivery>? due = null;

        lock (_gate)
        {
            List<ulong>? fired = null;
            foreach (var (id, timer) in _pending)
            {
                if (timer.DueAt.Ticks <= now.Ticks)
                {
                    (fired ??= []).Add(id);
                    (due ??= []).Add(timer.Delivery);
                }
            }

            if (fired is not null)
            {
                foreach (var id in fired)
                {
                    _pending.Remove(id);
                }
            }
        }

        if (due is null)
        {
            return;
        }

        // 投递在锁外进行：目标收件箱满载时返回 Full 而不阻塞，但仍不该把定时器的锁一起拖住。
        //
        // 逐条隔离是必须的：Deliver 会调到 payload 的 IDefensiveCopy.DefensiveCopy()，即下游用户代码。
        // 若让异常穿透到线程体，platform-timer 线程即刻死亡，此后每次 Schedule 照常返回合法 TimerId
        // 但永不投递——重连窗口、防重放窗口、ack 超时全部静默失效，且无异常、无日志。
        // 一个坏 payload 只许影响它自己那一条定时器。
        foreach (var delivery in due)
        {
            try
            {
                delivery.Deliver();
            }
#pragma warning disable CA1031 // 隔离边界：故意捕获一切，理由见上。
            catch (Exception)
#pragma warning restore CA1031
            {
                // 已知缺口（登记项）：Layer 1 的 Platform 零依赖，没有诊断汇聚面可写，
                // 因此这条失败在 MVP 期**不上报任何地方**。保住整个定时子系统不死，
                // 优先于报告单条投递失败。Rust 侧 host-runtime 落地后由其监督面承接。
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _supervisor.Dispose();

        lock (_gate)
        {
            _pending.Clear();
        }
    }

    private sealed class TimerBody : IThreadBody
    {
        private readonly MvpTimerService _owner;

        internal TimerBody(MvpTimerService owner) => _owner = owner;

        public ThreadStepResult Step(CancellationToken ct)
        {
            _owner.PumpDueTimers();
            PlatformWait.Block(TickInterval, ct);
            return new ThreadStepResult(true, null);
        }
    }

    private sealed record PendingTimer(MonotonicInstant DueAt, ITypedDelivery Delivery);

    private interface ITypedDelivery
    {
        void Deliver();
    }

    private sealed class TypedDelivery<TCommand> : ITypedDelivery
    {
        private readonly IBoundedInbox<TCommand> _target;
        private readonly TCommand _command;

        internal TypedDelivery(IBoundedInbox<TCommand> target, in TCommand command)
        {
            _target = target;
            _command = command;
        }

        public void Deliver() => _target.TryEnqueue(in _command);
    }
}
