using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lumio.Server.MvpHost.Platform;

/// <summary>
/// 全仓唯一等待语义落点。
///
/// 除本文件外，任何工程、任何文件都不得出现 <c>Task.Delay</c>、线程级睡眠或自建轮询循环。
///
/// <b>这条纪律由构建期护栏与源码扫描测试双重执行。</b>
/// `Directory.Build.props` 刻意**不写工程级** <c>NoWarn RS0030</c>——全仓唯一的禁用面例外是
/// <c>SystemWallClock.cs</c> 内的文件级 pragma，只放行墙钟那一条。因此本工程内、本文件之外写
/// <c>Thread.Sleep</c> 会报 <c>RS0030</c> 并构建失败；本文件自身走
/// <c>Task.Delay(...).GetAwaiter().GetResult()</c>，不触发 <c>Thread.Sleep(Int32)</c> 那条禁令，
/// 所以这里的唯一落点约束仍要靠源码扫描测试守。
///
/// 等待集中在这里的理由：定时语义一旦散落，重连窗口、防重放窗口、ack 超时就会各自长出
/// 一套时间源与一条轮询线程，将来换成 Rust 侧的 host-runtime 时是结构性返工。
/// </summary>
internal static class PlatformWait
{
    /// <summary>
    /// 阻塞当前受监督线程至多 <paramref name="duration"/>；取消时立即返回。
    /// 只由 <see cref="MvpTimerService"/> 的驱动线程调用。
    /// </summary>
    internal static void Block(TimeSpan duration, CancellationToken ct)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            Task.Delay(duration, ct).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // 取消是正常收敛路径，不是故障：调用方据 CancellationToken 自行退出循环。
        }
    }
}
