using System;
using System.Collections.Generic;
using System.Threading;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.Auth;

/// <summary>
/// 防重放窗口。键为 <c>(PrincipalId, nonce)</c>，窗口 provisional 30 秒（SRV-D-005）。
///
/// 时间一律取 <see cref="IMonotonicClock"/>：墙钟会跳变、会被 NTP 拨回，
/// 用它算窗口会在时钟调整那一刻把重放变成合法请求，且不报任何错。
///
/// 连续命中达到阈值即产出一次性的 <c>ReplayStorm</c> 信号，并按 provisional SRV-D-006
/// 把该来源配额减半。配额初值取 <see cref="AuthProvisionalDefaults.AuthRequestQueueMaxItems"/>——
/// 一个来源的配额本来就是它在请求队列里能占的份额，**不另造一个 provisional 数字**。
/// </summary>
public sealed class MvpAntiReplayWindow : IAntiReplayWindow
{
    /// <summary>
    /// auth **无自有线程**，认证在 session 编排路径上同步执行，正常装配下本类型是单线程访问。
    /// 这把锁不是在声称并发能力，它挡的是「装配错了」——把同一个窗口接到两条路径上时，
    /// 无锁的字典会静默损坏，而损坏的表现是重放检测时灵时不灵。
    /// </summary>
    private readonly Lock gate = new();

    private readonly Dictionary<(string Principal, string Nonce), long> seenAt = new();
    private readonly Dictionary<string, int> consecutiveHits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> quota = new(StringComparer.Ordinal);
    private readonly Queue<PrincipalId> storms = new();

    private readonly IMonotonicClock clock;
    private readonly long windowTicks;
    private readonly int stormThreshold;

    private MvpAntiReplayWindow(IMonotonicClock clock, long windowTicks, int stormThreshold)
    {
        this.clock = clock;
        this.windowTicks = windowTicks;
        this.stormThreshold = stormThreshold;
    }

    public static MvpAntiReplayWindow Create(IMonotonicClock clock, int windowSeconds, int stormThreshold)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stormThreshold);

        return new MvpAntiReplayWindow(clock, TimeSpan.FromSeconds(windowSeconds).Ticks, stormThreshold);
    }

    /// <summary>
    /// 判定顺序刻意如此：**先判窗口，再查去重表**。
    /// 反过来的话，一条足够旧的重放请求会先命中去重表被记成一次 <c>Replayed</c>，
    /// 从而把「这条请求根本不该被受理」误报成「这条请求是重放」——两者的处置不同。
    /// </summary>
    public AntiReplayVerdict Check(PrincipalId principal, string nonce, MonotonicInstant receivedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(nonce);

        lock (this.gate)
        {
            var now = this.clock.Now.Ticks;
            this.Evict(now);

            if (now - receivedAt.Ticks > this.windowTicks)
            {
                return AntiReplayVerdict.OutOfWindow;
            }

            var key = (principal.Value, nonce);
            if (this.seenAt.ContainsKey(key))
            {
                this.RecordHit(principal);
                return AntiReplayVerdict.Replayed;
            }

            this.seenAt[key] = receivedAt.Ticks;
            this.consecutiveHits[principal.Value] = 0;
            return AntiReplayVerdict.Ok;
        }
    }

    /// <summary>取走一条待处理的 <c>ReplayStorm</c> 信号。信号是**一次性**的，取走即不再重复产出。</summary>
    public bool TryDrainReplayStorm(out PrincipalId offender)
    {
        lock (this.gate)
        {
            if (this.storms.Count > 0)
            {
                offender = this.storms.Dequeue();
                return true;
            }
        }

        offender = default;
        return false;
    }

    /// <summary>该来源当前的准入配额。风暴命中后减半（provisional SRV-D-006）。</summary>
    public int QuotaFor(PrincipalId principal)
    {
        lock (this.gate)
        {
            return this.quota.TryGetValue(principal.Value, out var value)
                ? value
                : AuthProvisionalDefaults.AuthRequestQueueMaxItems;
        }
    }

    private void RecordHit(PrincipalId principal)
    {
        var hits = this.consecutiveHits.TryGetValue(principal.Value, out var current) ? current + 1 : 1;
        this.consecutiveHits[principal.Value] = hits;

        if (hits < this.stormThreshold)
        {
            return;
        }

        this.consecutiveHits[principal.Value] = 0;
        this.storms.Enqueue(principal);
        this.quota[principal.Value] = this.QuotaUnlocked(principal) / 2;
    }

    private int QuotaUnlocked(PrincipalId principal)
        => this.quota.TryGetValue(principal.Value, out var value)
            ? value
            : AuthProvisionalDefaults.AuthRequestQueueMaxItems;

    /// <summary>
    /// 窗口外的记录必须清掉，否则去重表是无界的——那会让本类型变成一条按连接数增长的内存泄漏，
    /// 而且泄漏方式是「内存慢慢涨」，没有任何一处会报错。清掉之后同一 nonce 在窗口外可重新使用，
    /// 这与「窗口外的请求本就判 OutOfWindow」是一致的。
    /// </summary>
    private void Evict(long now)
    {
        List<(string Principal, string Nonce)>? expired = null;

        foreach (var (key, at) in this.seenAt)
        {
            if (now - at > this.windowTicks)
            {
                (expired ??= []).Add(key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var key in expired)
        {
            this.seenAt.Remove(key);
        }
    }
}
