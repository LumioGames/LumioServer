using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Wire;
using Xunit;

namespace Lumio.Server.MvpHost.Auth.Tests;

/// <summary>
/// 不可变授权对象与 gate 执行体。
///
/// gate 的**判定本体不在本仓**——它在架构源生成的
/// <c>Lumio.Gen.ProtocolPermissionValidator.ProtocolGate.Evaluate</c>（ADR-048，带
/// <c>RejectPrecedence</c> 与 <c>RegisteredMessageIds</c>）。本组测试因此测两件事：
/// 执行体没有加判据，以及本仓没有偷偷写第二份判定。
/// </summary>
public sealed class GrantAndGateTest
{
    private static MvpPermissionGateRequest Admitted() => new(
        SessionId: "session-001",
        ProductId: AuthHarness.ProductId,
        GameReleaseId: AuthHarness.GameReleaseId,
        MessageId: "Delta",
        Role: "Client",
        Claims: ImmutableArray.Create("replication.read"),
        ConnectionGeneration: 7,
        AdmittedSessionId: "session-001",
        AdmittedProductId: AuthHarness.ProductId,
        AdmittedGameReleaseId: AuthHarness.GameReleaseId,
        AdmittedRole: "Client",
        AdmittedClaims: ImmutableArray.Create("replication.read"),
        AdmittedConnectionGeneration: 7);

    // ── 不可变授权对象

    [Fact]
    public void 授权对象的全部属性都不可在构造后写入()
    {
        var settable = typeof(PermissionGrant)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { } setter
                && !setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)))
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(settable);
    }

    [Fact]
    public void 授权对象的两个集合字段都是ImmutableArray()
    {
        var grant = typeof(PermissionGrant);

        Assert.Equal(typeof(ImmutableArray<string>), grant.GetProperty(nameof(PermissionGrant.Claims))!.PropertyType);
        Assert.Equal(
            typeof(ImmutableArray<string>),
            grant.GetProperty(nameof(PermissionGrant.AllowedMessageTypes))!.PropertyType);
    }

    /// <summary>
    /// 本工程不提供任何「派生后修改 grant」的 API：整个程序集里能产出
    /// <c>PermissionGrant</c> 的成员**只有** <c>Authorize</c> 一个。
    /// 多一个产出点就等于多一条不经授权路径造出 grant 的途径。
    /// </summary>
    [Fact]
    public void 程序集内产出授权对象的成员只有Authorize一个()
    {
        var producers = AuthArchitecture.AllMethods()
            .Where(m => m.ReturnType == typeof(PermissionGrant))
            .Select(m => $"{m.DeclaringType?.FullName}.{m.Name}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[] { $"{typeof(MvpAuthorizationService).FullName}.{nameof(MvpAuthorizationService.Authorize)}" },
            producers);
    }

    /// <summary>重连必须重新派生授权对象（SRV-D-013）：同一主体连续两次 <c>Authorize</c>，代次严格递增。</summary>
    [Fact]
    public void 重连重新派生时授权代次严格递增()
    {
        using var harness = new AuthHarness();
        var principal = new PrincipalId("principal-A-A-1.1.0");
        var scope = new SessionScope(
            new ServerSessionId("session-001"), AuthHarness.ProductId, AuthHarness.GameReleaseId, "Client");

        var first = harness.Service.Authorize(principal, in scope);
        var second = harness.Service.Authorize(principal, in scope);

        Assert.True(second.Epoch.Value > first.Epoch.Value);
    }

    /// <summary>
    /// 允许的消息类型直接取生成物的 <c>RegisteredMessageIds</c>，**本仓不抄一份**。
    /// 判据是「存在性 + 身份」而不是计数：上游 additive 增补一个消息类型不该让本条变红。
    /// </summary>
    [Fact]
    public void 授权对象的允许消息类型取自生成物的已注册集合()
    {
        using var harness = new AuthHarness();
        var scope = new SessionScope(
            new ServerSessionId("session-001"), AuthHarness.ProductId, AuthHarness.GameReleaseId, "Client");

        var grant = harness.Service.Authorize(new PrincipalId("principal-A-A-1.1.0"), in scope);

        Assert.Equal(
            Lumio.Gen.ProtocolPermissionValidator.ProtocolGate.RegisteredMessageIds,
            grant.AllowedMessageTypes);
        Assert.Equal("Client", grant.Role);
    }

    // ── gate 执行体：① ArchUnitNET 调用依赖

    /// <summary>
    /// <c>Auth</c> 程序集内对 <c>MvpProtocolPermissionGate.Evaluate</c> 的调用依赖
    /// **只来自** <c>MvpAuthorizationService.EvaluateMessagePermission</c> 这一个方法。
    /// 多一个调用点就意味着多一条可能与主路径判定不一致的路径。
    /// </summary>
    [Fact]
    public void 对生成物闸门的调用依赖只来自唯一那个方法()
    {
        var callers = AuthArchitecture.MethodCallEdges()
            .Where(edge => edge.Target.Contains(nameof(MvpProtocolPermissionGate), StringComparison.Ordinal)
                && edge.Target.Contains(nameof(MvpProtocolPermissionGate.Evaluate), StringComparison.Ordinal))
            .Select(edge => edge.Caller)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(callers);
        Assert.All(
            callers,
            caller => Assert.Contains(
                nameof(MvpAuthorizationService.EvaluateMessagePermission), caller, StringComparison.Ordinal));
    }

    // ── gate 执行体：② 签名级反射

    /// <summary>
    /// 不存在任何「接受 gate 判定并返回另一个 gate 判定」的成员——
    /// 那种签名正是「拿到判定后再覆盖一次」的形状。
    /// </summary>
    [Fact]
    public void 不存在可用于二次覆盖闸门判定的成员()
    {
        var offenders = AuthArchitecture.AllMethods()
            .Where(m => m.ReturnType == typeof(MvpPermissionGateVerdict)
                && m.GetParameters().Any(p => Unwrap(p.ParameterType) == typeof(MvpPermissionGateVerdict)))
            .Select(m => $"{m.DeclaringType?.FullName}.{m.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    // ── gate 执行体：③ **降级项**——「结果未被二次覆盖」在 IL 层不可判，定向用例 + 评审项

    /// <summary>
    /// **降级项**。闸门判 Accept 的请求，<c>EvaluateMessagePermission</c> 必返回
    /// <c>Accepted = true</c> 且 <c>StableErrorId = null</c>；判 Reject 的请求，
    /// 理由必须**逐字**是生成物给出的那一个。
    /// </summary>
    [Fact]
    public void 闸门判定被原样翻译成Ack不被二次覆盖()
    {
        using var harness = new AuthHarness();

        var accepted = harness.Service.EvaluateMessagePermission(Admitted());
        Assert.True(accepted.Accepted);
        Assert.Null(accepted.StableErrorId);

        var stale = Admitted() with { ConnectionGeneration = 6 };
        var rejected = harness.Service.EvaluateMessagePermission(stale);

        Assert.False(rejected.Accepted);
        Assert.Equal(MvpProtocolPermissionGate.Evaluate(stale).RejectReason, rejected.StableErrorId);
    }

    /// <summary>
    /// 拒绝优先级是**公共规则**，顺序由生成物的 <c>RejectPrecedence</c> 给定。
    /// 逐条驱动一个只触发该级的请求，断言执行体报出的正是那一级。
    /// 顺序表不在本仓重排，也不部分实现。
    /// </summary>
    [Fact]
    public void 每一级拒绝理由都能经执行体原样报出()
    {
        using var harness = new AuthHarness();

        foreach (var reason in Lumio.Gen.ProtocolPermissionValidator.ProtocolGate.RejectPrecedence)
        {
            var request = TriggerFor(reason);
            var viaGate = MvpProtocolPermissionGate.Evaluate(request);
            Assert.Equal(reason, viaGate.RejectReason);

            var viaService = harness.Service.EvaluateMessagePermission(request);
            Assert.False(viaService.Accepted);
            Assert.Equal(reason, viaService.StableErrorId);
        }
    }

    /// <summary>
    /// 本仓源码里**不出现任何拒绝理由字面量**——它们全部来自生成物。
    /// 复制一份字面量就等于给本仓一个独立漂移的入口（与 <c>Wire</c> 侧同款断言）。
    /// </summary>
    [Fact]
    public void 本工程源码不含任何拒绝理由字面量()
    {
        var offenders = (from source in AuthArchitecture.ProductionSources()
                         from reason in Lumio.Gen.ProtocolPermissionValidator.ProtocolGate.RejectPrecedence
                             .Concat(Lumio.Gen.ProtocolPermissionValidator.ProtocolGate.DeclaredOnlyReasons)
                         where source.Text.Contains($"\"{reason}\"", StringComparison.Ordinal)
                         select $"{source.File}: {reason}").ToList();

        Assert.Empty(offenders);
    }

    /// <summary>扫描器必须真的读到了源码，否则上一条会以「零文件」的方式空真通过。</summary>
    [Fact]
    public void 源码扫描器确实读到了本工程的生产源码()
        => Assert.NotEmpty(AuthArchitecture.ProductionSources());

    // ── Role / Claims 是准入上下文，不是每条消息的 wire 字段（ADR-022 明确否决）

    /// <summary>
    /// <c>Auth</c> 对 <c>MvpEnvelopeWriter</c> 的**任何**方法调用依赖数为 0——
    /// 它根本不构造出站信封，因此 <c>Role</c> / <c>Claims</c> 不可能被写进任何 Envelope 字段。
    /// </summary>
    [Fact]
    public void Auth不对信封写入器存在任何调用依赖()
    {
        var offenders = AuthArchitecture.AllMethodCallTargets()
            .Where(target => target.Contains(nameof(MvpEnvelopeWriter), StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary><c>Auth</c> 的公开与内部 API 的返回类型中不出现信封字节形态。</summary>
    [Fact]
    public void Auth的API返回类型中不出现信封字节()
    {
        var offenders = AuthArchitecture.AllMethods()
            .Where(m => Unwrap(m.ReturnType) == typeof(ReadOnlyMemory<byte>))
            .Select(m => $"{m.DeclaringType?.FullName}.{m.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    private static Type Unwrap(Type type) => type.IsByRef ? type.GetElementType()! : type;

    /// <summary>造一个**只**触发指定那一级的请求：优先级更高的判据全部保持相等。</summary>
    private static MvpPermissionGateRequest TriggerFor(string reason) => reason switch
    {
        "StaleConnectionGeneration" => Admitted() with { ConnectionGeneration = 6 },
        "SessionMismatch" => Admitted() with { SessionId = "session-999" },
        "ReleaseMismatch" => Admitted() with { GameReleaseId = "A-9.9.9" },
        "MessagePermissionDenied" => Admitted() with { MessageId = "NotRegistered" },
        "RoleMismatch" => Admitted() with { Role = "Operator" },
        "ClaimNotGranted" => Admitted() with { Claims = ImmutableArray.Create("replication.read", "world.write") },
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason), reason, "生成物新增了一级拒绝理由，本用例的触发器表必须同步补齐"),
    };
}
