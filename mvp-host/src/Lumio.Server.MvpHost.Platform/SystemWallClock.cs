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
    // 本 pragma 是全仓唯一的 RS0030 例外，**它就是例外本身**，不是任何外层开关的内层备份。
    // Directory.Build.props 刻意不写工程级 NoWarn（写了会一刀切放行本工程的全部四条禁令），
    // 因此本文件之外、本工程之内的 DateTimeOffset / Socket / DateTime / Thread.Sleep 照常被拦。
    // 删掉它，本文件立刻报 RS0030；把它挪到别处，就等于把全仓唯一墙钟出口挪走。
#pragma warning disable RS0030
    public string UtcIso8601Now() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
#pragma warning restore RS0030
}
