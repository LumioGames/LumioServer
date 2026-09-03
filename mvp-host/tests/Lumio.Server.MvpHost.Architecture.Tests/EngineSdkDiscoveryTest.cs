using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace Lumio.Server.MvpHost.Architecture.Tests;

/// <summary>
/// App 对 <c>Lumio.Engine.SDK</c> 的工程引用必须经
/// <c>LUMIO_ARCHITECTURE_ROOT</c> / 兄弟仓 <c>Exists()</c> 发现，
/// 不得写死 GitHub Actions 上不存在的相对路径；找不到必须 BLOCKED。
/// </summary>
public sealed class EngineSdkDiscoveryTest
{
    private const string SdkCsproj = "Lumio.Engine.SDK.csproj";
    private const string SdkRelative = "engine/managed/Lumio.Engine.SDK/Lumio.Engine.SDK.csproj";

    private static readonly string AppCsprojPath = Path.Combine(
        BuildGraph.MvpHostRoot,
        "src",
        "Lumio.Server.MvpHost.App",
        "Lumio.Server.MvpHost.App.csproj");

    private static readonly string DirectoryBuildPropsPath =
        Path.Combine(BuildGraph.MvpHostRoot, "Directory.Build.props");

    [Fact]
    public void App的EngineSdk引用走属性发现而不是写死的缺失路径()
    {
        var xml = XDocument.Load(AppCsprojPath);
        var engineIncludes = xml.Descendants("ProjectReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .Where(IsEngineSdkInclude)
            .ToList();

        Assert.Equal(["$(LumioEngineSdkProject)"], engineIncludes);

        var appText = File.ReadAllText(AppCsprojPath);
        Assert.DoesNotContain("../../../../LumioGameEngineArchitecture/", appText, StringComparison.Ordinal);
        Assert.DoesNotContain("../../../../LumioGameEngine/", appText, StringComparison.Ordinal);
        AssertNoMachinePath(appText);
    }

    [Fact]
    public void 发现顺序是环境变量再新仓名再旧仓名且缺失为BLOCKED()
    {
        var props = File.ReadAllText(DirectoryBuildPropsPath);
        var app = File.ReadAllText(AppCsprojPath);
        AssertNoMachinePath(props);

        var envAt = props.IndexOf("LUMIO_ARCHITECTURE_ROOT", StringComparison.Ordinal);
        var siblingAt = props.IndexOf(", 'LumioGameEngine', 'engine'", StringComparison.Ordinal);
        var legacyAt = props.IndexOf(", 'LumioGameEngineArchitecture', 'engine'", StringComparison.Ordinal);

        Assert.True(envAt >= 0, "Directory.Build.props 必须先读 LUMIO_ARCHITECTURE_ROOT");
        Assert.True(siblingAt > envAt, "兄弟仓 LumioGameEngine 必须排在环境变量之后");
        Assert.True(legacyAt > siblingAt, "旧名 LumioGameEngineArchitecture 必须排在新仓名之后");

        Assert.Contains("BLOCKED", app, StringComparison.Ordinal);
        Assert.Contains("RequireLumioEngineSdk", app, StringComparison.Ordinal);
    }

    [Fact]
    public void App对EngineSdk是唯一允许的构建图外引用()
    {
        var extras = new List<string>();
        foreach (var project in BuildGraph.All)
        {
            foreach (var reference in project.ProjectReferences)
            {
                if (BuildGraph.ByName(reference) is not null)
                {
                    continue;
                }

                extras.Add($"{project.Name} → {reference}");
            }
        }

        Assert.Equal(["Lumio.Server.MvpHost.App → Lumio.Engine.SDK"], extras);
    }

    [Fact]
    public void 无环境变量且无兄弟仓时解析为空并报BLOCKED()
    {
        var fakeRoot = Path.Combine(Path.GetTempPath(), "lumio-no-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeRoot);
        try
        {
            var property = EvaluateEngineSdkProperty(architectureRoot: string.Empty, repoRoot: fakeRoot);
            Assert.True(string.IsNullOrWhiteSpace(property), $"expected empty discovery, got '{property}'");

            var result = RunMsbuild(
                ["msbuild", AppCsprojPath, "-nologo", "-t:RequireLumioEngineSdk", $"-p:LumioRepoRoot={fakeRoot}", "-p:LUMIO_ARCHITECTURE_ROOT="],
                architectureRoot: null);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("BLOCKED", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fakeRoot, recursive: true);
        }
    }

    [Fact]
    public void 设置架构源环境变量时解析到真实EngineSdk工程()
    {
        var architectureRoot = LocateArchitectureRoot();
        Assert.True(
            architectureRoot is not null,
            "需要 LUMIO_ARCHITECTURE_ROOT 或兄弟仓 LumioGameEngine / LumioGameEngineArchitecture 才能解析 Engine SDK");

        var fakeRoot = Path.Combine(Path.GetTempPath(), "lumio-hide-sibling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeRoot);
        try
        {
            var property = EvaluateEngineSdkProperty(architectureRoot, fakeRoot);
            Assert.False(string.IsNullOrWhiteSpace(property));
            Assert.True(
                string.Equals(Path.GetFileName(property), SdkCsproj, StringComparison.Ordinal),
                $"resolved '{property}'");
            Assert.True(File.Exists(property), $"resolved path does not exist: {property}");
            Assert.StartsWith(
                Path.GetFullPath(architectureRoot),
                Path.GetFullPath(property),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(fakeRoot, recursive: true);
        }
    }

    private static bool IsEngineSdkInclude(string include)
        => include.Contains("LumioEngineSdkProject", StringComparison.Ordinal)
           || include.Contains("Lumio.Engine.SDK", StringComparison.Ordinal);

    private static void AssertNoMachinePath(string text)
    {
        Assert.DoesNotContain(@"C:\Work", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\Users", text, StringComparison.OrdinalIgnoreCase);
    }

    private static string? LocateArchitectureRoot()
    {
        var env = Environment.GetEnvironmentVariable("LUMIO_ARCHITECTURE_ROOT");
        if (IsArchitectureRoot(env))
        {
            return Path.GetFullPath(env!);
        }

        var repoRoot = Path.GetFullPath(Path.Combine(BuildGraph.MvpHostRoot, ".."));
        foreach (var name in new[] { "LumioGameEngine", "LumioGameEngineArchitecture" })
        {
            var candidate = Path.GetFullPath(Path.Combine(repoRoot, "..", name));
            if (IsArchitectureRoot(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsArchitectureRoot(string? root)
        => !string.IsNullOrWhiteSpace(root)
           && File.Exists(Path.Combine(root, SdkRelative.Replace('/', Path.DirectorySeparatorChar)));

    private static string EvaluateEngineSdkProperty(string architectureRoot, string repoRoot)
    {
        var result = RunMsbuild(
            [
                "msbuild",
                AppCsprojPath,
                "-nologo",
                "-getProperty:LumioEngineSdkProject",
                $"-p:LumioRepoRoot={repoRoot}",
                $"-p:LUMIO_ARCHITECTURE_ROOT={architectureRoot}",
            ],
            architectureRoot);
        Assert.Equal(0, result.ExitCode);
        return result.Output.Trim();
    }

    private static (int ExitCode, string Output) RunMsbuild(IReadOnlyList<string> arguments, string? architectureRoot)
    {
        var start = new ProcessStartInfo
        {
            FileName = DotnetPath(),
            WorkingDirectory = BuildGraph.MvpHostRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        if (architectureRoot is null)
        {
            start.Environment.Remove("LUMIO_ARCHITECTURE_ROOT");
        }
        else
        {
            start.Environment["LUMIO_ARCHITECTURE_ROOT"] = architectureRoot;
        }

        using var process = new Process { StartInfo = start };
        Assert.True(process.Start());
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "msbuild timed out");
        return (process.ExitCode, stdout + Environment.NewLine + stderr);
    }

    private static string DotnetPath()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "dotnet";
    }
}
