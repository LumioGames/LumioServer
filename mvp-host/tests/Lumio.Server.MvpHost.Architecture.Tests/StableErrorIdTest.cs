using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Lumio.Server.MvpHost.Architecture.Tests;

/// <summary>
/// 全构建图版本的错误码纪律（从 Wire 的单工程版本提升而来）。
///
/// **MVP 不发明任何新错误码。** 扫描用源码而不是反射：错误码是写在方法体里的
/// 字符串字面量，签名上看不见。
/// </summary>
public sealed class StableErrorIdTest
{
    private static IReadOnlyCollection<string> Registered { get; } =
        Lumio.Gen.ContractTypes.Catalog.StableErrorIds.ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<(string File, string Value)> ErrorIdLiterals()
    {
        foreach (var project in BuildGraph.Production)
        {
            var dir = Path.GetDirectoryName(project.Path)!;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}Generated{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    // Generated/ 是架构源的逐字节拷贝，本仓无权改，也不适用本仓纪律。
                    continue;
                }

                var text = File.ReadAllText(file);

                // 两种出现形态：结构化结果类型的 StableErrorId 实参，与出站 reasonCode。
                foreach (Match m in Regex.Matches(
                             text,
                             @"(?:EnvelopeParseResult|AckResult|AllocateResult|HostLifecycleResult|AdmissionStep)\([^)]*?""([A-Za-z][A-Za-z0-9]*)""",
                             RegexOptions.Singleline,
                             TimeSpan.FromSeconds(10)))
                {
                    yield return (Path.GetFileName(file), m.Groups[1].Value);
                }

                foreach (Match m in Regex.Matches(
                             text,
                             @"(?:reasonCode|ReasonCode|registeredErrorCode|registeredReasonCode)\s*[:=]\s*""([A-Za-z][A-Za-z0-9]*)""",
                             RegexOptions.Singleline,
                             TimeSpan.FromSeconds(10)))
                {
                    yield return (Path.GetFileName(file), m.Groups[1].Value);
                }
            }
        }
    }

    /// <summary>
    /// 不断言个数（<c>StableErrorIds</c> 已由 43 增至 53，计数必然随 additive 增补腐烂），
    /// 只断言「用到的每一个都在册」。
    /// </summary>
    [Fact]
    public void 全构建图用到的错误码都已在生成物注册()
    {
        var unregistered = ErrorIdLiterals()
            .Where(x => !Registered.Contains(x.Value))
            .Select(x => $"{x.File}: {x.Value}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(unregistered);
    }

    /// <summary>
    /// <c>AuthBusy</c> 与 <c>AggregateBusy</c> 是**模块内部枚举成员**，不是 StableErrorId：
    /// 前者是 Auth 的 <c>AuthQueueAdmission</c> 成员、后者是 WorldSlot 的入队结果成员，
    /// <c>ids/index.json</c> 里都不在册（实测）。需要对外表达时一律映射已注册的 <c>QueueFull</c>。
    ///
    /// 这条挡的是「反正意思对就先写上」——写上去它就跨了 wire，而对端不认识它。
    /// </summary>
    [Theory]
    [InlineData("AuthBusy")]
    [InlineData("AggregateBusy")]
    public void 模块内部忙碌码不出现在错误码位置且未在册(string internalOnly)
    {
        Assert.DoesNotContain(internalOnly, Registered);

        var offenders = ErrorIdLiterals()
            .Where(x => string.Equals(x.Value, internalOnly, StringComparison.Ordinal))
            .Select(x => x.File)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void QueueFull确实在册可作为映射目标()
        => Assert.Contains("QueueFull", Registered);

    /// <summary>
    /// 扫描器本身必须真的抓到东西。没有这条，正则写错时上面几条会以「零命中」
    /// 的方式全部变绿——这正是「有一份看起来在守护的东西」那类失效。
    /// </summary>
    [Fact]
    public void 扫描器确实抓到了错误码字面量()
        => Assert.NotEmpty(ErrorIdLiterals());
}
