using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.Wire;

namespace Lumio.Server.MvpHost.HostContracts;

/// <summary>
/// adapter SPI —— D-011 的合法落点；**不成为 wire 标准**。
/// MVP 唯一实现是启动期载入共享密钥、常量时间 exact-byte 比对的
/// <c>InjectedExactByteCredentialVerifier</c>，它只在 TestKit 与显式 dev Profile 下可被组装。
/// </summary>
public interface ICredentialVerifier
{
    CredentialVerification Verify(OpaqueCredentialInput credential, in VerificationContext context);
}

/// <summary>
/// 防重放窗口。键为 <c>(PrincipalId, nonce)</c>。
/// **凭据无效不消耗防重放窗口配额**——否则无效凭据可以用来耗尽合法主体的窗口。
/// </summary>
public interface IAntiReplayWindow
{
    AntiReplayVerdict Check(PrincipalId principal, string nonce, MonotonicInstant receivedAt);
}

/// <summary>
/// 授权服务。后两个成员的存在理由：gate 执行体归 <c>Auth</c>、调用方是 <c>Session</c>，
/// 两者同层且**相互零引用**，因此调用只能经本层接口，实例在 App 组装期接线。
/// </summary>
public interface IAuthorizationService
{
    /// <summary>在 session 编排路径上**同步**执行——既不在 WS 接收循环、也不在 Owner Thread 上。</summary>
    AuthenticateOutcome Authenticate(in AuthenticateCommand command);

    PermissionGrant Authorize(PrincipalId principal, in SessionScope scope);

    /// <summary>gate 执行体。判定本身委托架构源生成的 <c>ProtocolGate</c>，本仓不写第二份。</summary>
    AckResult EvaluateMessagePermission(in MvpPermissionGateRequest request);

    /// <summary>
    /// Audit 背压时为真：编排层据此**停止接纳新连接**。
    ///
    /// 这是「Audit 队列背压时认证结果不得静默放行」这条安全红线的机器化出口——
    /// 它让「停止接纳」成为编排层必须读的一个值，而不是一句纪律。
    /// </summary>
    bool AdmissionMustStop { get; }
}
