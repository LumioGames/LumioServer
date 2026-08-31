using System;
using System.Reflection;
using System.Threading;
using Lumio.Server.MvpHost.Platform;
using Xunit;

namespace Lumio.Server.MvpHost.Platform.Tests;

public sealed class TimerDeliveryRetryTests
{
    [Fact]
    public void ScheduleCannotRegisterAfterDisposalWinsTheRegistrationLock()
    {
        var clock = new ManualMonotonicClock();
        var timers = PlatformModule.CreateTimerService(clock);
        IBoundedInbox<string> target =
            PlatformModule.CreateInbox<string>(new QueueBudget(1, 1024));
        var serviceType = timers.GetType();
        var registrationGate = Assert.IsType<Lock>(serviceType
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(timers));
        var disposedField = serviceType.GetField(
            "_disposed",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var scheduleStarted = new ManualResetEventSlim(false);
        Exception? scheduleError = null;
        var scheduleThread = new Thread(() =>
        {
            scheduleStarted.Set();
            try
            {
                _ = timers.Schedule(new MonotonicInstant(1), target, "late");
            }
            catch (Exception error)
            {
                scheduleError = error;
            }
        });

        try
        {
            using (registrationGate.EnterScope())
            {
                scheduleThread.Start();
                Assert.True(scheduleStarted.Wait(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken));
                Assert.True(
                    SpinWait.SpinUntil(
                        () => (scheduleThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                        TimeSpan.FromSeconds(5)),
                    "Schedule did not block at the registration lock");
                disposedField.SetValue(timers, true);
            }

            Assert.True(scheduleThread.Join(TimeSpan.FromSeconds(5)));
            Assert.IsType<ObjectDisposedException>(scheduleError);
        }
        finally
        {
            disposedField.SetValue(timers, false);
            timers.Dispose();
        }
    }

    [Fact]
    public void CancelledDueTimerIsNotDeliveredFromAnEarlierSnapshot()
    {
        var clock = new ManualMonotonicClock();
        IBoundedInbox<string> blockingTarget =
            PlatformModule.CreateInbox<string>(new QueueBudget(1, 1024));
        using var blocker = new BlockingInbox<string>(blockingTarget);
        using var timers = PlatformModule.CreateTimerService(clock);
        IBoundedInbox<string> cancelledTarget =
            PlatformModule.CreateInbox<string>(new QueueBudget(1, 1024));
        var cancelled = new ObservedInbox<string>(cancelledTarget);
        IBoundedInbox<string> barrier = PlatformModule.CreateInbox<string>(new QueueBudget(1, 1024));

        timers.Schedule(new MonotonicInstant(1), blocker, "blocker");
        var cancelledTimer = timers.Schedule(new MonotonicInstant(1), cancelled, "cancelled");

        try
        {
            clock.AdvanceTo(1);
            Assert.True(
                blocker.WaitForDeliveryAttempt(),
                "The leading due delivery did not enter the blocking inbox");
            Assert.True(
                timers.Cancel(cancelledTimer),
                "The snapshotted timer must still be cancellable before it claims delivery");

            blocker.ReleaseDelivery();
            timers.Schedule(clock.Now, barrier, "barrier");
            Assert.True(
                TimerServiceTests.SpinUntil(() => barrier.Count > 0),
                "The timer pump did not advance beyond the cancelled snapshot");

            Assert.Equal(0, cancelled.EnqueueAttempts);
            Assert.Equal(0, cancelled.Count);
        }
        finally
        {
            blocker.ReleaseDelivery();
        }
    }

    [Fact]
    public void DueDeliveryRetriesWhileFullAndIsRemovedAfterAcceptance()
    {
        var clock = new ManualMonotonicClock();
        using var timers = PlatformModule.CreateTimerService(clock);
        IBoundedInbox<ObservedCommand> target =
            PlatformModule.CreateInbox<ObservedCommand>(new QueueBudget(1, 1024));
        IBoundedInbox<string> barrier = PlatformModule.CreateInbox<string>(new QueueBudget(1, 1024));
        var blockerAttempts = new AttemptCounter();
        var deliveryAttempts = new AttemptCounter();

        Assert.Equal(
            EnqueueStatus.Accepted,
            target.TryEnqueue(new ObservedCommand("blocker", blockerAttempts, ThrowOnCopy: false)).Status);
        var timer = timers.Schedule(
            new MonotonicInstant(1),
            target,
            new ObservedCommand("due", deliveryAttempts, ThrowOnCopy: false));
        clock.AdvanceTo(1);

        Assert.True(
            TimerServiceTests.SpinUntil(() => deliveryAttempts.Value >= 2),
            $"A due delivery that observed Full was not retried; attempts={deliveryAttempts.Value}");
        Assert.True(target.TryDequeue(out var blocker));
        Assert.Equal("blocker", blocker.Value);

        Assert.True(
            TimerServiceTests.SpinUntil(() => target.Count > 0),
            "The retained due delivery was not accepted after capacity became available");
        Assert.True(target.TryDequeue(out var delivered));
        Assert.Equal("due", delivered.Value);

        timers.Schedule(clock.Now, barrier, "barrier");
        Assert.True(
            TimerServiceTests.SpinUntil(() => barrier.Count > 0),
            "The timer pump did not continue after the accepted delivery");
        Assert.False(timers.Cancel(timer), "An accepted delivery must no longer remain pending");
    }

    [Fact]
    public void DueDeliveryIsRemovedWhenTargetIsClosed()
    {
        var clock = new ManualMonotonicClock();
        using var timers = PlatformModule.CreateTimerService(clock);
        IBoundedInbox<ObservedCommand> closedTarget =
            PlatformModule.CreateInbox<ObservedCommand>(new QueueBudget(1, 1024));
        var closed = new ObservedInbox<ObservedCommand>(closedTarget);
        IBoundedInbox<string> barrier = PlatformModule.CreateInbox<string>(new QueueBudget(1, 1024));
        var attempts = new AttemptCounter();
        closedTarget.Close();

        var timer = timers.Schedule(
            new MonotonicInstant(1),
            closed,
            new ObservedCommand("closed", attempts, ThrowOnCopy: false));
        clock.AdvanceTo(1);

        Assert.True(
            TimerServiceTests.SpinUntil(() => closed.EnqueueAttempts > 0),
            "The due delivery never observed the closed inbox");
        timers.Schedule(clock.Now, barrier, "barrier");
        Assert.True(
            TimerServiceTests.SpinUntil(() => barrier.Count > 0),
            "The timer pump did not continue after the Closed result");
        Assert.Equal(1, closed.EnqueueAttempts);
        Assert.Equal(0, attempts.Value);
        Assert.False(timers.Cancel(timer), "A Closed delivery must no longer remain pending");
    }

    [Fact]
    public void ThrowingDeliveryIsRemovedAndDoesNotRetry()
    {
        var clock = new ManualMonotonicClock();
        using var timers = PlatformModule.CreateTimerService(clock);
        IBoundedInbox<ObservedCommand> target =
            PlatformModule.CreateInbox<ObservedCommand>(new QueueBudget(1, 1024));
        IBoundedInbox<string> barrier = PlatformModule.CreateInbox<string>(new QueueBudget(1, 1024));
        var attempts = new AttemptCounter();

        var timer = timers.Schedule(
            new MonotonicInstant(1),
            target,
            new ObservedCommand("throw", attempts, ThrowOnCopy: true));
        clock.AdvanceTo(1);
        Assert.True(
            TimerServiceTests.SpinUntil(() => attempts.Value > 0),
            "The throwing delivery was never attempted");

        timers.Schedule(clock.Now, barrier, "barrier");
        Assert.True(
            TimerServiceTests.SpinUntil(() => barrier.Count > 0),
            "The timer pump did not continue after the delivery exception");
        Assert.Equal(1, attempts.Value);
        Assert.False(timers.Cancel(timer), "A throwing delivery must no longer remain pending");
    }

    private sealed class ManualMonotonicClock : IMonotonicClock
    {
        private long ticks;

        public MonotonicInstant Now => new(Interlocked.Read(ref this.ticks));

        internal void AdvanceTo(long value) => Interlocked.Exchange(ref this.ticks, value);
    }

    private sealed class AttemptCounter
    {
        private int value;

        internal int Value => Volatile.Read(ref this.value);

        internal void Increment() => Interlocked.Increment(ref this.value);
    }

    private sealed class ObservedInbox<T>(IBoundedInbox<T> inner) : IBoundedInbox<T>
    {
        private int enqueueAttempts;

        internal int EnqueueAttempts => Volatile.Read(ref this.enqueueAttempts);

        public QueueBudget Budget => inner.Budget;

        public int Count => inner.Count;

        public EnqueueResult TryEnqueue(in T item)
        {
            Interlocked.Increment(ref this.enqueueAttempts);
            return inner.TryEnqueue(in item);
        }

        public bool TryDequeue(out T item) => inner.TryDequeue(out item!);

        public void Close() => inner.Close();
    }

    private sealed class BlockingInbox<T>(IBoundedInbox<T> inner) : IBoundedInbox<T>, IDisposable
    {
        private readonly ManualResetEventSlim deliveryAttempted = new(false);
        private readonly ManualResetEventSlim deliveryReleased = new(false);

        public QueueBudget Budget => inner.Budget;

        public int Count => inner.Count;

        internal bool WaitForDeliveryAttempt() =>
            this.deliveryAttempted.Wait(TimeSpan.FromSeconds(5));

        internal void ReleaseDelivery() => this.deliveryReleased.Set();

        public EnqueueResult TryEnqueue(in T item)
        {
            this.deliveryAttempted.Set();
            this.deliveryReleased.Wait();
            return inner.TryEnqueue(in item);
        }

        public bool TryDequeue(out T item) => inner.TryDequeue(out item!);

        public void Close() => inner.Close();

        public void Dispose()
        {
            this.deliveryAttempted.Dispose();
            this.deliveryReleased.Dispose();
        }
    }

    private readonly record struct ObservedCommand(
        string Value,
        AttemptCounter Attempts,
        bool ThrowOnCopy) : IDefensiveCopy<ObservedCommand>
    {
        public ObservedCommand DefensiveCopy()
        {
            this.Attempts.Increment();
            if (this.ThrowOnCopy)
            {
                throw new InvalidOperationException("copy failed");
            }

            return this;
        }
    }
}
