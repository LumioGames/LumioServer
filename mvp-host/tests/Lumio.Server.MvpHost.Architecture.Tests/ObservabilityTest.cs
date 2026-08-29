using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using ArchUnitNET.Domain.Extensions;
using ArchUnitNET.Loader;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.TestKit;
using Xunit;

namespace Lumio.Server.MvpHost.Architecture.Tests;

/// <summary>
/// Audit / Diagnostic 两条写入面的形状与 ADR-011 强制。
/// </summary>
public sealed class ObservabilityTest
{
    private const string FixedTimestamp = "2026-08-27T00:10:00Z";

    private static readonly HostIdentity Identity = new("A", "A-1.1.0", "server-auth");

    private static readonly string[] AlwaysRequiredFields =
    {
        "productId", "gameReleaseId", "traceId", "producerId", "eventSeq",
    };

    private static readonly string[] TraceSinkMembers = { "Ack", "Audit", "State" };

    private static (ObservabilityServices Services, IBoundedInbox<AuditRecord> Audit, IBoundedInbox<DiagnosticRecord> Diagnostic, RecordingHostTraceSink Trace)
        Build(int auditCapacity = 8)
    {
        var auditInbox = PlatformModule.CreateInbox<AuditRecord>(new QueueBudget(auditCapacity, 65536));
        var diagnosticInbox = PlatformModule.CreateInbox<DiagnosticRecord>(new QueueBudget(8, 65536));
        var trace = new RecordingHostTraceSink();
        var services = ObservabilityModule.Create(auditInbox, diagnosticInbox, new FakeWallClock(FixedTimestamp), trace, Identity);
        return (services, auditInbox, diagnosticInbox, trace);
    }

    /// <summary>
    /// ADR-011 的 REQUIRED / FORBIDDEN 两张表逐条机器强制。
    /// **Release scope 出现 <c>sessionId</c> 即失败**——那是在伪造关联。
    /// </summary>
    [Theory]
    [InlineData("Process", "sessionId")]
    [InlineData("Process", "worldId")]
    [InlineData("Process", "tickId")]
    [InlineData("Process", "txnId")]
    [InlineData("Release", "sessionId")]
    [InlineData("Release", "worldId")]
    [InlineData("Release", "tickId")]
    [InlineData("Release", "txnId")]
    [InlineData("Session", "worldId")]
    [InlineData("Session", "tickId")]
    [InlineData("Session", "txnId")]
    [InlineData("World", "txnId")]
    public void 被禁字段登记在FORBIDDEN表内(string scope, string forbidden)
        => Assert.Contains(forbidden, CorrelationScopeRules.ForbiddenFor(scope));

    [Theory]
    [InlineData("Release", "releasePoolId")]
    [InlineData("Session", "sessionId")]
    [InlineData("World", "worldId")]
    [InlineData("World", "tickId")]
    [InlineData("Txn", "txnId")]
    [InlineData("Txn", "worldId")]
    [InlineData("Txn", "tickId")]
    public void 必填字段登记在REQUIRED表内(string scope, string required)
        => Assert.Contains(required, CorrelationScopeRules.RequiredFor(scope));

    [Fact]
    public void 五个基础字段恒必填且Process无额外必填()
    {
        Assert.Equal(AlwaysRequiredFields, CorrelationScopeRules.AlwaysRequired);

        Assert.Empty(CorrelationScopeRules.RequiredFor("Process"));
    }

    [Fact]
    public void Release作用域带上sessionId即被判违规()
    {
        var correlation = new CorrelationView(
            "Release", "A", "A-1.1.0", "pool-a-1.1", SessionId: "session-001",
            null, null, null, "trace-1", "server-auth", 1);

        var violation = CorrelationScopeRules.Validate(correlation);

        Assert.NotNull(violation);
        Assert.Contains("sessionId", violation, StringComparison.Ordinal);
    }

    /// <summary>
    /// 用镜像 fixture 作金标准：本仓产出的认证拒绝审计事件，字段集与取值必须与之相同。
    /// </summary>
    [Fact]
    public void 认证拒绝审计事件与镜像fixture同形()
    {
        var (services, inbox, _, _) = Build();

        services.Audit.WriteReleaseScopedReject(
            releasePoolId: "pool-a-1.1",
            productId: "A",
            gameReleaseId: "A-1.1.0",
            traceId: "trace-auth-reject-0001",
            producerId: "server-auth",
            eventSeq: 42,
            reasonCode: "ReleaseMismatch");

        Assert.True(inbox.TryDequeue(out var record));

        var produced = LoggingEventJson.From(record);
        var golden = (JsonObject)JsonNode.Parse(
            File.ReadAllText(Path.Combine(
                BuildGraph.MvpHostRoot, "contract-mirror", "fixtures", "valid", "logging-auth-reject-audit.json")))!;

        Assert.Equal(
            golden.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal),
            produced.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));

        Assert.Equal("Audit", produced["category"]!.GetValue<string>());
        Assert.Equal("Warn", produced["severity"]!.GetValue<string>());
        Assert.Equal("Durable", produced["durability"]!.GetValue<string>());
        Assert.Equal("Applied", produced["redaction"]!.GetValue<string>());

        var correlation = (JsonObject)produced["correlation"]!;
        Assert.Equal("Release", correlation["scope"]!.GetValue<string>());
        Assert.True(correlation.ContainsKey("releasePoolId"));
        Assert.False(correlation.ContainsKey("sessionId"));

        // eventId / timestamp 是 required 成员，缺任一项即产不出合法事件。
        Assert.Matches("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", produced["eventId"]!.GetValue<string>());
        Assert.Matches(
            "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,9})?Z$",
            produced["timestamp"]!.GetValue<string>());
    }

    /// <summary>
    /// 两种记录序列化后都必须过镜像 <c>logging-event.schema.json</c> 的结构层校验。
    /// 该 schema <c>additionalProperties: false</c>，多写一个成员同样过不了——
    /// 这条与上一条互为交叉验证。
    /// </summary>
    [Fact]
    public void 两种记录都通过日志事件的结构层校验()
    {
        var (services, auditInbox, diagnosticInbox, _) = Build();

        services.Audit.WriteSessionScoped(
            new ServerSessionId("session-001"), "A", "A-1.1.0", "trace-1", "server-session", 7, "session admitted");
        services.Diagnostics.Write("Diagnostic", "Info", "queue drained");

        Assert.True(auditInbox.TryDequeue(out var audit));
        Assert.True(diagnosticInbox.TryDequeue(out var diagnostic));

        foreach (var (label, json) in new[]
                 {
                     ("audit", LoggingEventJson.From(audit)),
                     ("diagnostic", LoggingEventJson.From(diagnostic)),
                 })
        {
            var failure = LoggingEventSchema.Validate(json);
            Assert.True(failure is null, $"{label} 未通过 logging-event schema：{failure}");
        }
    }

    [Fact]
    public void 时间戳的唯一来源是墙钟出口()
    {
        var (services, inbox, _, _) = Build();

        services.Audit.WriteSessionScoped(
            new ServerSessionId("session-001"), "A", "A-1.1.0", "trace-1", "server-session", 1, "m");

        Assert.True(inbox.TryDequeue(out var record));
        Assert.Equal(FixedTimestamp, record.Timestamp);
    }

    /// <summary>
    /// 队列满时**不得让调用方静默通过**：如实回 <c>Full</c>、置背压、产出请求关闸的类型化事件。
    /// </summary>
    [Fact]
    public void 审计队列达阈时请求关闸而不静默放行()
    {
        var (services, _, _, _) = Build(auditCapacity: 1);

        var first = services.Audit.WriteSessionScoped(
            new ServerSessionId("s"), "A", "A-1.1.0", "t", "p", 1, "m1");
        Assert.Equal(EnqueueStatus.Accepted, first.Status);
        Assert.False(services.IsAuditBackpressured);

        var second = services.Audit.WriteSessionScoped(
            new ServerSessionId("s"), "A", "A-1.1.0", "t", "p", 2, "m2");

        Assert.Equal(EnqueueStatus.Full, second.Status);
        Assert.True(services.IsAuditBackpressured);
    }

    /// <summary>写入成功后自动镜像给 trace，因此 Auth 侧无需显式调用。</summary>
    [Fact]
    public void 审计写入成功后自动镜像给trace()
    {
        var (services, _, _, trace) = Build();

        services.Audit.WriteReleaseScopedReject("pool", "A", "A-1.1.0", "t", "p", 1, "ReleaseMismatch");

        Assert.Single(trace.Audits);
        Assert.Equal("Release", trace.Audits[0].Correlation.Scope);
    }

    /// <summary>生产 Profile 的 trace sink 只写不查——三个方法全部空实现，且接口无任何查询成员。</summary>
    [Fact]
    public void trace面只写没有任何查询方法()
    {
        var members = typeof(IHostTraceSink).GetMethods();

        Assert.Equal(TraceSinkMembers, members.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(members, m => Assert.Equal(typeof(void), m.ReturnType));
        Assert.Empty(typeof(IHostTraceSink).GetProperties());
    }

    /// <summary>
    /// **ArchUnitNET 调用依赖断言**：<c>IWallClock</c> 只被 Observability 消费。
    /// 墙钟一旦散播到别处，超时与窗口判定迟早会误用它——而那类 bug 只在跨时区或跨闰秒时显形。
    /// </summary>
    [Fact]
    public void 墙钟只被可观测层消费()
    {
        var architecture = new ArchLoader()
            .LoadAssemblies(
                typeof(IWorldSimulationPort).Assembly,
                typeof(IAuditWriter).Assembly,
                typeof(Wire.MvpEnvelopeReader).Assembly)
            .Build();

        var offenders = architecture.Types
            .Where(t => t.Members.Any(m => m.GetMethodCallDependencies().Any(d =>
                d.TargetMember.FullName.Contains("IWallClock", StringComparison.Ordinal))))
            .Where(t => !(t.Assembly.Name ?? string.Empty).StartsWith("Lumio.Server.MvpHost.Observability", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(offenders);
    }
}

/// <summary>
/// 用镜像 <c>logging-event.schema.json</c> 做结构层校验。
/// 校验器本体是 <c>Wire</c> 的 <c>JsonSchemaValidator</c>（internal），这里经其公开入口
/// <c>MvpSchemaGate</c> 复用——本测试工程**不重写第二个校验器**：
/// 两个校验器迟早会在某条构造上分歧，而分歧的表现是「一边放行一边拦」。
/// </summary>
internal static class LoggingEventSchema
{
    internal static string? Validate(JsonObject candidate) => Wire.MvpSchemaGate.ValidateLoggingEvent(candidate);
}
