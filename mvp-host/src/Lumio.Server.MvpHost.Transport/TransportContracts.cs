using System;
using Lumio.Server.MvpHost.HostContracts;

namespace Lumio.Server.MvpHost.Transport;

/// <summary>
/// 连接状态机，逐字取自 <c>modules/transport/README.md</c>：
/// <c>Accepted → EnvelopeValidated → Bound → Active → Draining → Closed</c>，
/// 任一状态因可致命错误 <c>→ Closed(fault)</c>。
///
/// 这是 <b>transport 私有</b>的状态机，不放进 HostContracts——
/// 别的模块不需要知道一个连接处在哪一步，它们只收类型化事件。
/// </summary>
public enum TransportConnectionState
{
    Accepted,
    EnvelopeValidated,
    Bound,
    Active,
    Draining,
    Closed,
}

/// <summary>
/// 生产 Profile 的故障策略：一律放行。
///
/// 它住在本程序集内，但**唯一构造点在组装根 App**——本程序集内不得有任何
/// 对它构造函数的调用依赖。硬编码 pass-through 是 LumioClient 侧的已知缺陷，
/// 那会让「生产里注入不了故障」变成「生产里注入了也没用」。
/// </summary>
public sealed class PassThroughFaultPolicy : ITransportFaultPolicy
{
    public TransportFaultAction Decide(in TransportFaultContext ctx) => TransportFaultAction.Pass;
}

/// <summary>
/// provisional 配置常量。**不是公共常量，也不是性能承诺**——
/// 它们是本仓在 MVP 期取的一组值，随实测调整，不构成对任何下游的保证。
/// </summary>
public static class TransportProvisionalLimits
{
    /// <summary>每连接入站限流：稳态速率（provisional）。</summary>
    public const int InboundMessagesPerSecond = 64;

    /// <summary>每连接入站限流：突发上限（provisional）。</summary>
    public const int InboundBurst = 128;

    /// <summary>每 tick 每连接 egress 批量上限（provisional）。</summary>
    public const int EgressBatchPerTick = 8;

    /// <summary>空闲截止（provisional）。经 <c>ITimerService</c> 投递 Close，不自建轮询线程。</summary>
    public const int IdleTimeoutSeconds = 15;

    /// <summary>
    /// 单次接收缓冲上限（provisional）。**分配前拒绝的关键**：
    /// 无论对端声明多长，本实现一次只分配这么多，超限在累计计数上判死，
    /// 绝不先分配一个「声明长度」的缓冲再看它合不合法。
    /// </summary>
    public const int ReceiveBufferBytes = 8192;
}
