using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.Wire;

namespace Lumio.Server.MvpHost.Auth;

/// <summary>
/// auth 存根的编排面。<c>Session</c> 只经 <c>HostContracts</c> 的
/// <see cref="IAuthorizationService"/> 调用本实现，两个程序集互不引用，实例在组装期注入。
///
/// **auth 无自有线程**：认证在调用方（session 编排路径）上同步执行，
/// 既不在 WS 接收循环、也不在 Slot Owner Thread 上。
///
/// **可重试类为空**：认证裁决从不重试，本类型不实现任何重试循环。
/// </summary>
public sealed class MvpAuthorizationService : IAuthorizationService
{
    /// <summary>
    /// 可拒绝路径能产出的 <c>StableErrorId</c> 全集，**整表来自生成物**：
    /// 判定级的六条来自 <c>ProtocolGate.RejectPrecedence</c>（顺序是公共规则，本仓不重排、不部分实现），
    /// 声明级的一条来自 <c>ProtocolGate.DeclaredOnlyReasons</c>（闸门从不自行推导它，由会话所有者声明）。
    /// 本仓一个字面量都不抄——抄一份就等于给它一个独立漂移的机会。
    /// </summary>
    private static readonly ImmutableArray<string> Producible =
    [
        .. Lumio.Gen.ProtocolPermissionValidator.ProtocolGate.RejectPrecedence,
        .. Lumio.Gen.ProtocolPermissionValidator.ProtocolGate.DeclaredOnlyReasons,
    ];

    /// <summary>Host 私有的审计理由文本；不是 <c>StableErrorId</c>，不跨 wire。</summary>
    private const string AdmissionStopped = "audit backpressure: admission stopped, connection refused";

    private const string ReplayRejected = "channel credential replayed or outside the anti-replay window";

    private const string EventQueueFull = "MvpAuthEventQueue full: success ack held in reserve, never dropped";
    private const string SuccessReserveExhausted = "MvpAuthEventQueue success reserve exhausted";

    private readonly ICredentialVerifier verifier;
    private readonly IAntiReplayWindow antiReplay;
    private readonly IMonotonicClock clock;
    private readonly ObservabilityServices observability;
    private readonly HostIdentity identity;
    private readonly string releasePoolId;

    private readonly IBoundedInbox<AuthenticateCommand> requestInbox;
    private readonly IBoundedInbox<AuthenticateOutcome> eventInbox;
    private readonly IBoundedOutbox<AuthenticateOutcome> eventOutbox;

    /// <summary>
    /// <c>MvpAuthEventQueue</c> 的保留槽。队列满时**成功 ack 绝不丢弃**——
    /// 丢一条成功 ack 的后果是一条已经通过认证的连接在编排层永远等不到回执。
    /// </summary>
    private readonly Queue<AuthenticateOutcome> successReserve = new();
    private readonly object successReserveGate = new();
    private readonly int successReserveCapacity = AuthProvisionalDefaults.AuthEventQueueMaxItems;

    private ulong grantEpoch;
    private ulong eventSeq;

    private MvpAuthorizationService(
        ICredentialVerifier verifier,
        IAntiReplayWindow antiReplay,
        IMonotonicClock clock,
        ObservabilityServices observability,
        in HostIdentity identity,
        string releasePoolId)
    {
        this.verifier = verifier;
        this.antiReplay = antiReplay;
        this.clock = clock;
        this.observability = observability;
        this.identity = identity;
        this.releasePoolId = releasePoolId;

        this.requestInbox = PlatformModule.CreateInbox<AuthenticateCommand>(
            new QueueBudget(AuthProvisionalDefaults.AuthRequestQueueMaxItems, 64 * 1024));
        this.eventInbox = PlatformModule.CreateInbox<AuthenticateOutcome>(
            new QueueBudget(AuthProvisionalDefaults.AuthEventQueueMaxItems, 64 * 1024));
        this.eventOutbox = PlatformModule.CreateOutbox(this.eventInbox);
    }

    /// <summary>
    /// 组装根显式构造入口。
    ///
    /// <para>
    /// <b>本签名比卡面多两个参数，两处都是交付时发现的必要扩展</b>（与 R-00274 把
    /// <c>ObservabilityModule.Create</c> 由四参扩为五参同型，理由同样写在这里而不是靠口头约定）：
    /// </para>
    /// <para>
    /// ① <paramref name="clock"/>：<see cref="PermissionGrant.ExpiresAt"/> 是
    /// <c>MonotonicInstant</c>，而 <c>Authorize</c> 的签名由 <c>HostContracts</c> 冻结、
    /// 不带时刻入参。没有时钟就只剩「填一个编出来的常量」，那会让授权对象的有效期变成谎话。
    /// </para>
    /// <para>
    /// ② <paramref name="identity"/>：<c>IAuditWriter.WriteReleaseScopedReject</c> 要求调用方给出
    /// <c>productId</c> / <c>gameReleaseId</c> / <c>producerId</c>（<c>common.schema.json</c> 的
    /// correlation 里前两个**恒必填**）。唯一的替代来源是入站 <c>VerificationContext</c>，
    /// 那是**对端声明的值**——把它写进审计关联，等于让对端决定自己被记在哪个 Release 名下。
    /// 因此取宿主自身身份，由组装根注入。
    /// </para>
    /// </summary>
    public static MvpAuthorizationService Create(
        ICredentialVerifier verifier,
        IAntiReplayWindow antiReplay,
        IMonotonicClock clock,
        ObservabilityServices observability,
        in HostIdentity identity,
        string releasePoolId)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(antiReplay);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(observability);
        ArgumentException.ThrowIfNullOrWhiteSpace(releasePoolId);

        if (string.IsNullOrEmpty(identity.ProductId)
            || string.IsNullOrEmpty(identity.GameReleaseId)
            || string.IsNullOrEmpty(identity.ProducerId))
        {
            throw new ArgumentException("HostIdentity 的三项必须非空——审计关联的恒必填字段没有别的来源", nameof(identity));
        }

        return new MvpAuthorizationService(verifier, antiReplay, clock, observability, in identity, releasePoolId);
    }

    /// <summary>见 <see cref="Producible"/>：取值域整表来自生成物，本仓既不缩小也不扩大它。</summary>
    public static ImmutableArray<string> ProducibleStableErrorIds => Producible;

    /// <summary>
    /// Audit 背压时为真：编排层据此**停止接纳新连接**。
    /// 这是「Audit 队列背压时认证结果不得静默放行」这条安全红线的机器化出口。
    /// </summary>
    public bool AdmissionMustStop => this.observability.IsAuditBackpressured;

    /// <summary>
    /// 通道认证。顺序固定：**背压门 → 凭据比对 → 防重放**。
    ///
    /// 凭据比对排在防重放**之前**是有意的：凭据无效的请求**不消耗**防重放窗口配额，
    /// 否则任何人都能拿一串无效凭据把合法主体的 nonce 空间烧光。
    /// </summary>
    public AuthenticateOutcome Authenticate(in AuthenticateCommand command)
    {
        if (this.AdmissionMustStop)
        {
            // 背压时**不写新的 audit**：审计队列正是满的那一条，再写只会加深背压。
            return new AuthenticateOutcome(
                CredentialVerdict.Rejected, default, AntiReplayVerdict.Ok, null, AdmissionStopped);
        }

        var context = command.Context;
        var verification = this.verifier.Verify(command.Credential, in context);

        if (verification.Verdict == CredentialVerdict.Rejected)
        {
            // 凭据无效**没有**对应的已注册 ErrorCode（absences.json 的 ABS-AUTH-CREDENTIAL-ERRORCODE），
            // 因此：不发 Envelope Error（承载层以 WebSocket close 1008 拒绝），
            // StableErrorId 为 null，审计里也不写任何 errorCode。
            // 填一个语义不对的已注册码，是把「没有码」伪装成「有依据」。
            this.WriteReleaseScopedReject(reasonCode: null);

            return new AuthenticateOutcome(
                CredentialVerdict.Rejected, default, AntiReplayVerdict.Ok, null, verification.AuditReason);
        }

        var antiReplayVerdict = this.antiReplay.Check(verification.Principal, context.Nonce, context.ReceivedAt);
        if (antiReplayVerdict != AntiReplayVerdict.Ok)
        {
            // 这一条**有**已注册码：它是生成物声明的、闸门从不自行推导的那一个。
            var declared = Lumio.Gen.ProtocolPermissionValidator.ProtocolGate.DeclaredOnlyReasons[0];
            this.WriteReleaseScopedReject(declared);

            return new AuthenticateOutcome(
                verification.Verdict, verification.Principal, antiReplayVerdict, declared, ReplayRejected);
        }

        return new AuthenticateOutcome(
            CredentialVerdict.Accepted, verification.Principal, AntiReplayVerdict.Ok, null, null);
    }

    /// <summary>
    /// 派生不可变授权对象。**重连必须重新派生**（SRV-D-013）：代次严格递增，
    /// 不保留任何认证状态，也不实现任何 Session Resume Token 快捷路径。
    ///
    /// <c>Claims</c> 恒空：架构源尚未发布任何 claim 词表，本仓编一个就是发明公共合同。
    /// <c>AllowedMessageTypes</c> 直接取生成物的 <c>RegisteredMessageIds</c>，不抄一份。
    /// </summary>
    public PermissionGrant Authorize(PrincipalId principal, in SessionScope scope)
    {
        var expiresAt = new MonotonicInstant(
            this.clock.Now.Ticks + TimeSpan.FromSeconds(AuthProvisionalDefaults.GrantLifetimeSeconds).Ticks);

        return new PermissionGrant(
            Principal: principal,
            Role: scope.Role,
            Claims: ImmutableArray<string>.Empty,
            AllowedMessageTypes: [.. Lumio.Gen.ProtocolPermissionValidator.ProtocolGate.RegisteredMessageIds],
            Epoch: new GrantEpoch(++this.grantEpoch),
            ExpiresAt: expiresAt);
    }

    /// <summary>
    /// gate 执行体。**判定全部委托生成物**，本方法只把结果翻译成 <see cref="AckResult"/>——
    /// 不加判据、不重排拒绝优先级、不二次覆盖结果。
    ///
    /// 生成物的能力边界照 ADR-048 §2：它**只校验「已注册」，不校验角色权限**。
    /// 架构源没有 role→message 权限表，本仓也不补一张——补一张就是发明公共合同并抢跑 D-009。
    /// </summary>
    public AckResult EvaluateMessagePermission(in MvpPermissionGateRequest request)
    {
        var verdict = MvpProtocolPermissionGate.Evaluate(in request);

        return new AckResult(verdict.Accepted, verdict.RejectReason);
    }

    /// <summary>
    /// <c>MvpAuthRequestQueue</c> 的入队。<paramref name="outward"/> 是对外表达形态：
    /// <c>AuthBusy</c> 是模块内部状态，对外一律用**队列自己给出的已注册码**，
    /// 本工程不抄那个字符串。
    /// </summary>
    public AuthQueueAdmission TryEnqueueRequest(in AuthenticateCommand command, out AckResult outward)
    {
        var result = this.requestInbox.TryEnqueue(in command);
        outward = new AckResult(result.Status == EnqueueStatus.Accepted, result.StableErrorId);

        return result.Status switch
        {
            EnqueueStatus.Accepted => AuthQueueAdmission.Accepted,
            EnqueueStatus.Full => AuthQueueAdmission.AuthBusy,
            _ => AuthQueueAdmission.Closed,
        };
    }

    public bool TryDequeueRequest(out AuthenticateCommand command) => this.requestInbox.TryDequeue(out command);

    /// <summary>
    /// 把一条认证结果投给 <c>MvpAuthEventQueue</c>（所有者是消费者 <c>Session</c>）。
    /// 满载时写一条 diagnostic，且**成功 ack 进保留槽绝不丢弃**。
    /// </summary>
    public void PublishOutcome(in AuthenticateOutcome outcome)
    {
        var result = this.eventOutbox.TryPublish(in outcome);
        if (result.Status == EnqueueStatus.Accepted)
        {
            return;
        }

        if (IsSuccessAck(in outcome))
        {
            lock (this.successReserveGate)
            {
                if (this.successReserve.Count >= this.successReserveCapacity)
                {
                    this.observability.Diagnostics.Write(
                        "Diagnostic",
                        "Error",
                        SuccessReserveExhausted);
                    throw new InvalidOperationException(SuccessReserveExhausted);
                }

                this.successReserve.Enqueue(outcome);
            }
        }

        this.observability.Diagnostics.Write("Diagnostic", "Error", EventQueueFull);
    }

    /// <summary>保留槽优先：成功 ack 必须先于普通存量被交付。</summary>
    public bool TryDequeueOutcome(out AuthenticateOutcome outcome)
    {
        lock (this.successReserveGate)
        {
            if (this.successReserve.Count > 0)
            {
                outcome = this.successReserve.Dequeue();
                return true;
            }
        }

        return this.eventInbox.TryDequeue(out outcome);
    }

    /// <summary>
    /// 关闭两条队列。请求队列关闭后拒绝新请求；事件队列关闭后**只交付已入队项**
    /// （含保留槽），不再接受新投递。幂等。
    /// </summary>
    public void CloseQueues()
    {
        this.requestInbox.Close();
        this.eventInbox.Close();
    }

    private static bool IsSuccessAck(in AuthenticateOutcome outcome)
        => outcome.Verdict == CredentialVerdict.Accepted
            && outcome.AntiReplay == AntiReplayVerdict.Ok
            && outcome.StableErrorId is null;

    /// <summary>
    /// 拒绝事件的唯一写出口。<c>correlation.scope = "Release"</c> 且**不带 sessionId**——
    /// 认证失败发生在 session 创建之前，此时任何 sessionId 都是编造的（ADR-011）。
    /// <c>eventId</c> 与 <c>timestamp</c> 由 <c>Observability</c> 内部填充，本方法不传。
    /// </summary>
    private void WriteReleaseScopedReject(string? reasonCode)
    {
        var seq = this.eventSeq++;

        this.observability.Audit.WriteReleaseScopedReject(
            releasePoolId: this.releasePoolId,
            productId: this.identity.ProductId,
            gameReleaseId: this.identity.GameReleaseId,
            traceId: $"trace-auth-reject-{seq}",
            producerId: this.identity.ProducerId,
            eventSeq: seq,
            reasonCode: reasonCode);
    }
}
