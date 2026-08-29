using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Lumio.Server.MvpHost.Platform;
using Xunit;

namespace Lumio.Server.MvpHost.Platform.Tests;

public sealed class BoundedQueueTests
{
    [Fact]
    public void BoundedInboxFullNeverBlocksTest()
    {
        IBoundedInbox<int> inbox = PlatformModule.CreateInbox<int>(new QueueBudget(2, 1024));

        Assert.Equal(EnqueueStatus.Accepted, inbox.TryEnqueue(1).Status);
        Assert.Equal(EnqueueStatus.Accepted, inbox.TryEnqueue(2).Status);

        // 满载必须同步返回 Full，绝不阻塞：用一条独立线程加超时来证明「同步返回」不是靠运气。
        EnqueueResult third = default;
        var done = new ManualResetEventSlim(false);
        var t = new Thread(() => { third = inbox.TryEnqueue(3); done.Set(); }) { IsBackground = true };
        t.Start();

        Assert.True(done.Wait(2_000, TestContext.Current.CancellationToken), "TryEnqueue 在满载时阻塞了");
        Assert.Equal(EnqueueStatus.Full, third.Status);
        Assert.Equal("QueueFull", third.StableErrorId);
        Assert.Equal(2, inbox.Count);
    }

    [Fact]
    public void ClosedInboxRejectsEnqueueButStillDrainsTest()
    {
        IBoundedInbox<int> inbox = PlatformModule.CreateInbox<int>(new QueueBudget(4, 1024));
        inbox.TryEnqueue(7);
        inbox.TryEnqueue(8);

        inbox.Close();

        Assert.Equal(EnqueueStatus.Closed, inbox.TryEnqueue(9).Status);

        // 关闭前已入队的元素仍能取出，直到为空。
        Assert.True(inbox.TryDequeue(out var a));
        Assert.Equal(7, a);
        Assert.True(inbox.TryDequeue(out var b));
        Assert.Equal(8, b);
        Assert.False(inbox.TryDequeue(out _));
    }

    [Fact]
    public void BoundedInboxDefensiveCopyTest()
    {
        IBoundedInbox<PayloadItem> inbox = PlatformModule.CreateInbox<PayloadItem>(new QueueBudget(4, 4096));

        var backing = new byte[] { 1, 2, 3, 4 };
        Assert.Equal(EnqueueStatus.Accepted, inbox.TryEnqueue(new PayloadItem(backing)).Status);

        // 调用方改写自己持有的底层数组，出队值必须不受影响。
        backing[0] = 0xFF;
        backing[3] = 0xFF;

        Assert.True(inbox.TryDequeue(out var got));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, got.Payload.ToArray());
    }

    [Fact]
    public void OutboxPublishesIntoTargetInboxTest()
    {
        IBoundedInbox<int> inbox = PlatformModule.CreateInbox<int>(new QueueBudget(2, 1024));
        IBoundedOutbox<int> outbox = PlatformModule.CreateOutbox(inbox);

        Assert.Equal(EnqueueStatus.Accepted, outbox.TryPublish(11).Status);
        Assert.True(inbox.TryDequeue(out var v));
        Assert.Equal(11, v);
    }

    [Fact]
    public void BudgetIsExposedVerbatimTest()
    {
        var budget = new QueueBudget(9, 512);
        IBoundedInbox<int> inbox = PlatformModule.CreateInbox<int>(budget);
        Assert.Equal(budget, inbox.Budget);
    }

    private readonly record struct PayloadItem(ReadOnlyMemory<byte> Payload) : IDefensiveCopy<PayloadItem>
    {
        public PayloadItem DefensiveCopy() => new(Payload.ToArray());
    }
}

public sealed class ClockTests
{
    [Fact]
    public void MonotonicClockNeverGoesBackwardTest()
    {
        IMonotonicClock clock = PlatformModule.CreateClock();

        var previous = clock.Now;
        for (var i = 0; i < 10_000; i++)
        {
            var current = clock.Now;
            Assert.True(current.Ticks >= previous.Ticks, $"单调时钟回退：{previous.Ticks} → {current.Ticks}");
            previous = current;
        }
    }

    [Fact]
    public void MonotonicInstantTicksAreTimeSpanTicksTest()
    {
        // 单位纪律：Ticks 必须是 TimeSpan tick（100 ns），不是 Stopwatch 原始计数。
        // 二者在 macOS/Linux 上差 100 倍（Stopwatch.Frequency=1e9 vs TicksPerSecond=1e7），
        // 在 Windows 上恰好相等——所以只在 Windows 上测会漏掉这个平台相关的静默偏差。
        IMonotonicClock clock = PlatformModule.CreateClock();

        var before = clock.Now;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 50)
        {
            Thread.SpinWait(1_000);
        }

        sw.Stop();
        var after = clock.Now;

        var measured = TimeSpan.FromTicks(after.Ticks - before.Ticks);

        // 允许宽松区间（调度抖动），但足以把 100 倍的单位错误钉死：
        // 若 Ticks 是 Stopwatch 计数，measured 会是真实耗时的 100 倍。
        Assert.InRange(measured.TotalMilliseconds, 25d, 2_000d);
    }

    [Fact]
    public void WallClockShapeTest()
    {
        IWallClock wallClock = PlatformModule.CreateWallClock();
        var now = wallClock.UtcIso8601Now();

        // 取自架构源 schemas/common.schema.json 的 $defs.timestamp.pattern。
        const string TimestampPattern =
            @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,9})?Z$";
        Assert.Matches(TimestampPattern, now);
        Assert.EndsWith("Z", now, StringComparison.Ordinal);

        // IWallClock 与 IMonotonicClock 严格分域：任一方都不可赋值给另一方。
        Assert.False(typeof(IMonotonicClock).IsAssignableFrom(typeof(IWallClock)));
        Assert.False(typeof(IWallClock).IsAssignableFrom(typeof(IMonotonicClock)));
    }

    [Fact]
    public void WallClockIsTheOnlyDateTimeOffsetFileTest()
    {
        var offenders = PlatformSources.FilesContaining("System.DateTimeOffset", "DateTimeOffset");
        Assert.Single(offenders);
        Assert.Equal("SystemWallClock.cs", offenders[0]);

        // 全仓唯一墙钟出口的存在理由必须写在文件头，否则下一个人会把它当普通时钟用。
        var text = PlatformSources.ReadAll()["SystemWallClock.cs"];
        Assert.Contains("不得用于任何超时", text, StringComparison.Ordinal);
        Assert.Contains("全仓唯一墙钟出口", text, StringComparison.Ordinal);
        Assert.Contains("logging-event", text, StringComparison.Ordinal);
    }
}

public sealed class TimerServiceTests
{
    [Fact]
    public void TimerServiceTakesNoCallbackTest()
    {
        // Timer 只按「到期投递一条预置命令」工作；接受委托就等于把回调语义散播到各模块。
        using var probe = PlatformModule.CreateTimerService(PlatformModule.CreateClock());
        foreach (var type in new[] { typeof(ITimerService), probe.GetType() })
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var parameter in method.GetParameters())
                {
                    Assert.False(
                        typeof(Delegate).IsAssignableFrom(parameter.ParameterType),
                        $"{type.Name}.{method.Name} 的参数 {parameter.Name} 是委托类型 {parameter.ParameterType}");
                }
            }
        }
    }

    [Fact]
    public void ScheduleDeliversTypedCommandToInboxTest()
    {
        IMonotonicClock clock = PlatformModule.CreateClock();
        using var timers = PlatformModule.CreateTimerService(clock);
        IBoundedInbox<string> target = PlatformModule.CreateInbox<string>(new QueueBudget(4, 1024));

        timers.Schedule(new MonotonicInstant(clock.Now.Ticks), target, "fired");

        Assert.True(SpinUntil(() => target.Count > 0), "到期命令未被投递");
        Assert.True(target.TryDequeue(out var got));
        Assert.Equal("fired", got);
    }

    [Fact]
    public void CancelledTimerNeverDeliversTest()
    {
        IMonotonicClock clock = PlatformModule.CreateClock();
        using var timers = PlatformModule.CreateTimerService(clock);
        IBoundedInbox<string> target = PlatformModule.CreateInbox<string>(new QueueBudget(4, 1024));

        // MonotonicInstant.Ticks 的单位是 TimeSpan tick（100 ns），因此这里确实是 30 秒后。
        // 若实现改用 Stopwatch 原始计数，本机（Stopwatch.Frequency = 1e9）会静默变成 0.3 秒。
        var id = timers.Schedule(new MonotonicInstant(clock.Now.Ticks + TimeSpan.FromSeconds(30).Ticks), target, "late");
        Assert.True(timers.Cancel(id));
        Assert.False(timers.Cancel(id), "重复取消必须返回 false");

        Assert.Equal(0, target.Count);
    }

    [Fact]
    public void ThrowingPayloadDoesNotKillTimerThreadTest()
    {
        // 投递路径会调到 payload 的 DefensiveCopy()，即下游用户代码。
        // 若异常穿透到线程体，platform-timer 线程即刻死亡，此后 Schedule 照常返回合法 TimerId
        // 却永不投递——所有窗口与超时静默失效。一个坏 payload 只许影响它自己那一条。
        IMonotonicClock clock = PlatformModule.CreateClock();
        using var timers = PlatformModule.CreateTimerService(clock);

        IBoundedInbox<ExplodingPayload> poisoned = PlatformModule.CreateInbox<ExplodingPayload>(new QueueBudget(4, 1024));
        timers.Schedule(new MonotonicInstant(clock.Now.Ticks), poisoned, default);

        // 先证明坏 payload 的投递确实跑到并抛了——否则下面的断言会因为「压根没投递」而假通过。
        Assert.True(SpinUntil(() => Volatile.Read(ref _explodeCount) > 0), "坏 payload 的投递从未发生");

        // 关键断言：定时器线程仍然活着，后续定时器照常投递。
        IBoundedInbox<string> healthy = PlatformModule.CreateInbox<string>(new QueueBudget(4, 1024));
        timers.Schedule(new MonotonicInstant(clock.Now.Ticks), healthy, "still-alive");

        Assert.True(SpinUntil(() => healthy.Count > 0), "坏 payload 之后定时器线程已死");
    }

    private static int _explodeCount;

    private readonly record struct ExplodingPayload : IDefensiveCopy<ExplodingPayload>
    {
        public ExplodingPayload DefensiveCopy()
        {
            Interlocked.Increment(ref _explodeCount);
            throw new InvalidOperationException("payload 拷贝失败");
        }
    }

    internal static bool SpinUntil(Func<bool> condition)
    {
        // SpinWait 会自行退避到让核（而非纯 Thread.Yield 空转烧核）——
        // 慢 CI / 单 vCPU 容器上后者既 flaky 又和被测线程抢 CPU。
        // 注意不能用 Thread.Sleep：它在本仓是分析器禁用面（测试工程也被拦）。
        var deadline = Environment.TickCount64 + 5_000;
        var spin = new SpinWait();
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            spin.SpinOnce();
        }

        return condition();
    }
}

public sealed class NamedThreadSupervisorTests
{
    [Fact]
    public void NamedThreadCarriesNameTest()
    {
        using var supervisor = PlatformModule.CreateThreadSupervisor();
        var body = new RecordingBody();

        var handle = supervisor.Start("worldslot-7", body);

        Assert.True(TimerServiceTests.SpinUntil(() => body.ObservedName is not null));
        Assert.Equal("worldslot-7", body.ObservedName);
        Assert.Equal("worldslot-7", handle.Name);
        Assert.NotEqual(0, handle.ManagedThreadId);
    }

    [Fact]
    public void ThrowingStepProducesFaultedSupervisionEventTest()
    {
        using var supervisor = PlatformModule.CreateThreadSupervisor();
        supervisor.Start("faulting", new ThrowingBody());

        SupervisionEvent evt = default;
        Assert.True(TimerServiceTests.SpinUntil(() => supervisor.TryDrainEvent(out evt)));
        Assert.Equal("faulting", evt.ThreadName);
        Assert.True(evt.Faulted);
        Assert.Equal("PanicBoundary", evt.StableErrorId);
    }

    [Fact]
    public void DisposeStopsProducingNewEventsButKeepsBacklogTest()
    {
        var supervisor = PlatformModule.CreateThreadSupervisor();

        // 先制造一条真实的故障事件——否则 _events 恒空，本测试就是空转断言：
        // 把 Dispose() 掏空成 `_disposed = true` 也照样通过（已被变异测试证实）。
        var body = new ThrowingBody();
        supervisor.Start("faulting-before-dispose", body);

        // 线程退出 ⟹ 监督器的 catch 已经跑完、事件已入队。用它作同步点，
        // 避免「信号已发但 Publish 还没执行」时 Dispose 把事件吃掉的竞态。
        Assert.True(TimerServiceTests.SpinUntil(() => body.RunningThread is { IsAlive: false }));

        supervisor.Dispose();

        // 卡面判据是「不再**产出**事件」——存量必须留着：
        // 宿主收敛常见顺序就是先 Dispose 再 drain 故障做退出诊断，清空会把诊断吃掉。
        Assert.True(supervisor.TryDrainEvent(out var backlog));
        Assert.True(backlog.Faulted);
        Assert.Equal("faulting-before-dispose", backlog.ThreadName);
    }

    [Fact]
    public void DisposeJoinsThreadsTest()
    {
        var supervisor = PlatformModule.CreateThreadSupervisor();
        var body = new LiveBody();
        supervisor.Start("joinable", body);
        Assert.True(TimerServiceTests.SpinUntil(() => body.Ticks > 0));

        supervisor.Dispose();

        // Dispose 返回后线程必须已经退出——否则宿主「关干净了」是假的。
        Assert.False(body.RunningThread!.IsAlive, "Dispose 返回后受监督线程仍存活");
    }

    private sealed class LiveBody : IThreadBody
    {
        private int _ticks;

        internal int Ticks => Volatile.Read(ref _ticks);

        internal Thread? RunningThread { get; private set; }

        public ThreadStepResult Step(CancellationToken ct)
        {
            RunningThread ??= Thread.CurrentThread;
            Interlocked.Increment(ref _ticks);
            ct.WaitHandle.WaitOne(1);
            return new ThreadStepResult(true, null);
        }
    }


    private sealed class RecordingBody : IThreadBody
    {
        private string? _observedName;

        // Volatile：body 在受监督线程上写，测试在自己的线程上读。
        internal string? ObservedName => Volatile.Read(ref _observedName);

        public ThreadStepResult Step(CancellationToken ct)
        {
            Volatile.Write(ref _observedName, Thread.CurrentThread.Name);
            ct.WaitHandle.WaitOne(1);
            return new ThreadStepResult(true, null);
        }
    }

    private sealed class ThrowingBody : IThreadBody
    {
        internal Thread? RunningThread { get; private set; }

        public ThreadStepResult Step(CancellationToken ct)
        {
            RunningThread = Thread.CurrentThread;
            throw new InvalidOperationException("boom");
        }
    }
}

public sealed class PlatformSourceDisciplineTests
{
    [Fact]
    public void TaskDelayHasAtMostOneSourceFileTest()
    {
        var offenders = PlatformSources.FilesContaining("Task.Delay");
        Assert.True(
            offenders.Count <= 1,
            $"Task.Delay 出现在多个文件：{string.Join(", ", offenders)}");

        if (offenders.Count == 1)
        {
            var text = PlatformSources.ReadAll()[offenders[0]];
            Assert.Contains("全仓唯一等待语义落点", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PlatformHasNoThreadSleepAndNoBareDateTimeTest()
    {
        Assert.Empty(PlatformSources.FilesContaining("Thread.Sleep"));

        // System.DateTime（非 DateTimeOffset）在本工程内零出现。
        Assert.Empty(PlatformSources.CodeMatching(@"\bDateTime\b(?!Offset)"));
    }

    [Fact]
    public void NoUnboundedChannelOrBareConcurrentQueueTest()
    {
        Assert.Empty(PlatformSources.FilesContaining("CreateUnbounded"));
        Assert.Empty(PlatformSources.FilesContaining("ConcurrentQueue"));
    }

    [Fact]
    public void QueuesJsonIsRegisteredAndEmptyByDesignTest()
    {
        var path = System.IO.Path.Combine(PlatformSources.ProjectDirectory, "queues.json");
        Assert.True(System.IO.File.Exists(path), $"缺 queues.json：{path}");

        using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("queues", out var queues));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, queues.ValueKind);

        // Platform 只提供队列原语，不持有任何具体业务队列。
        Assert.Equal(0, queues.GetArrayLength());
        Assert.True(root.TryGetProperty("note", out _));
    }
}

internal static class PlatformSources
{
    internal static string ProjectDirectory { get; } = Locate();

    private static string Locate()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !string.Equals(dir.Name, "mvp-host", StringComparison.Ordinal))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("从测试输出目录向上找不到 mvp-host/");
        }

        return System.IO.Path.Combine(dir.FullName, "src", "Lumio.Server.MvpHost.Platform");
    }

    /// <summary>
    /// 键是**相对工程目录的路径**，不是 basename：用 basename 会让
    /// <c>Internal/PlatformWait.cs</c> 与 <c>PlatformWait.cs</c> 相互覆盖，
    /// 字典里只剩一个，纪律扫描随之出现假阴性。
    /// 同时排除 <c>obj/</c> 与 <c>bin/</c>——SDK 生成物不该参与纪律判定。
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ReadAll()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in System.IO.Directory.EnumerateFiles(ProjectDirectory, "*.cs", System.IO.SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(ProjectDirectory, file)
                .Replace('\\', '/');

            if (relative.StartsWith("obj/", StringComparison.Ordinal) ||
                relative.StartsWith("bin/", StringComparison.Ordinal))
            {
                continue;
            }

            result[relative] = System.IO.File.ReadAllText(file);
        }

        return result;
    }

    /// <summary>
    /// 剥离注释后的源码。纪律约束的是**代码**，不是散文——
    /// 讲解「本工程为什么不许出现 X」的注释里必然会写出 X 本身，
    /// 拿原文扫描会把这类注释判成违规（本卡实测被误伤三次）。
    /// </summary>
    /// <remarks>
    /// 已知边界：剥离用的是朴素正则，不做完整词法分析。含 <c>//</c> 的字符串字面量
    /// （如 URL）会被从该行截断，可能造成**假阴性**（漏判），不会造成假阳性。
    /// 本工程无此类字面量；若将来出现，这条扫描的可靠性需要重估。
    /// </remarks>
    private static string StripComments(string source)
    {
        var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(
            source, @"/\*.*?\*/", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);

        return System.Text.RegularExpressions.Regex.Replace(
            withoutBlocks, @"//.*?$", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Multiline);
    }

    internal static IReadOnlyList<string> CodeMatching(string pattern)
    {
        return ReadAll()
            .Where(kv => System.Text.RegularExpressions.Regex.IsMatch(StripComments(kv.Value), pattern))
            .Select(kv => kv.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    internal static IReadOnlyList<string> FilesContaining(params string[] needles)
    {
        return ReadAll()
            .Where(kv => needles.Any(n => StripComments(kv.Value).Contains(n, StringComparison.Ordinal)))
            .Select(kv => kv.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }
}
