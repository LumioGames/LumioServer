using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lumio.Server.MvpHost.Platform;

/// <summary>
/// 全仓唯一等待语义落点。
///
/// 除本文件外，任何工程、任何文件都不得出现 <c>Task.Delay</c>、线程级睡眠或自建轮询循环。
///
/// <b>本工程内这条纪律的实际执行者只有源码扫描测试，不是分析器。</b>
/// `Directory.Build.props` 给本工程开的例外是**工程级** <c>NoWarn RS0030</c>，
/// 它一刀切关掉了全部四条禁令（Socket / DateTime / DateTimeOffset / Thread.Sleep），
/// 不只是墙钟那一条——所以本工程内写 <c>Thread.Sleep</c> 构建**不会**失败。
/// 收窄该例外到文件级归 `Directory.Build.props` 的所有者卡（R-00270），已上报。
/// 在收窄之前，不要以为这里有构建期护栏。
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
