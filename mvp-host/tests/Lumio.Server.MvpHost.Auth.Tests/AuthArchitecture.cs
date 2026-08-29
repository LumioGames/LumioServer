using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ArchUnitNET.Domain;
using ArchUnitNET.Domain.Extensions;
using ArchUnitNET.Loader;

namespace Lumio.Server.MvpHost.Auth.Tests;

/// <summary>
/// 本工程的断言底座。
///
/// **为什么必须有 ArchUnitNET**：<c>System.Reflection</c> 只能看签名与元数据，
/// 看不到方法体内部、构造点或调用点。凡「谁调用了谁」的断言只能靠调用依赖，
/// 凡能用类型/成员签名表达的仍走签名级反射（设计 §4.3）。**不使用 IL 字节扫描。**
/// </summary>
internal static class AuthArchitecture
{
    internal static System.Reflection.Assembly AuthAssembly { get; } = typeof(MvpAuthorizationService).Assembly;

    internal static ArchUnitNET.Domain.Architecture Loaded { get; } =
        new ArchLoader().LoadAssemblies(AuthAssembly).Build();

    /// <summary>mvp-host 根。测试进程 cwd 由 runner 决定、不可依赖，因此向上找哨兵文件。</summary>
    internal static string MvpHostRoot { get; } = Locate();

    /// <summary>某个具体类型的全部方法调用依赖目标（<c>声明类型.成员名</c> 形态的全名）。</summary>
    internal static IEnumerable<string> MethodCallTargets(System.Type type)
        => TypeOf(type).Members
            .SelectMany(member => member.GetMethodCallDependencies())
            .Select(dependency => dependency.TargetMember.FullName);

    /// <summary>整个 <c>Auth</c> 程序集的方法调用依赖目标。</summary>
    internal static IEnumerable<string> AllMethodCallTargets()
        => Loaded.Types.SelectMany(t => t.Members)
            .SelectMany(member => member.GetMethodCallDependencies())
            .Select(dependency => dependency.TargetMember.FullName);

    /// <summary>产出 <c>(调用方成员全名, 被调方成员全名)</c> 对——用于「调用只来自某一个方法」这类断言。</summary>
    internal static IEnumerable<(string Caller, string Target)> MethodCallEdges()
        => Loaded.Types.SelectMany(t => t.Members)
            .SelectMany(member => member.GetMethodCallDependencies()
                .Select(dependency => (Caller: member.FullName, Target: dependency.TargetMember.FullName)));

    /// <summary><c>Auth</c> 程序集的全部类型依赖（类型级，含字段/参数/局部类型的引用）。</summary>
    internal static IEnumerable<string> AllTypeDependencies()
        => Loaded.Types.SelectMany(t => t.Dependencies).Select(d => d.Target.FullName);

    /// <summary>程序集内全部类型名与成员名（含非公开），供「不存在某个名字」这类签名级断言使用。</summary>
    internal static IEnumerable<string> AllNames()
    {
        foreach (var type in AuthAssembly.GetTypes())
        {
            yield return type.FullName ?? type.Name;

            foreach (var member in type.GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.DeclaredOnly))
            {
                yield return $"{type.FullName}.{member.Name}";
            }
        }
    }

    /// <summary>程序集内全部方法（含非公开），供签名级断言使用。</summary>
    internal static IEnumerable<MethodInfo> AllMethods()
        => AuthAssembly.GetTypes().SelectMany(t => t.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.DeclaredOnly));

    /// <summary>本工程生产源码的全部 <c>.cs</c> 文本（不含 obj/bin）。</summary>
    internal static IEnumerable<(string File, string Text)> ProductionSources()
    {
        var root = Path.Combine(MvpHostRoot, "src", "Lumio.Server.MvpHost.Auth");

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (Path.GetFileName(file), File.ReadAllText(file));
        }
    }

    private static IType TypeOf(System.Type type)
        => Loaded.Types.First(t => string.Equals(t.FullName, type.FullName, StringComparison.Ordinal));

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
