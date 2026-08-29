using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lumio.Server.MvpHost.Wire.Tests
{
    /// <summary>
    /// 镜像 fixture 与 schema 的装载入口。测试进程的 cwd 由 runner 决定、不可依赖，
    /// 因此从程序集所在目录逐级向上找哨兵文件（<c>eng/verify-all.sh</c>）。
    ///
    /// 生产代码走的是嵌入资源（见 Wire.csproj），只有测试按路径读——
    /// 测试要证明的恰恰是「嵌入的那份与磁盘上被哈希锁住的那份一致」。
    /// </summary>
    internal static class MirrorFixtures
    {
        internal static string MvpHostRoot { get; } = Locate();

        internal static string MirrorPath(params string[] segments)
        {
            var parts = new string[segments.Length + 2];
            parts[0] = MvpHostRoot;
            parts[1] = "contract-mirror";
            Array.Copy(segments, 0, parts, 2, segments.Length);
            return Path.Combine(parts);
        }

        internal static byte[] ReadBytes(params string[] segments) => File.ReadAllBytes(MirrorPath(segments));

        internal static string ReadText(params string[] segments) => File.ReadAllText(MirrorPath(segments));

        /// <summary>
        /// <c>fixtures/&lt;bucket&gt;/</c> 下文件名以 <paramref name="prefix"/> 起头的全部 fixture。
        ///
        /// 刻意**枚举目录而不是写死清单**：镜像清单会随上游 additive 增补变长
        /// （本轮 invalid 就由 5 条变成 10 条全集），写死条数的测试在上游加一条反例时
        /// 会安静地漏掉它——而漏掉的正好是新出现的那个风险。
        /// </summary>
        internal static IReadOnlyList<string> FixtureNames(string bucket, string prefix)
            => Directory.EnumerateFiles(MirrorPath("fixtures", bucket), prefix + "*.json")
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)
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
}
