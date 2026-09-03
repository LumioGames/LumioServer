using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Lumio.Server.MvpHost.Architecture.Tests;

/// <summary>
/// 从 csproj 读出的构建图。**读工程文件而不是读已加载的程序集**：
/// 分层与禁边是**声明**层面的约束，一条 <c>ProjectReference</c> 即使当下没有任何代码用到，
/// 也已经把边建立了；等它被用到再报错就晚了。
/// </summary>
internal sealed record ProjectNode(
    string Name,
    string Path,
    int? Layer,
    bool IsProduction,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences);

internal static class BuildGraph
{
    internal static string MvpHostRoot { get; } = Locate();

    internal static IReadOnlyList<ProjectNode> All { get; } = Load();

    internal static IReadOnlyList<ProjectNode> Production { get; } =
        All.Where(p => p.IsProduction).ToList();

    internal static ProjectNode? ByName(string name)
        => All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// <c>build.proj</c> 的 traversal glob。<c>adapters/</c> 刻意不在其中——
    /// Runtime 类型全部关在不进构建图的 Adapter 工程里，
    /// 「Adapter 缺席仍全绿」因此是机器可判断言而不是口头承诺。
    /// </summary>
    internal static IReadOnlyList<string> TraversalGlobs { get; } = LoadTraversalGlobs();

    private static List<ProjectNode> Load()
    {
        var nodes = new List<ProjectNode>();

        foreach (var dir in new[] { "src", "tests", "testkit", "adapters" })
        {
            var root = Path.Combine(MvpHostRoot, dir);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
            {
                nodes.Add(Parse(csproj));
            }
        }

        // verify_admission lives in account-server; Game Server consumes that
        // assembly in-process. Including the library (not App/Tests) keeps
        // LayeringTest's extra-graph assertion intact instead of weakening it.
        var accountLibrary = Path.GetFullPath(Path.Combine(
            MvpHostRoot,
            "..",
            "account-server",
            "src",
            "Lumio.Server.Account",
            "Lumio.Server.Account.csproj"));
        if (File.Exists(accountLibrary))
        {
            nodes.Add(Parse(accountLibrary));
        }

        return nodes.OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
    }

    private static ProjectNode Parse(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);

        var layerText = doc.Descendants("MvpHostLayer").FirstOrDefault()?.Value;
        var productionText = doc.Descendants("MvpHostProductionProject").FirstOrDefault()?.Value;

        var projectReferences = doc.Descendants("ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => ProjectReferenceName(v!))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        var packageReferences = doc.Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        return new ProjectNode(
            Name: Path.GetFileNameWithoutExtension(csprojPath),
            Path: csprojPath,
            Layer: int.TryParse(layerText, out var layer) ? layer : null,
            IsProduction: !string.Equals(productionText, "false", StringComparison.OrdinalIgnoreCase),
            ProjectReferences: projectReferences,
            PackageReferences: packageReferences);
    }

    private static string ProjectReferenceName(string include)
    {
        var normalized = include.Replace('\\', '/');
        if (normalized.Contains("LumioEngineSdkProject", StringComparison.Ordinal)
            || normalized.Contains("Lumio.Engine.SDK", StringComparison.Ordinal))
        {
            return "Lumio.Engine.SDK";
        }

        return Path.GetFileNameWithoutExtension(normalized);
    }

    private static List<string> LoadTraversalGlobs()
        => XDocument.Load(Path.Combine(MvpHostRoot, "build.proj"))
            .Descendants("TraversalProject")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToList();

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "eng", "verify-all.sh")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"从 {AppContext.BaseDirectory} 向上找不到 mvp-host 根（哨兵 eng/verify-all.sh）。");
    }
}
