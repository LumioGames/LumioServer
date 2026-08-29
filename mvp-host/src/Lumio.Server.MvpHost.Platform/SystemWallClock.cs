using System;
using System.Globalization;

namespace Lumio.Server.MvpHost.Platform;

/// <summary>
/// 全仓唯一墙钟出口。本文件是整个 mvp-host 中允许出现 <c>System.DateTimeOffset</c> 的唯一位置
/// （例外声明在 <c>Directory.Build.props</c>，由 Platform 工程内的源码扫描测试锁定「恰有一个文件」）。
///
/// <b>存在理由</b>：架构源 <c>logging-event.schema.json</c> 的 <c>required</c> 含 <c>timestamp</c>
/// 且 <c>additionalProperties: false</c>——没有这个出口，本仓根本产不出一条合法的 logging-event，
/// Observability 的全部审计断言都不可满足。
///
/// <b>不得用于任何超时 / 窗口 / 间隔 / 顺序判定</b>：墙钟会跳变、会被 NTP 拨回，
/// 拿它算窗口是经典的隐藏 bug 源。那些一律走 <see cref="IMonotonicClock"/>。
/// </summary>
internal sealed class SystemWallClock : IWallClock
{
    // 本 pragma 是「双重收窄」的内层。注意它目前是**冗余**的：
    // Directory.Build.props 给本工程开的例外是工程级 NoWarn RS0030，外层已经把诊断关掉，
    // 内层因此抑制不到任何东西。等该例外收窄到文件级（归 R-00270，已上报）之后它才真正生效。
    // 保留它，是为了收窄那天不需要再回来补。
#pragma warning disable RS0030
    public string UtcIso8601Now() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
#pragma warning restore RS0030
}
