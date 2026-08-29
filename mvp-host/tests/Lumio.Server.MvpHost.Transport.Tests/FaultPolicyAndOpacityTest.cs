using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ArchUnitNET.Domain.Extensions;
using ArchUnitNET.Loader;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.TestKit;
using Xunit;

namespace Lumio.Server.MvpHost.Transport.Tests;

/// <summary>
/// 故障策略的注入方式，与 <c>PermissionGrantRef</c> 的不透明性。
///
/// 断言机制按设计 §4.3 的纪律选定：<c>System.Reflection</c> 看不到方法体与构造点，
/// 凡「谁调用了谁」一律用 ArchUnitNET 的调用依赖断言；能用签名表达的用签名级反射。
/// 不使用 IL 字节扫描，不引入任何未冻结的分析包。
/// </summary>
public sealed class FaultPolicyAndOpacityTest
{
    private static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader()
        .LoadAssemblies(typeof(TransportService).Assembly)
        .Build();

    /// <summary>
    /// 故障装饰器在两处各挂一次：解码后 / ingress 入队前，egress 出队后 / 交 carrier 前。
    /// 位置刻意与 LumioClient 的 <c>FaultDecoratingTransport</c> 对称，
    /// 使双端故障脚本共用同一 <c>TransportFaultContext{Seed, Sequence}</c> 口径。
    /// </summary>
    [Fact]
    public void 故障策略在入站与出站两个挂点各被调用一次()
    {
        var policy = new RecordingFaultPolicy();
        using var harness = new TransportHarness(policy);

        // 首帧走握手分支（那一步还没有 ingress 可入队，因此不挂故障装饰器）；
        // 第二帧才是「解码后、ingress 入队前」这个挂点真正生效的地方。
        var id = ConnectionLifecycleTest.AcceptAndValidate(harness);

        harness.Carrier.QueueInbound(id, TransportHarness.ValidEnvelope(sequence: 2));
        harness.Service.PumpReceiveOnce(id);

        harness.Service.TryEnqueue(id, harness.Service.EpochOf(id), new OutboundEnvelopeBytes(TransportHarness.ValidEnvelope()));
        harness.Service.PumpSendOnce(id);

        Assert.Contains(policy.Contexts, c => c.IsIngress);
        Assert.Contains(policy.Contexts, c => !c.IsIngress);
    }

    /// <summary>
    /// ① **签名级**：<c>Create</c> 的 <c>ITransportFaultPolicy</c> 参数存在且**无默认值**，
    /// 且 <c>TransportService</c> 内没有任何具体策略类型的字段——只有接口类型的。
    ///
    /// 有默认值的话，「生产用的是哪个策略」就成了要读调用点才知道的事，
    /// 而漏传时它会静默变成 pass-through。
    /// </summary>
    [Fact]
    public void 故障策略是必填构造参数且字段只有接口类型()
    {
        var create = typeof(TransportService).GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(create);

        var parameter = create.GetParameters().FirstOrDefault(p => p.ParameterType == typeof(ITransportFaultPolicy));
        Assert.NotNull(parameter);
        Assert.False(parameter.HasDefaultValue, "故障策略参数不得有默认值");

        var concreteFields = typeof(TransportService)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(f => typeof(ITransportFaultPolicy).IsAssignableFrom(f.FieldType) && f.FieldType != typeof(ITransportFaultPolicy))
            .Select(f => $"{f.Name}: {f.FieldType.Name}")
            .ToList();

        Assert.Empty(concreteFields);
    }

    /// <summary>
    /// ② **ArchUnitNET 调用依赖**：本程序集内不存在对 <c>PassThroughFaultPolicy</c>
    /// 构造函数的调用依赖——唯一构造点在组装根 <c>App</c>，不在本程序集内。
    ///
    /// 这条不能写成「反射断言不存在 new 硬编码」：反射看不到构造点，
    /// 而 <c>PassThroughFaultPolicy</c> 本就住在本程序集里，类型存在不等于被构造。
    /// </summary>
    [Fact]
    public void 本程序集内不构造放行策略()
    {
        var offenders = Architecture.Types
            .Where(t => !t.FullName.Contains("PassThroughFaultPolicy", StringComparison.Ordinal))
            .Where(t => t.Members.Any(m => m.GetMethodCallDependencies().Any(d =>
                d.TargetMember.FullName.Contains("PassThroughFaultPolicy", StringComparison.Ordinal)
                && d.TargetMember.Name.Contains(".ctor", StringComparison.Ordinal))))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// ① **签名级**：<c>PermissionGrantRef</c> 只作为字段 / 参数 / 返回类型出现，
    /// 不存在任何以其内部数值为入参的判定方法（无 <c>bool XxxFrom(ulong)</c> 形态）。
    /// </summary>
    [Fact]
    public void 授权引用不被任何判定方法解释()
    {
        var suspicious = new List<string>();

        foreach (var type in typeof(TransportService).Assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.ReturnType != typeof(bool))
                {
                    continue;
                }

                var takesRawGrantValue = method.GetParameters().Any(p =>
                    p.ParameterType == typeof(ulong)
                    && p.Name is not null
                    && p.Name.Contains("grant", StringComparison.OrdinalIgnoreCase));

                if (takesRawGrantValue)
                {
                    suspicious.Add($"{type.FullName}.{method.Name}");
                }
            }
        }

        Assert.Empty(suspicious);
    }

    /// <summary>
    /// ② **ArchUnitNET 调用依赖**：对 <c>PermissionGrantRef.Value</c> getter 的调用依赖数为 0。
    /// 相等比较用 <c>record struct</c> 自带的 <c>Equals</c>，不读取内部数值——
    /// 一旦读了数值，transport 就开始「理解」授权，而它的职责只是搬运。
    /// </summary>
    [Fact]
    public void 授权引用的内部数值从未被读取()
    {
        var offenders = Architecture.Types
            .Where(t => t.Members.Any(m => m.GetMethodCallDependencies().Any(d =>
                d.TargetMember.FullName.Contains("PermissionGrantRef", StringComparison.Ordinal)
                && d.TargetMember.Name.Contains("get_Value", StringComparison.Ordinal))))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>③ 程序集引用级：transport 绝不依赖 auth。</summary>
    [Fact]
    public void transport不引用auth()
    {
        var referenced = typeof(TransportService).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        Assert.DoesNotContain("Lumio.Server.MvpHost.Auth", referenced);
        Assert.DoesNotContain("Lumio.Server.MvpHost.Session", referenced);
        Assert.DoesNotContain("Lumio.Server.MvpHost.WorldSlot", referenced);
    }

    /// <summary>
    /// 禁用面在本工程生效的**间接证据**：本程序集不引用 <c>System.Net.Sockets</c>，
    /// 且源码里不出现 <c>Thread.Sleep</c> / <c>Task.Delay</c> / <c>DateTime</c>。
    /// 直接证据是构建期的 RS0030（手工探针见交回物）。
    /// </summary>
    [Fact]
    public void 本工程不触碰墙钟与睡眠()
    {
        var sourceDir = System.IO.Path.Combine(
            RepoPaths.MvpHostRoot, "src", "Lumio.Server.MvpHost.Transport");

        var offenders = new List<string>();

        foreach (var file in System.IO.Directory.EnumerateFiles(sourceDir, "*.cs", System.IO.SearchOption.AllDirectories))
        {
            var text = System.IO.File.ReadAllText(file);

            foreach (var banned in new[] { "Thread.Sleep", "Task.Delay", "DateTime.UtcNow", "DateTimeOffset.UtcNow" })
            {
                if (text.Contains(banned, StringComparison.Ordinal))
                {
                    offenders.Add($"{System.IO.Path.GetFileName(file)}: {banned}");
                }
            }
        }

        Assert.Empty(offenders);
    }
}

internal sealed class RecordingFaultPolicy : ITransportFaultPolicy
{
    internal List<TransportFaultContext> Contexts { get; } = new();

    public TransportFaultAction Decide(in TransportFaultContext ctx)
    {
        this.Contexts.Add(ctx);
        return TransportFaultAction.Pass;
    }
}
