using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Lumio.Server.MvpHost.Observability;

/// <summary>
/// ADR-011 的 REQUIRED / FORBIDDEN 两张表，机器强制。
///
/// **FORBIDDEN 比 REQUIRED 更要紧**：漏填一个字段是可见的缺陷，
/// 而多填一个字段（例如 Release 作用域里带上 <c>sessionId</c>）是**伪造关联**——
/// 认证在 session 创建之前就失败了，那条 audit 里的 sessionId 只能是编造的。
/// 这正是 <c>fixtures/valid/logging-auth-reject-audit.json</c> 用 Release 作用域
/// 且不带 sessionId 的原因。
/// </summary>
public static class CorrelationScopeRules
{
    /// <summary>五个基础字段恒必填，与作用域无关。</summary>
    public static ImmutableArray<string> AlwaysRequired { get; } =
        ImmutableArray.Create("productId", "gameReleaseId", "traceId", "producerId", "eventSeq");

    public static ImmutableArray<string> Scopes { get; } =
        ImmutableArray.Create("Process", "Release", "Session", "World", "Txn");

    private static readonly Dictionary<string, string[]> RequiredByScope = new(StringComparer.Ordinal)
    {
        ["Process"] = Array.Empty<string>(),
        ["Release"] = new[] { "releasePoolId" },
        ["Session"] = new[] { "sessionId" },
        ["World"] = new[] { "worldId", "tickId" },
        ["Txn"] = new[] { "txnId", "worldId", "tickId" },
    };

    private static readonly Dictionary<string, string[]> ForbiddenByScope = new(StringComparer.Ordinal)
    {
        ["Process"] = new[] { "sessionId", "worldId", "tickId", "txnId" },
        ["Release"] = new[] { "sessionId", "worldId", "tickId", "txnId" },
        ["Session"] = new[] { "worldId", "tickId", "txnId" },
        ["World"] = new[] { "txnId" },
        ["Txn"] = Array.Empty<string>(),
    };

    public static ImmutableArray<string> RequiredFor(string scope)
        => RequiredByScope.TryGetValue(scope, out var fields)
            ? fields.ToImmutableArray()
            : throw new ArgumentOutOfRangeException(nameof(scope), scope, "未注册的 correlation scope");

    public static ImmutableArray<string> ForbiddenFor(string scope)
        => ForbiddenByScope.TryGetValue(scope, out var fields)
            ? fields.ToImmutableArray()
            : throw new ArgumentOutOfRangeException(nameof(scope), scope, "未注册的 correlation scope");

    /// <summary>校验通过返回 null，否则返回首条违规说明。</summary>
    public static string? Validate(in CorrelationView correlation)
    {
        if (!Scopes.Contains(correlation.Scope, StringComparer.Ordinal))
        {
            return $"未注册的 correlation scope：{correlation.Scope}";
        }

        foreach (var field in AlwaysRequired)
        {
            if (!HasValue(correlation, field))
            {
                return $"scope={correlation.Scope} 缺恒必填字段 {field}";
            }
        }

        foreach (var field in RequiredFor(correlation.Scope))
        {
            if (!HasValue(correlation, field))
            {
                return $"scope={correlation.Scope} 缺必填字段 {field}";
            }
        }

        foreach (var field in ForbiddenFor(correlation.Scope))
        {
            if (HasValue(correlation, field))
            {
                return $"scope={correlation.Scope} 出现被禁字段 {field}——这是在伪造关联";
            }
        }

        return null;
    }

    private static bool HasValue(in CorrelationView c, string field) => field switch
    {
        "productId" => !string.IsNullOrEmpty(c.ProductId),
        "gameReleaseId" => !string.IsNullOrEmpty(c.GameReleaseId),
        "traceId" => !string.IsNullOrEmpty(c.TraceId),
        "producerId" => !string.IsNullOrEmpty(c.ProducerId),

        // eventSeq 是 ulong：0 是合法序号，不能拿「非零」当「有值」。
        "eventSeq" => true,

        "releasePoolId" => !string.IsNullOrEmpty(c.ReleasePoolId),
        "sessionId" => !string.IsNullOrEmpty(c.SessionId),
        "worldId" => !string.IsNullOrEmpty(c.WorldId),
        "tickId" => c.TickId is not null,
        "txnId" => !string.IsNullOrEmpty(c.TxnId),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "未知 correlation 字段"),
    };
}
