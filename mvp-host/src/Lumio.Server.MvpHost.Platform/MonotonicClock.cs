using System.Diagnostics;

namespace Lumio.Server.MvpHost.Platform;

/// <summary>
/// 进程内单调时钟。刻意不读任何墙钟：墙钟会跳变、会被 NTP 拨回，
/// 用它算超时与窗口会在时钟调整那一刻静默失序。本类型与 <see cref="SystemWallClock"/> 严格分域。
/// </summary>
/// <remarks>
/// <b>单位纪律</b>：产出的 <see cref="MonotonicInstant.Ticks"/> 是
/// <see cref="System.TimeSpan"/> tick（100 ns），**不是** <see cref="Stopwatch"/> 的原始计数。
/// 二者极易混淆且后果是平台相关的静默偏差：本机实测 <c>Stopwatch.Frequency</c> = 1e9（1 ns），
/// Windows 上是 1e7（100 ns），而 <c>TimeSpan.TicksPerSecond</c> 恒为 1e7。直接用原始计数会让
/// <c>clock.Now.Ticks + TimeSpan.FromSeconds(30).Ticks</c> 在 macOS/Linux 上变成 0.3 秒、
/// 在 Windows 上却是正确的 30 秒——重连窗口、防重放窗口、ack 超时全部按 1/100 生效且不报错。
/// 因此这里一次性换算成 TimeSpan tick，使下游按直觉写的时长运算恒为正确。
/// </remarks>
internal sealed class MonotonicClock : IMonotonicClock
{
    private readonly long _origin = Stopwatch.GetTimestamp();

    public MonotonicInstant Now => new(Stopwatch.GetElapsedTime(_origin).Ticks);
}
