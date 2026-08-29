namespace Lumio.Server.MvpHost.Auth;

/// <summary>
/// 全部为 <b>provisional</b> 声明值（SRV-D-005 / D-006 / D-013），
/// **不是公共常量、不是性能承诺**：决策门冻结后由上游给定，本仓即改用公共取值。
/// </summary>
public static class AuthProvisionalDefaults
{
    /// <summary>SRV-D-005：防重放窗口秒数。</summary>
    public const int AntiReplayWindowSeconds = 30;

    /// <summary>SRV-D-006：连续重放命中达到该阈值即产出 <c>ReplayStorm</c> 信号。</summary>
    public const int ReplayStormThreshold = 8;

    /// <summary>SRV-D-015 同族：<c>MvpAuthRequestQueue</c> 容量。</summary>
    public const int AuthRequestQueueMaxItems = 32;

    /// <summary><c>MvpAuthEventQueue</c> 容量。</summary>
    public const int AuthEventQueueMaxItems = 64;

    /// <summary>SRV-D-013：授权对象存活秒数；重连一律重新派生，不续期。</summary>
    public const int GrantLifetimeSeconds = 300;
}

/// <summary>
/// <c>MvpAuthRequestQueue</c> 的入队结果。
///
/// <c>AuthBusy</c> 是**模块内部状态、不是 <c>StableErrorId</c>**：
/// <c>Lumio.Gen.ContractTypes.Catalog.StableErrorIds</c> 中不在册。
/// 需要对外表达时一律映射 <c>Platform</c> 队列自己给出的已注册码（<c>QueueFull</c>），
/// 本工程**不写该字符串字面量**——照抄一份就等于给它一个独立漂移的机会。
/// </summary>
public enum AuthQueueAdmission
{
    Accepted,
    AuthBusy,
    Closed,
}
