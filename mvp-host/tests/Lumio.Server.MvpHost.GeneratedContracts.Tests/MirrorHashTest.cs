using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Xunit;

namespace Lumio.Server.MvpHost.GeneratedContracts.Tests
{
    /// <summary>
    /// 镜像自架构源的文件必须逐字节等于 <c>eng/contract-mirror.sha256</c> 记录的内容，
    /// 且 <c>contract-mirror/</c> 下不得出现未登记的文件。
    ///
    /// 白名单只有 <c>MIRROR.md</c> 一项：它是本仓手写的，架构源没有对应文件，
    /// 进哈希清单会与「与架构源字节相同」互斥。任何第二项白名单都会在
    /// 「字节级镜像」与「无未登记文件」之间开一个口子。
    /// </summary>
    public sealed class MirrorHashTest
    {
        private static readonly string[] Whitelist = { "MIRROR.md" };

        [Fact]
        public void 清单登记的每个文件都与镜像现状逐字节一致()
        {
            var entries = ContractMirrorManifest.Read();

            Assert.NotEmpty(entries);

            var mismatched = new List<string>();
            foreach (var (relativePath, expectedHash) in entries)
            {
                var absolute = MvpHostTree.Path(relativePath.Split('/'));
                if (!File.Exists(absolute))
                {
                    mismatched.Add($"{relativePath}: 清单登记但文件不存在");
                    continue;
                }

                var actual = ContractMirrorManifest.HashOf(absolute);
                if (!string.Equals(actual, expectedHash, StringComparison.Ordinal))
                {
                    mismatched.Add($"{relativePath}: 清单 {expectedHash} != 实际 {actual}");
                }
            }

            Assert.Empty(mismatched);
        }

        [Fact]
        public void 镜像目录下除白名单外没有未登记的文件()
        {
            var mirrorRoot = MvpHostTree.Path("contract-mirror");
            Assert.True(Directory.Exists(mirrorRoot), $"镜像目录不存在：{mirrorRoot}");

            var registered = ContractMirrorManifest.Read()
                .Select(e => e.RelativePath)
                .ToHashSet(StringComparer.Ordinal);

            var unregistered = Directory
                .EnumerateFiles(mirrorRoot, "*", SearchOption.AllDirectories)
                .Select(ToRepositoryRelativePath)
                .Where(p => !registered.Contains(p))
                .Where(p => !Whitelist.Contains(p["contract-mirror/".Length..], StringComparer.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            Assert.Empty(unregistered);
        }

        /// <summary>
        /// 契约生成物走源码拷贝而非工程引用：镜像目录下出现任何工程文件，
        /// 都意味着有人试图把架构源的 net8.0 工程接进本构建根（父级
        /// <c>Directory.Build.targets</c> 会把它按 net10.0 判死，理由见 MIRROR.md）。
        /// </summary>
        [Fact]
        public void 镜像目录下不存在任何工程文件或源码()
        {
            var mirrorRoot = MvpHostTree.Path("contract-mirror");
            Assert.True(Directory.Exists(mirrorRoot), $"镜像目录不存在：{mirrorRoot}");

            var offenders = Directory
                .EnumerateFiles(mirrorRoot, "*", SearchOption.AllDirectories)
                .Where(p =>
                {
                    var name = Path.GetFileName(p);
                    return name.EndsWith(".csproj", StringComparison.Ordinal)
                        || name.EndsWith(".cs", StringComparison.Ordinal)
                        || name.StartsWith("Directory.Build.", StringComparison.Ordinal);
                })
                .Select(ToRepositoryRelativePath)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            Assert.Empty(offenders);
        }

        private static string ToRepositoryRelativePath(string absolute)
            => Path.GetRelativePath(MvpHostTree.Root, absolute).Replace('\\', '/');
    }

    /// <summary>
    /// <c>eng/contract-mirror.sha256</c> 的读取与哈希计算。测试与 shell / PowerShell
    /// 两侧脚本共用同一份格式约定：每行 <c>&lt;sha256&gt;  &lt;相对 mvp-host 的路径&gt;</c>，
    /// 与 <c>shasum -a 256</c> 的输出格式一致（两个空格分隔）。
    /// </summary>
    internal static class ContractMirrorManifest
    {
        internal static IReadOnlyList<(string RelativePath, string Hash)> Read()
        {
            var manifestPath = MvpHostTree.Path("eng", "contract-mirror.sha256");
            Assert.True(File.Exists(manifestPath), $"哈希清单不存在：{manifestPath}");

            var entries = new List<(string, string)>();
            foreach (var line in File.ReadAllLines(manifestPath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var separator = trimmed.IndexOf("  ", StringComparison.Ordinal);
                Assert.True(separator > 0, $"清单行格式非法（缺两空格分隔）：{line}");

                var hash = trimmed[..separator];
                var path = trimmed[(separator + 2)..].Trim();
                Assert.Equal(64, hash.Length);
                entries.Add((path, hash));
            }

            return entries;
        }

        internal static string HashOf(string absolutePath)
        {
            using var stream = File.OpenRead(absolutePath);
            var digest = SHA256.HashData(stream);
            return Convert.ToHexString(digest).ToLower(CultureInfo.InvariantCulture);
        }
    }
}
