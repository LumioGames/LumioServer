using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Lumio.Server.MvpHost.Architecture.Tests;

/// <summary>
/// 分层与禁边。判据全部从 csproj 的 <c>&lt;MvpHostLayer&gt;</c> 与 <c>ProjectReference</c> 读出，
/// **不使用任何共享 allowlist 文件**——新增工程因此不需要改一份人人都要动的清单，
/// 也就不会在卡之间制造文件冲突。
/// </summary>
public sealed class LayeringTest
{
    // CA1861：断言用的期望数组提为 static readonly——它们在多个 [Fact] 里被反复求值。
    private static readonly string[] SimulationReferenceEdges = { "Lumio.Server.MvpHost.HostContracts" };

    private static readonly string[] HostContractsEdges =
    {
        "Lumio.Server.MvpHost.Platform", "Lumio.Server.MvpHost.Wire",
    };

    private static readonly string[] ObservabilityEdges = { "Lumio.Server.MvpHost.HostContracts" };

    private static readonly string[] TestKitEdges =
    {
        "Lumio.Server.MvpHost.HostContracts",
        "Lumio.Server.MvpHost.Observability",
        "Lumio.Server.MvpHost.Platform",
        "Lumio.Server.MvpHost.Wire",
    };

    [Fact]
    public void 每个工程都声明了层号()
    {
        var missing = BuildGraph.All.Where(p => p.Layer is null).Select(p => p.Name).ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// 被依赖方的 layer **严格小于**依赖方——严格小于即无环，不需要另做环检测。
    /// </summary>
    [Fact]
    public void 依赖边的层号严格递减因而无环()
    {
        var violations = new List<string>();

        foreach (var project in BuildGraph.All)
        {
            foreach (var reference in project.ProjectReferences)
            {
                var target = BuildGraph.ByName(reference);
                if (target is null)
                {
                    if (IsAllowedExtraGraph(project.Name, reference))
                    {
                        continue;
                    }

                    violations.Add($"{project.Name} 引用了构建图外的 {reference}");
                    continue;
                }

                if (project.Layer is null || target.Layer is null)
                {
                    continue;
                }

                if (target.Layer >= project.Layer)
                {
                    violations.Add(
                        $"{project.Name}(layer {project.Layer}) → {target.Name}(layer {target.Layer})：被依赖方层号必须严格更小");
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// 模块红线：这些边**一条都不能有**。
    /// <c>Transport ↮ Auth</c> 是 Rust 侧原文的红线（transport 只搬运不透明的
    /// <c>PermissionGrantRef</c>，绝不依赖 auth）；<c>WorldSlot</c> 不认识任何
    /// 连接层概念；<c>Auth</c> 不认识 session 编排。
    /// </summary>
    [Theory]
    [InlineData("Lumio.Server.MvpHost.Transport", "Lumio.Server.MvpHost.Auth")]
    [InlineData("Lumio.Server.MvpHost.Auth", "Lumio.Server.MvpHost.Transport")]
    [InlineData("Lumio.Server.MvpHost.Auth", "Lumio.Server.MvpHost.Session")]
    [InlineData("Lumio.Server.MvpHost.WorldSlot", "Lumio.Server.MvpHost.Transport")]
    [InlineData("Lumio.Server.MvpHost.WorldSlot", "Lumio.Server.MvpHost.Auth")]
    [InlineData("Lumio.Server.MvpHost.WorldSlot", "Lumio.Server.MvpHost.Session")]
    public void 禁边不存在(string from, string to)
    {
        var project = BuildGraph.ByName(from);

        // 工程尚未落地时本条为空真——断言先落地，被断言的工程由后续卡提供。
        if (project is null)
        {
            return;
        }

        Assert.DoesNotContain(to, project.ProjectReferences);
    }

    [Fact]
    public void 参考仿真实现的出度只有HostContracts()
    {
        var project = BuildGraph.ByName("Lumio.Server.MvpHost.Simulation.Reference");
        if (project is null)
        {
            return;
        }

        Assert.Equal(SimulationReferenceEdges, project.ProjectReferences);
    }

    [Fact]
    public void 契约层的引用恰为Wire与Platform()
    {
        var project = BuildGraph.ByName("Lumio.Server.MvpHost.HostContracts");
        Assert.NotNull(project);

        Assert.Equal(
            HostContractsEdges,
            project.ProjectReferences.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void 可观测层的引用恰为契约层()
    {
        var project = BuildGraph.ByName("Lumio.Server.MvpHost.Observability");
        Assert.NotNull(project);

        Assert.Equal(ObservabilityEdges, project.ProjectReferences);
    }

    /// <summary>
    /// TestKit 是测试**库**不是测试工程：必须声明 <c>MvpHostProductionProject=false</c>，
    /// 且**不得**设 <c>IsTestProject</c>——设了会让 runner 把它当成一个零测试的测试程序集。
    /// </summary>
    [Fact]
    public void 测试库声明为非生产工程且四个引用齐全()
    {
        var project = BuildGraph.ByName("Lumio.Server.MvpHost.TestKit");
        Assert.NotNull(project);
        Assert.False(project.IsProduction);

        Assert.Equal(TestKitEdges, project.ProjectReferences.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// 生产工程不得引用任何测试包。构建期的 <c>Directory.Build.targets</c> 已经硬失败一次，
    /// 这里再断言一次是因为两者的触发时机不同：那条只在该工程真被构建时才响，
    /// 而这条扫的是**全部** csproj，包括暂时不在构建图里的。
    /// </summary>
    [Fact]
    public void 生产工程不引用任何测试包()
    {
        var offenders = new List<string>();

        foreach (var project in BuildGraph.Production)
        {
            foreach (var package in project.PackageReferences)
            {
                if (package.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                    || package.StartsWith("nunit", StringComparison.OrdinalIgnoreCase)
                    || package.StartsWith("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
                    || package.StartsWith("TngTech.ArchUnitNET", StringComparison.OrdinalIgnoreCase)
                    || package.StartsWith("FsCheck", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{project.Name} → {package}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// App 必须引用架构源 <c>Lumio.Engine.SDK</c>；该工程不在本仓构建图内，
    /// 是唯一特许的构建图外边。路径由 <c>LUMIO_ARCHITECTURE_ROOT</c> / 兄弟仓发现，见
    /// <see cref="EngineSdkDiscoveryTest"/>。
    /// </summary>
    private static bool IsAllowedExtraGraph(string from, string to)
        => string.Equals(from, "Lumio.Server.MvpHost.App", StringComparison.Ordinal)
           && string.Equals(to, "Lumio.Engine.SDK", StringComparison.Ordinal);

    /// <summary>所有 <c>PackageReference</c> 一律不带 <c>Version</c>——版本只在中央文件声明。</summary>
    [Fact]
    public void 包引用一律不带版本属性()
    {
        var offenders = BuildGraph.All
            .Select(p => (p.Name, Xml: System.Xml.Linq.XDocument.Load(p.Path)))
            .SelectMany(x => x.Xml.Descendants("PackageReference")
                .Where(e => e.Attribute("Version") is not null)
                .Select(e => $"{x.Name} → {e.Attribute("Include")?.Value}"))
            .ToList();

        Assert.Empty(offenders);
    }
}
