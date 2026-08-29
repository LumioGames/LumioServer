using System;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Platform;
using Xunit;

namespace Lumio.Server.MvpHost.Auth.Tests;

/// <summary>
/// 防重放窗口：窗口 provisional 30 秒 + 单调 nonce，键为 <c>(PrincipalId, nonce)</c>。
/// </summary>
public sealed class AntiReplayTest
{
    private static readonly PrincipalId Principal = new("principal-A-A-1.1.0");

    private static long Seconds(int n) => TimeSpan.FromSeconds(n).Ticks;

    [Fact]
    public void 同一主体同一nonce第二次判Replayed()
    {
        var clock = new FakeClock();
        var window = MvpAntiReplayWindow.Create(clock, AuthProvisionalDefaults.AntiReplayWindowSeconds, 8);

        Assert.Equal(AntiReplayVerdict.Ok, window.Check(Principal, "nonce-1", clock.Now));
        Assert.Equal(AntiReplayVerdict.Replayed, window.Check(Principal, "nonce-1", clock.Now));
    }

    /// <summary>键是二元组：换主体或换 nonce 都是另一条记录。</summary>
    [Fact]
    public void 键是主体与nonce的二元组()
    {
        var clock = new FakeClock();
        var window = MvpAntiReplayWindow.Create(clock, AuthProvisionalDefaults.AntiReplayWindowSeconds, 8);

        Assert.Equal(AntiReplayVerdict.Ok, window.Check(Principal, "nonce-1", clock.Now));
        Assert.Equal(AntiReplayVerdict.Ok, window.Check(Principal, "nonce-2", clock.Now));
        Assert.Equal(AntiReplayVerdict.Ok, window.Check(new PrincipalId("other"), "nonce-1", clock.Now));
    }

    /// <summary>
    /// 推进超过 30 秒后，携带旧时刻的请求判 <c>OutOfWindow</c>。
    /// 这条挡的是「重放一条足够旧的请求」——它不在窗口内，因此连去重表都不该查。
    /// </summary>
    [Fact]
    public void 超出三十秒窗口的请求判OutOfWindow()
    {
        var clock = new FakeClock();
        var window = MvpAntiReplayWindow.Create(clock, AuthProvisionalDefaults.AntiReplayWindowSeconds, 8);
        var issuedAt = clock.Now;

        clock.Advance(Seconds(AuthProvisionalDefaults.AntiReplayWindowSeconds) + 1);

        Assert.Equal(AntiReplayVerdict.OutOfWindow, window.Check(Principal, "nonce-1", issuedAt));
    }

    [Fact]
    public void 窗口边界内的请求仍判Ok()
    {
        var clock = new FakeClock();
        var window = MvpAntiReplayWindow.Create(clock, AuthProvisionalDefaults.AntiReplayWindowSeconds, 8);
        var issuedAt = clock.Now;

        clock.Advance(Seconds(AuthProvisionalDefaults.AntiReplayWindowSeconds));

        Assert.Equal(AntiReplayVerdict.Ok, window.Check(Principal, "nonce-1", issuedAt));
    }

    /// <summary>
    /// 凭据无效的请求**不消耗**防重放窗口配额——否则任何人都能用一串无效凭据
    /// 把合法主体的 nonce 空间烧光。同一 nonce 随后仍可被一次合法请求使用。
    /// </summary>
    [Fact]
    public void 凭据无效的请求不消耗防重放窗口配额()
    {
        using var harness = new AuthHarness();

        var bad = harness.WrongCredentialCommand(nonce: "nonce-shared", requestId: 1);
        var badOutcome = harness.Service.Authenticate(in bad);
        Assert.Equal(CredentialVerdict.Rejected, badOutcome.Verdict);

        var good = harness.ValidCommand(nonce: "nonce-shared", requestId: 2);
        var goodOutcome = harness.Service.Authenticate(in good);

        Assert.Equal(CredentialVerdict.Accepted, goodOutcome.Verdict);
        Assert.Equal(AntiReplayVerdict.Ok, goodOutcome.AntiReplay);
    }

    /// <summary>
    /// 连续命中达到阈值即产出类型化 <c>ReplayStorm</c> 信号，并按 provisional SRV-D-006
    /// 把该来源配额减半。配额初值取 <c>AuthRequestQueueMaxItems</c>——不另造一个 provisional 数字。
    /// </summary>
    [Fact]
    public void 重放风暴产出信号并把该来源配额减半()
    {
        var clock = new FakeClock();
        var window = MvpAntiReplayWindow.Create(clock, AuthProvisionalDefaults.AntiReplayWindowSeconds, 4);

        var before = window.QuotaFor(Principal);
        Assert.Equal(AuthProvisionalDefaults.AuthRequestQueueMaxItems, before);

        Assert.Equal(AntiReplayVerdict.Ok, window.Check(Principal, "nonce-1", clock.Now));
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(AntiReplayVerdict.Replayed, window.Check(Principal, "nonce-1", clock.Now));
        }

        Assert.True(window.TryDrainReplayStorm(out var offender));
        Assert.Equal(Principal, offender);
        Assert.Equal(before / 2, window.QuotaFor(Principal));
    }

    /// <summary>信号是**一次性**的：drain 走之后不再重复产出同一条。</summary>
    [Fact]
    public void 风暴信号被取走后不再重复产出()
    {
        var clock = new FakeClock();
        var window = MvpAntiReplayWindow.Create(clock, AuthProvisionalDefaults.AntiReplayWindowSeconds, 2);

        Assert.Equal(AntiReplayVerdict.Ok, window.Check(Principal, "nonce-1", clock.Now));
        Assert.Equal(AntiReplayVerdict.Replayed, window.Check(Principal, "nonce-1", clock.Now));
        Assert.Equal(AntiReplayVerdict.Replayed, window.Check(Principal, "nonce-1", clock.Now));

        Assert.True(window.TryDrainReplayStorm(out _));
        Assert.False(window.TryDrainReplayStorm(out _));
    }

    /// <summary>未达阈值不产出信号，配额不变——阈值不是装饰。</summary>
    [Fact]
    public void 未达阈值不产出信号且配额不变()
    {
        var clock = new FakeClock();
        var window = MvpAntiReplayWindow.Create(clock, AuthProvisionalDefaults.AntiReplayWindowSeconds, 4);

        Assert.Equal(AntiReplayVerdict.Ok, window.Check(Principal, "nonce-1", clock.Now));
        Assert.Equal(AntiReplayVerdict.Replayed, window.Check(Principal, "nonce-1", clock.Now));

        Assert.False(window.TryDrainReplayStorm(out _));
        Assert.Equal(AuthProvisionalDefaults.AuthRequestQueueMaxItems, window.QuotaFor(Principal));
    }

    /// <summary>重放命中的认证结果：拒绝，且 <c>StableErrorId</c> 取生成物声明的那一个。</summary>
    [Fact]
    public void 重放命中的认证以已注册的声明式理由拒绝()
    {
        using var harness = new AuthHarness();

        var first = harness.ValidCommand(nonce: "nonce-replay", requestId: 1);
        Assert.Equal(AntiReplayVerdict.Ok, harness.Service.Authenticate(in first).AntiReplay);

        var second = harness.ValidCommand(nonce: "nonce-replay", requestId: 2);
        var outcome = harness.Service.Authenticate(in second);

        Assert.Equal(AntiReplayVerdict.Replayed, outcome.AntiReplay);
        Assert.Equal(
            Lumio.Gen.ProtocolPermissionValidator.ProtocolGate.DeclaredOnlyReasons[0], outcome.StableErrorId);
    }

    /// <summary>
    /// 可推进的单调时钟。<c>TestKit</c> 的 <c>FakeMonotonicClock</c> 完全等价，
    /// 这里另起一个是为了让本文件的窗口测试不依赖装置构造（不需要凭据文件）。
    /// </summary>
    private sealed class FakeClock : IMonotonicClock
    {
        private long ticks;

        public MonotonicInstant Now => new(this.ticks);

        internal void Advance(long delta) => this.ticks += delta;
    }
}
