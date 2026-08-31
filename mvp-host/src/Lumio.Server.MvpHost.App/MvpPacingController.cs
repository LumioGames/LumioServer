#if MVP_HOST_FULL_GRAPH
using System;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.WorldSlot;

namespace Lumio.Server.MvpHost.App;

internal readonly record struct PacingTimerFired(ulong Generation);

/// <summary>
/// Converts monotonic timer deliveries into capacity-one WorldSlot permits.
/// A delayed timer schedules from the observed time, so pacing records an
/// overrun instead of accumulating catch-up work.
/// </summary>
internal sealed class MvpPacingController : IBoundedInbox<PacingTimerFired>, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(10);
    private static readonly QueueBudget TimerInboxBudget = new(1, 128);

    private readonly object gate = new();
    private readonly WorldSlotHost worldSlot;
    private readonly IMonotonicClock clock;
    private readonly ITimerService timers;
    private readonly IBoundedInbox<PacingTimerFired> inbox;
    private TimerId? scheduledTimer;
    private ulong generation;
    private ulong nextTick;
    private bool started;
    private bool disposed;

    internal MvpPacingController(
        WorldSlotHost worldSlot,
        IMonotonicClock clock,
        ITimerService timers)
    {
        ArgumentNullException.ThrowIfNull(worldSlot);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timers);
        this.worldSlot = worldSlot;
        this.clock = clock;
        this.timers = timers;
        this.inbox = PlatformModule.CreateInbox<PacingTimerFired>(in TimerInboxBudget);
    }

    public QueueBudget Budget => this.inbox.Budget;

    public int Count => this.inbox.Count;

    internal void Start()
    {
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (this.started)
            {
                return;
            }

            this.started = true;
            this.generation++;
            this.Schedule(this.clock.Now);
        }
    }

    public EnqueueResult TryEnqueue(in PacingTimerFired item)
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                return new EnqueueResult(EnqueueStatus.Closed, "ContextClosing");
            }

            var admitted = this.inbox.TryEnqueue(in item);
            if (admitted.Status != EnqueueStatus.Accepted)
            {
                return admitted;
            }

            if (!this.inbox.TryDequeue(out var fired)
                || fired.Generation != this.generation)
            {
                return new EnqueueResult(EnqueueStatus.Accepted, null);
            }

            this.scheduledTimer = null;
            if (this.worldSlot.State == WorldSlotHostState.Running
                && !this.worldSlot.IsPacingStopped)
            {
                var candidate = new LogicalTickToken(this.nextTick + 1);
                var permit = this.worldSlot.EnqueueTickPermit(candidate, this.worldSlot.Epoch);
                if (permit.Status == EnqueueStatus.Accepted)
                {
                    this.nextTick = candidate.Value;
                }
            }

            if (this.worldSlot.State == WorldSlotHostState.Running
                && !this.worldSlot.IsPacingStopped)
            {
                this.Schedule(new MonotonicInstant(this.clock.Now.Ticks + TickInterval.Ticks));
            }

            return new EnqueueResult(EnqueueStatus.Accepted, null);
        }
    }

    public bool TryDequeue(out PacingTimerFired item) => this.inbox.TryDequeue(out item);

    public void Close() => this.Dispose();

    public void Dispose()
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            if (this.scheduledTimer is { } timer)
            {
                _ = this.timers.Cancel(timer);
                this.scheduledTimer = null;
            }

            this.inbox.Close();
        }
    }

    private void Schedule(MonotonicInstant dueAt)
    {
        var command = new PacingTimerFired(this.generation);
        this.scheduledTimer = this.timers.Schedule(dueAt, this, command);
    }
}
#endif
