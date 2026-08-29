using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Lumio.Server.MvpHost.Architecture.Tests;

/// <summary>
/// <c>absences.json</c> 的结构与引用完整性。
///
/// 这份清单是「本仓明知缺席、且承诺不越界实现」的唯一登记处。它的价值全在
/// **每条都指得回一个真实位置**——一条 <c>source</c> 指向不存在的路径的登记，
/// 读起来像有依据，实际什么都没说。
/// </summary>
public sealed class AbsencesManifestTest
{
    private static readonly string[] ReasonVocabulary =
    {
        "载体已提供", "阶段未到", "决策门冻结", "实现方为 P1",
    };

    /// <summary>每条登记恰含的五个字段名（有序，供集合比较）。</summary>
    private static readonly string[] EntryFields = { "clause", "id", "reason", "source", "successor" };

    /// <summary>
    /// 由 <c>scaffold-mvp-host-build-baseline</c> 一次性写全的 19 条。
    ///
    /// **断言的是「这 19 个 id 都在册」，不是「条目数恰为 19」**：卡面原文写死条数，
    /// 但那会和正在进行的 <c>absences.json</c> 修订批次（补登记 <c>Handshake.role</c>
    /// 私有约定等）直接冲突——追加一条合法登记不该让本测试变红。
    /// 「已知 id 全在册」同样挡住删条目，而且不挡追加，与「计数断言必然腐烂」的裁决一致。
    /// </summary>
    private static readonly string[] KnownIds =
    {
        "ABS-WORLDSLOT-NATIVE",
        "ABS-WORLDSLOT-DEFERRED-TRANSITIONS",
        "ABS-SESSION-FAULTED-UNREACHABLE",
        "ABS-RELEASE-EXACTMATCH",
        "ABS-RELEASE-MEMBER-HEALTH",
        "ABS-AUDIT-DURABLE-ACK",
        "ABS-FAILURE-BUNDLE",
        "ABS-PERSISTENCE-SNAPSHOT",
        "ABS-MAINTENANCE-CONTROLPLANE",
        "ABS-ENVELOPE-POCO",
        "ABS-PERMISSION-VALIDATOR",
        "ABS-WIRE-FRAGMENTATION",
        "ABS-TRANSPORT-PROFILE-ID",
        "ABS-LENGTH-SEMANTICS",
        "ABS-AUTH-CREDENTIAL-ERRORCODE",
        "ABS-CLIENT-UPLINK-COMMAND",
        "ABS-REPLICATION-STATE-PAYLOAD",
        "ABS-REPLICATION-MAPPING-SET",
        "ABS-AUTH-CREDENTIAL-CARRIAGE",
    };

    private static JsonObject Manifest()
        => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(BuildGraph.MvpHostRoot, "absences.json")))!;

    private static IEnumerable<JsonObject> Entries()
        => (Manifest()["absences"] as JsonArray ?? new JsonArray()).OfType<JsonObject>();

    [Fact]
    public void 清单是合法JSON且基线号相符()
    {
        Assert.Equal("LGE-V1.4-2026-08-27", Manifest()["baselineId"]!.GetValue<string>());
    }

    [Fact]
    public void 已知的十九条登记全部在册且id唯一()
    {
        var ids = Entries().Select(e => e["id"]!.GetValue<string>()).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        var missing = KnownIds.Where(known => !ids.Contains(known, StringComparer.Ordinal)).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void 每条恰含五个字段且取值合法()
    {
        var violations = new List<string>();

        foreach (var entry in Entries())
        {
            var id = entry["id"]?.GetValue<string>() ?? "(无 id)";

            var fields = entry.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (!fields.SequenceEqual(EntryFields, StringComparer.Ordinal))
            {
                violations.Add($"{id} 的字段集是 [{string.Join(", ", fields)}]，应恰为五项");
                continue;
            }

            var reason = entry["reason"]!.GetValue<string>();
            if (!ReasonVocabulary.Contains(reason, StringComparer.Ordinal))
            {
                violations.Add($"{id} 的 reason「{reason}」不在四值词表内");
            }

            if (string.IsNullOrWhiteSpace(entry["successor"]!.GetValue<string>()))
            {
                violations.Add($"{id} 的 successor 为空");
            }

            if (string.IsNullOrWhiteSpace(entry["clause"]!.GetValue<string>()))
            {
                violations.Add($"{id} 的 clause 为空");
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// 每条 <c>source</c> 指向的路径必须真实存在。<c>contract-mirror/</c> 内的路径按
    /// <c>mvp-host/</c> 相对解析，其余按仓库根解析。
    /// </summary>
    [Fact]
    public void 每条source指向的路径都存在()
    {
        var repoRoot = Directory.GetParent(BuildGraph.MvpHostRoot)!.FullName;
        var missing = new List<string>();

        foreach (var entry in Entries())
        {
            var id = entry["id"]!.GetValue<string>();
            var source = entry["source"]!.GetValue<string>();

            var underMvpHost = Path.Combine(BuildGraph.MvpHostRoot, source);
            var underRepo = Path.Combine(repoRoot, source);

            if (!File.Exists(underMvpHost) && !Directory.Exists(underMvpHost)
                && !File.Exists(underRepo) && !Directory.Exists(underRepo))
            {
                missing.Add($"{id} → {source}");
            }
        }

        Assert.Empty(missing);
    }
}
