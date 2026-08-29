using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Lumio.Server.MvpHost.Platform;
using Xunit;

namespace Lumio.Server.MvpHost.Auth.Tests;

/// <summary>
/// 工程形状与队列登记。判据从 csproj 与 <c>queues.json</c> 读出——
/// 一条 <c>ProjectReference</c> 即使当下没有任何代码用到，也已经把边建立了。
/// </summary>
public sealed class AuthProjectShapeTest
{
    private static readonly string[] ContractFields =
    {
        "owner", "producer", "consumer", "ordering", "budget", "onFull", "onClose",
    };

    private static readonly string[] ExpectedReferences =
    {
        "Lumio.Server.MvpHost.HostContracts", "Lumio.Server.MvpHost.Observability",
    };

    private static readonly string[] ForbiddenReferences =
    {
        "Lumio.Server.MvpHost.Transport",
        "Lumio.Server.MvpHost.Session",
        "Lumio.Server.MvpHost.WorldSlot",
    };

    private static string ProjectDir => Path.Combine(
        AuthArchitecture.MvpHostRoot, "src", "Lumio.Server.MvpHost.Auth");

    private static XDocument Csproj() => XDocument.Load(
        Path.Combine(ProjectDir, "Lumio.Server.MvpHost.Auth.csproj"));

    private static List<string> ProjectReferences() => Csproj()
        .Descendants("ProjectReference")
        .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))
        .OrderBy(v => v, StringComparer.Ordinal)
        .ToList();

    [Fact]
    public void 工程声明第四层()
        => Assert.Equal("4", Csproj().Descendants("MvpHostLayer").Single().Value);

    [Fact]
    public void 工程引用恰为契约层与可观测层两个()
        => Assert.Equal(ExpectedReferences, ProjectReferences());

    /// <summary>
    /// 红线边一条都不能有。<c>Auth ↮ Transport</c> 是 Rust 侧原文的红线；
    /// <c>Auth ↛ Session</c> 是「gate 执行体归 Auth、调用方是 Session、两者相互零引用」的前提。
    /// </summary>
    [Theory]
    [InlineData("Lumio.Server.MvpHost.Transport")]
    [InlineData("Lumio.Server.MvpHost.Session")]
    [InlineData("Lumio.Server.MvpHost.WorldSlot")]
    public void 不引用同层的任何其他模块(string forbidden)
        => Assert.DoesNotContain(forbidden, ProjectReferences());

    [Fact]
    public void 禁边表非空以免上一条空真通过()
        => Assert.NotEmpty(ForbiddenReferences);

    /// <summary>生产工程零 <c>PackageReference</c>：分析包与测试栈都不得进生产侧。</summary>
    [Fact]
    public void 生产工程不引用任何包()
        => Assert.Empty(Csproj().Descendants("PackageReference"));

    // ── 队列登记

    private static List<JsonObject> RegisteredQueues()
    {
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(ProjectDir, "queues.json")))!;
        return (doc["queues"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
    }

    [Fact]
    public void 登记了请求队列与事件队列两条()
    {
        var names = RegisteredQueues().Select(q => q["name"]!.GetValue<string>()).ToList();

        Assert.Contains("MvpAuthRequestQueue", names);
        Assert.Contains("MvpAuthEventQueue", names);
    }

    [Fact]
    public void 每条登记行的七项合同字段齐全()
    {
        var violations = new List<string>();

        foreach (var entry in RegisteredQueues())
        {
            var name = entry["name"]?.GetValue<string>() ?? "(未命名)";
            foreach (var field in ContractFields)
            {
                if (entry[field] is null || string.IsNullOrWhiteSpace(entry[field]!.ToString()))
                {
                    violations.Add($"{name} 缺合同字段 {field}");
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// <c>onFull</c> 的写法与 world-slot 卡给 <c>AggregateBusy</c> 的同口径：
    /// 内部忙碌状态**逐字**标注它映射到哪个已注册码，登记行本身就把映射写清楚。
    /// </summary>
    [Fact]
    public void 请求队列的满载动作逐字写明内部码到已注册码的映射()
    {
        var entry = RegisteredQueues().Single(q => q["name"]!.GetValue<string>() == "MvpAuthRequestQueue");

        Assert.Contains("AuthBusy（映射 QueueFull）", entry["onFull"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>事件队列按「消费者拥有队列」的口径归 Session（wave 5 落地，属允许的前向引用）。</summary>
    [Fact]
    public void 事件队列的所有者是消费者而不是生产者()
    {
        var entry = RegisteredQueues().Single(q => q["name"]!.GetValue<string>() == "MvpAuthEventQueue");

        Assert.Equal("Lumio.Server.MvpHost.Session", entry["owner"]!.GetValue<string>());
        Assert.Equal("Lumio.Server.MvpHost.Auth", RegisteredQueues()
            .Single(q => q["name"]!.GetValue<string>() == "MvpAuthRequestQueue")["owner"]!.GetValue<string>());
    }

    /// <summary>
    /// **签名级断言**：登记行数不得超过本程序集内 <c>IBoundedInbox&lt;T&gt;</c> /
    /// <c>IBoundedOutbox&lt;T&gt;</c> 类型化成员的个数——登记比实际队列多，
    /// 说明有一条登记行是凭空写的。
    /// </summary>
    [Fact]
    public void 登记行数不超过类型化队列成员数()
    {
        var queueMembers = AuthArchitecture.AuthAssembly.GetTypes()
            .SelectMany(t => t
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(f => f.FieldType)
                .Concat(t
                    .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                        | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Select(p => p.PropertyType)))
            .Count(IsBoundedQueue);

        Assert.True(
            RegisteredQueues().Count <= queueMembers,
            $"登记 {RegisteredQueues().Count} 条，但只有 {queueMembers} 个类型化队列成员");
    }

    /// <summary>本工程的队列一律经 <c>PlatformModule</c> 创建，绝不自建无界通道。</summary>
    [Fact]
    public void 队列一律经Platform创建且不创建无界通道()
    {
        var targets = AuthArchitecture.AllMethodCallTargets().ToList();

        Assert.Contains(targets, t => t.Contains("PlatformModule", StringComparison.Ordinal));
        Assert.DoesNotContain(targets, t => t.Contains("CreateUnbounded", StringComparison.Ordinal));
    }

    // ── 缺席登记：本卡引用的两条必须在册（不追加、只校验）

    [Theory]
    [InlineData("ABS-AUTH-CREDENTIAL-ERRORCODE")]
    [InlineData("ABS-AUTH-CREDENTIAL-CARRIAGE")]
    public void 本卡引用的缺席登记在册(string id)
    {
        var manifest = (JsonObject)JsonNode.Parse(
            File.ReadAllText(Path.Combine(AuthArchitecture.MvpHostRoot, "absences.json")))!;

        var ids = (manifest["absences"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(e => e["id"]!.GetValue<string>())
            .ToList();

        Assert.Contains(id, ids);
    }

    private static bool IsBoundedQueue(Type type)
        => type.IsGenericType
            && (type.GetGenericTypeDefinition() == typeof(IBoundedInbox<>)
                || type.GetGenericTypeDefinition() == typeof(IBoundedOutbox<>));
}
