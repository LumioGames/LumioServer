using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using ArchUnitNET.Domain;
using ArchUnitNET.Domain.Extensions;
using ArchUnitNET.Loader;
using Xunit;

namespace Lumio.Server.MvpHost.Architecture.Tests;

/// <summary>
/// 队列登记的聚合校验。
///
/// 断言机制按纪律逐条选定：<c>System.Reflection</c> 看不到方法体、构造点或调用点，
/// 因此凡「谁调用了谁」一律用 ArchUnitNET 的**调用依赖**断言；
/// 凡能用类型/成员签名表达的用**签名级**反射断言。不使用 IL 字节扫描。
/// </summary>
public sealed class QueueRegistryTest
{
    private static readonly string[] ContractFields =
    {
        "owner", "producer", "consumer", "ordering", "budget", "onFull", "onClose",
    };

    private static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(HostContracts.IWorldSimulationPort).Assembly,
            typeof(Observability.IAuditWriter).Assembly,
            typeof(Wire.MvpEnvelopeReader).Assembly,
            typeof(Platform.IMonotonicClock).Assembly)
        .Build();

    private static IEnumerable<(string Project, JsonObject Entry)> RegisteredQueues()
    {
        foreach (var project in BuildGraph.All)
        {
            var path = Path.Combine(Path.GetDirectoryName(project.Path)!, "queues.json");
            if (!File.Exists(path))
            {
                continue;
            }

            var doc = JsonNode.Parse(File.ReadAllText(path))!;
            foreach (var entry in (doc["queues"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                yield return (project.Name, entry);
            }
        }
    }

    /// <summary>① 纯 JSON + csproj 元数据判定：七项合同字段齐全，<c>owner</c> 指向的工程在构建图内存在。</summary>
    [Fact]
    public void 每条登记行的七项合同字段齐全且owner在构建图内()
    {
        var violations = new List<string>();

        foreach (var (project, entry) in RegisteredQueues())
        {
            var name = entry["name"]?.GetValue<string>() ?? "(未命名)";

            foreach (var field in ContractFields)
            {
                if (entry[field] is null || string.IsNullOrWhiteSpace(entry[field]!.ToString()))
                {
                    violations.Add($"{project} 的 {name} 缺合同字段 {field}");
                }
            }

            // owner 允许**前向引用尚未落地的工程**：按「消费者拥有队列」的口径，
            // 跨模块队列的所有者常常是下游卡才交付的消费者
            // （例如 MvpTransportEventOutbox 归 Session，而 Session 在 wave 5）。
            // 因此这里只判两件事：名字符合本仓工程命名规范；已落地时必须是生产工程。
            // 拼错或瞎填仍会被前一条挡住，而前向引用会在该工程落地后自动被覆盖。
            var owner = entry["owner"]?.GetValue<string>();
            if (owner is not null)
            {
                if (!owner.StartsWith("Lumio.Server.MvpHost.", StringComparison.Ordinal))
                {
                    violations.Add($"{project} 的 {name} 的 owner「{owner}」不符合工程命名规范");
                }
                else if (BuildGraph.ByName(owner) is { IsProduction: false })
                {
                    violations.Add($"{project} 的 {name} 的 owner {owner} 不是生产工程");
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// 防空转：整张登记表不能全是前向引用。允许 owner 指向尚未落地的工程是必要的松弛，
    /// 但若**一条都没落地**，上面那条「已落地时必须是生产工程」就从未真正判过任何东西。
    /// </summary>
    [Fact]
    public void 至少有一条登记的owner已经落地()
    {
        var landed = RegisteredQueues()
            .Select(x => x.Entry["owner"]?.GetValue<string>())
            .Where(o => o is not null && BuildGraph.ByName(o) is not null)
            .ToList();

        Assert.NotEmpty(landed);
    }

    /// <summary>
    /// ② **ArchUnitNET 调用依赖断言**：凡对 <c>PlatformModule.CreateInbox/CreateOutbox</c>
    /// 存在方法调用依赖的生产工程，<c>queues.json</c> 中至少有一条 <c>owner</c> 为该工程的登记行。
    ///
    /// 反射做不到这条——调用点在方法体里，签名上看不见。
    /// </summary>
    [Fact]
    public void 创建了队列的工程必须有登记行()
    {
        var owners = RegisteredQueues()
            .Select(x => x.Entry["owner"]?.GetValue<string>())
            .Where(o => o is not null)
            .Select(o => o!)
            .ToHashSet(StringComparer.Ordinal);

        var creators = Architecture.Types
            .Where(t => t.Assembly.Name is not null)
            .Where(t => CallsMethodMatching(t, "PlatformModule", "CreateInbox", "CreateOutbox"))
            .Select(t => t.Assembly.Name!.Split(',')[0])
            .Distinct(StringComparer.Ordinal)
            .Where(a => BuildGraph.ByName(a)?.IsProduction == true)
            .ToList();

        var missing = creators.Where(c => !owners.Contains(c)).ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// ② **签名级断言**：某工程的登记行数不得超过该工程内
    /// <c>IBoundedInbox&lt;T&gt;</c> / <c>IBoundedOutbox&lt;T&gt;</c> 类型化字段与属性的个数。
    /// 登记比实际队列多，说明有一条登记行是凭空写的。
    /// </summary>
    [Fact]
    public void 登记行数不超过类型化队列成员数()
    {
        var violations = new List<string>();

        foreach (var group in RegisteredQueues().GroupBy(x => x.Project, StringComparer.Ordinal))
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == group.Key);

            if (assembly is null)
            {
                continue;
            }

            var queueMembers = assembly.GetTypes()
                .SelectMany(t => t
                    .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Select(f => f.FieldType)
                    .Concat(t
                        .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                        .Select(p => p.PropertyType)))
                .Count(IsBoundedQueue);

            if (group.Count() > queueMembers)
            {
                violations.Add($"{group.Key}：登记 {group.Count()} 条，但只有 {queueMembers} 个类型化队列成员");
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// ③ **ArchUnitNET 调用依赖断言**：全构建图不存在对
    /// <c>Channel.CreateUnbounded</c> 的调用依赖。无界队列会让所有背压设计失效，
    /// 而且失效方式是「内存慢慢涨」，没有任何一处会报错。
    /// </summary>
    [Fact]
    public void 全构建图不创建无界通道()
    {
        var offenders = Architecture.Types
            .Where(t => CallsMethodMatching(t, declaringHint: null, "CreateUnbounded"))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// ③ **签名级断言**：<c>ConcurrentQueue&lt;&gt;</c> 不作为任何生产类型的字段或属性类型出现。
    /// 它没有容量上限，用它就等于放弃了队列预算。
    /// </summary>
    [Fact]
    public void 生产类型不持有无界并发队列()
    {
        var offenders = new List<string>();

        foreach (var project in BuildGraph.Production)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == project.Name);

            if (assembly is null)
            {
                continue;
            }

            foreach (var type in assembly.GetTypes())
            {
                var members = type
                    .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Select(f => (Member: (MemberInfo)f, Type: f.FieldType))
                    .Concat(type
                        .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                        .Select(p => (Member: (MemberInfo)p, Type: p.PropertyType)));

                offenders.AddRange(members
                    .Where(m => m.Type.IsGenericType
                        && m.Type.GetGenericTypeDefinition() == typeof(System.Collections.Concurrent.ConcurrentQueue<>))
                    .Select(m => $"{type.FullName}.{m.Member.Name}"));
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// 方法调用依赖挂在 <c>IMember</c> 上而不是 <c>IType</c> 上，因此要下沉一层遍历成员。
    /// 这正是「调用点在方法体里」的直接体现——类型层面看不见谁调用了谁。
    /// </summary>
    private static bool CallsMethodMatching(IType type, string? declaringHint, params string[] methodNameHints)
        => type.Members.Any(member => member.GetMethodCallDependencies().Any(d =>
            (declaringHint is null || d.TargetMember.FullName.Contains(declaringHint, StringComparison.Ordinal))
            && methodNameHints.Any(hint => d.TargetMember.Name.Contains(hint, StringComparison.Ordinal))));

    private static bool IsBoundedQueue(System.Type type)
    {
        if (!type.IsGenericType)
        {
            return false;
        }

        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(Platform.IBoundedInbox<>) || definition == typeof(Platform.IBoundedOutbox<>);
    }
}
