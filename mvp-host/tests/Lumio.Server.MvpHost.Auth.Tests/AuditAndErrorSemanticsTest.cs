using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Xunit;

namespace Lumio.Server.MvpHost.Auth.Tests;

/// <summary>
/// 错误语义与审计形状。
///
/// **可重试类为空**：认证裁决从不重试。可拒绝路径产出的 <c>StableErrorId</c> 取值域
/// 完全由生成物给定（<c>RejectPrecedence</c> ∪ <c>DeclaredOnlyReasons</c>），本仓不自造。
/// **凭据无效没有对应的已注册码**（<c>absences.json</c> 的
/// <c>ABS-AUTH-CREDENTIAL-ERRORCODE</c>），因此它既不发 Envelope Error，
/// 审计里也**不写任何 errorCode**——写一个语义不对的已注册码，是把缺席伪装成有依据。
/// </summary>
public sealed class AuditAndErrorSemanticsTest
{
    private static IReadOnlyCollection<string> Registered { get; } =
        Lumio.Gen.ContractTypes.Catalog.StableErrorIds.ToHashSet(StringComparer.Ordinal);

    // ── 错误码取值域

    /// <summary>
    /// 可拒绝路径产出的每一个 <c>StableErrorId</c> 都在
    /// <c>Lumio.Gen.ContractTypes.Catalog.StableErrorIds</c> 里在册；
    /// 取值域本身也不是本仓写死的表，而是生成物的两张表之并。
    /// **不断言个数**——ErrorCode 已由 43 增至 53，计数式断言必然腐烂。
    /// </summary>
    [Fact]
    public void 可拒绝路径的错误码全部在生成物注册()
    {
        Assert.NotEmpty(MvpAuthorizationService.ProducibleStableErrorIds);

        var unregistered = MvpAuthorizationService.ProducibleStableErrorIds
            .Where(id => !Registered.Contains(id))
            .ToList();

        Assert.Empty(unregistered);
    }

    /// <summary>取值域等于生成物两张表之并——本仓既不缩小也不扩大它。</summary>
    [Fact]
    public void 错误码取值域恰是生成物两张表之并()
    {
        var expected = Lumio.Gen.ProtocolPermissionValidator.ProtocolGate.RejectPrecedence
            .Concat(Lumio.Gen.ProtocolPermissionValidator.ProtocolGate.DeclaredOnlyReasons)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            expected,
            MvpAuthorizationService.ProducibleStableErrorIds.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary><c>AuthBusy</c> 是模块内部状态，**不在册**；对外一律映射队列自己给出的已注册码。</summary>
    [Fact]
    public void 内部忙碌码不在册且不进错误码位置()
    {
        Assert.DoesNotContain(nameof(AuthQueueAdmission.AuthBusy), Registered);

        using var harness = new AuthHarness();
        var commands = new List<AuthenticateCommand>();
        for (var i = 0; i <= AuthProvisionalDefaults.AuthRequestQueueMaxItems; i++)
        {
            commands.Add(harness.ValidCommand($"nonce-{i}", (ulong)i));
        }

        AckResult last = default;
        var admission = AuthQueueAdmission.Accepted;
        foreach (var command in commands)
        {
            var current = command;
            admission = harness.Service.TryEnqueueRequest(in current, out last);
        }

        Assert.Equal(AuthQueueAdmission.AuthBusy, admission);
        Assert.False(last.Accepted);
        Assert.NotNull(last.StableErrorId);
        Assert.Contains(last.StableErrorId, Registered);
        Assert.NotEqual(nameof(AuthQueueAdmission.AuthBusy), last.StableErrorId);
    }

    // ── 可重试类为空

    /// <summary>
    /// <c>AuthenticateOutcome</c> 的取值里没有任何表示「稍后重试」的成员，
    /// 且整个程序集里不存在名字带重试语义的类型或成员。**签名级断言**——
    /// 「不存在重试循环」在 IL 层不可判，这一半是评审项。
    /// </summary>
    [Theory]
    [InlineData("Retry")]
    [InlineData("Backoff")]
    [InlineData("TryAgain")]
    public void 认证结果不含任何重试语义的取值(string forbidden)
    {
        var enumValues = Enum.GetNames<CredentialVerdict>()
            .Concat(Enum.GetNames<AntiReplayVerdict>())
            .Concat(Enum.GetNames<AuthQueueAdmission>());

        Assert.DoesNotContain(enumValues, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));

        var offenders = AuthArchitecture.AllNames()
            .Where(name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(offenders);
    }

    // ── 凭据无效：不发 Envelope Error、不写任何 errorCode

    /// <summary>
    /// 凭据比对失败：<c>StableErrorId</c> 为 <c>null</c>（无对应已注册码），
    /// 不构造任何出站 Envelope，**恰产出一条** Audit 记录，且该记录**不带 errorCode**。
    /// </summary>
    [Fact]
    public void 凭据无效不发Envelope且审计不带错误码()
    {
        using var harness = new AuthHarness();
        var command = harness.WrongCredentialCommand();

        var outcome = harness.Service.Authenticate(in command);

        Assert.Equal(CredentialVerdict.Rejected, outcome.Verdict);
        Assert.Null(outcome.StableErrorId);
        Assert.NotNull(outcome.AuditReason);

        var records = harness.DrainAuditRecords();
        var record = Assert.Single(records);
        Assert.Null(record.ReasonCode);

        var json = LoggingEventJson.From(record);
        Assert.False(json.ContainsKey("fields"));
        Assert.Null(Wire.MvpSchemaGate.ValidateLoggingEvent(json));
    }

    /// <summary>
    /// **防退化**：有已注册码的拒绝路径**必须**带上它。
    /// 「允许不带 errorCode」这条放宽只服务于「确实没有码」的那一条路径，
    /// 放宽而不同时约束放宽本身，就会变成人人可以省略。
    /// </summary>
    [Fact]
    public void 有已注册码的拒绝路径必须带上该码()
    {
        using var harness = new AuthHarness();

        var first = harness.ValidCommand(nonce: "nonce-replay", requestId: 1);
        harness.Service.Authenticate(in first);
        harness.DrainAuditRecords();

        var second = harness.ValidCommand(nonce: "nonce-replay", requestId: 2);
        harness.Service.Authenticate(in second);

        var record = Assert.Single(harness.DrainAuditRecords());
        Assert.Equal(
            Lumio.Gen.ProtocolPermissionValidator.ProtocolGate.DeclaredOnlyReasons[0], record.ReasonCode);
        Assert.Contains(record.ReasonCode, Registered);
    }

    /// <summary>认证成功不产出任何 Audit —— 审计面只记拒绝，不记常态。</summary>
    [Fact]
    public void 认证成功不产出审计记录()
    {
        using var harness = new AuthHarness();
        var command = harness.ValidCommand();

        Assert.Equal(CredentialVerdict.Accepted, harness.Service.Authenticate(in command).Verdict);
        Assert.Empty(harness.DrainAuditRecords());
    }

    // ── 审计形状：镜像 fixture 是金标准

    /// <summary>
    /// 以镜像 <c>fixtures/valid/logging-auth-reject-audit.json</c> 为金标准逐字段比对；
    /// 字段集比对**另含 <c>eventId</c> 与 <c>timestamp</c>**（两者由 <c>Observability</c>
    /// 内部填充，<c>Auth</c> 不传：<c>eventId</c> 按 <c>event-{producerId}-{eventSeq}</c> 生成，
    /// <c>timestamp</c> 来自 <c>Platform</c> 的 <c>IWallClock</c>）。
    ///
    /// 同时断言 trace 镜像：每次成功写入 Audit，<c>IHostTraceSink</c> 上**恰镜像一条**。
    /// </summary>
    [Fact]
    public void 认证拒绝审计与镜像fixture同形且镜像给trace()
    {
        using var harness = new AuthHarness();

        var first = harness.ValidCommand(nonce: "nonce-replay", requestId: 1);
        harness.Service.Authenticate(in first);
        var second = harness.ValidCommand(nonce: "nonce-replay", requestId: 2);
        harness.Service.Authenticate(in second);

        var record = Assert.Single(harness.DrainAuditRecords());
        var produced = LoggingEventJson.From(record);
        var golden = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(
            AuthArchitecture.MvpHostRoot,
            "contract-mirror", "fixtures", "valid", "logging-auth-reject-audit.json")))!;

        Assert.Equal(
            golden.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal),
            produced.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));

        Assert.Equal("Audit", produced["category"]!.GetValue<string>());
        Assert.Equal("Warn", produced["severity"]!.GetValue<string>());
        Assert.Equal("Durable", produced["durability"]!.GetValue<string>());
        Assert.Equal("Applied", produced["redaction"]!.GetValue<string>());

        var correlation = (JsonObject)produced["correlation"]!;
        Assert.Equal("Release", correlation["scope"]!.GetValue<string>());
        Assert.Equal(AuthHarness.ReleasePoolId, correlation["releasePoolId"]!.GetValue<string>());
        Assert.False(correlation.ContainsKey("sessionId"));

        Assert.Matches("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", produced["eventId"]!.GetValue<string>());
        Assert.Equal(AuthHarness.FixedTimestamp, produced["timestamp"]!.GetValue<string>());

        Assert.Null(Wire.MvpSchemaGate.ValidateLoggingEvent(produced));

        // Auth 侧无需显式调用 Trace.Audit：镜像由 IAuditWriter 的实现完成。
        var mirrored = Assert.Single(harness.Trace.Audits);
        Assert.Equal("Release", mirrored.Correlation.Scope);
    }

    /// <summary>每写成功一条 Audit，trace 上就多恰一条镜像——不多不少。</summary>
    [Fact]
    public void 审计写入与trace镜像逐条一一对应()
    {
        using var harness = new AuthHarness();

        for (var i = 0; i < 3; i++)
        {
            var command = harness.WrongCredentialCommand($"nonce-{i}", (ulong)i);
            harness.Service.Authenticate(in command);
        }

        Assert.Equal(3, harness.Trace.Audits.Count);
        Assert.Equal(3, harness.DrainAuditRecords().Count);
    }

    // ── 凭据永不进日志

    /// <summary>
    /// 把一条已知字节串作为凭据走完一次失败认证，断言产出的全部 Audit 与 Diagnostic
    /// 记录的序列化文本中**不含**该字节串的任何编码形式（原文 / base64 / hex）。
    /// </summary>
    [Fact]
    public void 凭据不以任何编码形式出现在审计与诊断中()
    {
        using var harness = new AuthHarness();
        var command = harness.CanaryCommand();

        harness.Service.Authenticate(in command);

        var texts = harness.DrainAuditJson().Concat(harness.DrainDiagnosticJson()).ToList();
        Assert.NotEmpty(texts);

        var raw = Encoding.UTF8.GetString(AuthHarness.CanaryCredential);
        var base64 = Convert.ToBase64String(AuthHarness.CanaryCredential);
        var hex = Convert.ToHexString(AuthHarness.CanaryCredential);

        foreach (var text in texts)
        {
            Assert.DoesNotContain(raw, text, StringComparison.Ordinal);
            Assert.DoesNotContain(base64, text, StringComparison.Ordinal);
            Assert.DoesNotContain(hex, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Audit 背压不得静默放行

    /// <summary>
    /// <c>MvpAuditQueue</c> 达阈时认证路径产出「请求停止接纳新连接」的类型化结果，
    /// 而不是继续返回 Accept。**这是安全红线的机器化出口**：编排层必须读到一个值，
    /// 而不是记住一句纪律。
    /// </summary>
    [Fact]
    public void 审计队列达阈时认证路径停止接纳而不是放行()
    {
        using var harness = new AuthHarness(auditCapacity: 1);

        Assert.False(harness.Service.AdmissionMustStop);

        for (var i = 0; i < 2; i++)
        {
            var bad = harness.WrongCredentialCommand($"nonce-bad-{i}", (ulong)i);
            harness.Service.Authenticate(in bad);
        }

        Assert.True(harness.Observability.IsAuditBackpressured);
        Assert.True(harness.Service.AdmissionMustStop);

        var good = harness.ValidCommand(nonce: "nonce-good", requestId: 9);
        var outcome = harness.Service.Authenticate(in good);

        Assert.NotEqual(CredentialVerdict.Accepted, outcome.Verdict);
    }

    // ── auth 无自有线程

    /// <summary>
    /// **ArchUnitNET 调用依赖断言**：<c>Auth</c> 内不存在对 <c>INamedThreadSupervisor.Start</c>
    /// 的调用，也不存在任何 <c>Thread</c> / <c>Task.Run</c> 的使用——
    /// 认证在调用方（session 编排路径）上同步执行。
    /// </summary>
    [Theory]
    [InlineData("INamedThreadSupervisor", "Start")]
    [InlineData("System.Threading.Thread", "")]
    [InlineData("System.Threading.Tasks.Task", "Run")]
    public void Auth内不存在任何线程创建调用(string declaringHint, string methodHint)
    {
        var offenders = AuthArchitecture.AllMethodCallTargets()
            .Where(target => target.Contains(declaringHint, StringComparison.Ordinal)
                && (methodHint.Length == 0 || target.Contains(methodHint, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>类型级依赖同样不认识线程与线程监督面。</summary>
    [Theory]
    [InlineData("System.Threading.Thread")]
    [InlineData("Lumio.Server.MvpHost.Platform.INamedThreadSupervisor")]
    [InlineData("Lumio.Server.MvpHost.Platform.IThreadBody")]
    public void Auth的类型依赖里不出现线程面(string forbidden)
    {
        var offenders = AuthArchitecture.AllTypeDependencies()
            .Where(target => string.Equals(target, forbidden, StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// <c>MvpAuthEventQueue</c> 满载时写 diagnostic，**且不得丢成功 ack**——
    /// 成功 ack 进保留槽，随后仍可被取走。丢一条成功 ack 的后果是
    /// 一条已经通过认证的连接在编排层永远等不到回执。
    /// </summary>
    [Fact]
    public void 事件队列满载写诊断且不丢成功ack()
    {
        using var harness = new AuthHarness();

        var accepted = new AuthenticateOutcome(
            CredentialVerdict.Accepted, new PrincipalId("p"), AntiReplayVerdict.Ok, null, null);
        var rejected = new AuthenticateOutcome(
            CredentialVerdict.Rejected, default, AntiReplayVerdict.Ok, null, "rejected");

        for (var i = 0; i < AuthProvisionalDefaults.AuthEventQueueMaxItems; i++)
        {
            harness.Service.PublishOutcome(in rejected);
        }

        harness.Service.PublishOutcome(in accepted);

        Assert.NotEmpty(harness.DrainDiagnosticJson());

        var delivered = new List<AuthenticateOutcome>();
        while (harness.Service.TryDequeueOutcome(out var outcome))
        {
            delivered.Add(outcome);
        }

        Assert.Contains(delivered, o => o.Verdict == CredentialVerdict.Accepted);
    }

    [Fact]
    public void 成功结果保留槽有界且耗尽时进入故障路径()
    {
        using var harness = new AuthHarness();
        var rejected = new AuthenticateOutcome(
            CredentialVerdict.Rejected, default, AntiReplayVerdict.Ok, null, "rejected");
        var accepted = new AuthenticateOutcome(
            CredentialVerdict.Accepted, new PrincipalId("p"), AntiReplayVerdict.Ok, null, null);

        for (var i = 0; i < AuthProvisionalDefaults.AuthEventQueueMaxItems; i++)
        {
            harness.Service.PublishOutcome(in rejected);
        }

        for (var i = 0; i < AuthProvisionalDefaults.AuthEventQueueMaxItems; i++)
        {
            harness.Service.PublishOutcome(in accepted);
        }

        Assert.Throws<InvalidOperationException>(() => harness.Service.PublishOutcome(in accepted));
    }

    /// <summary>关闭后请求队列拒绝新请求，事件队列只交付已入队项。</summary>
    [Fact]
    public void 关闭后请求队列拒收而事件队列只交付存量()
    {
        using var harness = new AuthHarness();

        var queued = harness.ValidCommand("nonce-queued", 1);
        Assert.Equal(AuthQueueAdmission.Accepted, harness.Service.TryEnqueueRequest(in queued, out _));

        var outcome = new AuthenticateOutcome(
            CredentialVerdict.Accepted, new PrincipalId("p"), AntiReplayVerdict.Ok, null, null);
        harness.Service.PublishOutcome(in outcome);

        harness.Service.CloseQueues();

        var late = harness.ValidCommand("nonce-late", 2);
        Assert.Equal(AuthQueueAdmission.Closed, harness.Service.TryEnqueueRequest(in late, out var refused));
        Assert.False(refused.Accepted);

        Assert.True(harness.Service.TryDequeueOutcome(out _));
    }
}
