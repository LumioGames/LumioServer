using System;
using System.IO;

namespace Lumio.Server.MvpHost.GeneratedContracts.Tests
{
    /// <summary>
    /// 定位 <c>mvp-host/</c> 根。测试进程的 cwd 由 runner 决定、不可依赖，
    /// 因此从程序集所在目录逐级向上找哨兵文件（<c>eng/verify-all.sh</c>）。
    /// </summary>
    internal static class MvpHostTree
    {
        internal static string Root { get; } = Locate();

        internal static string Path(params string[] segments)
        {
            var parts = new string[segments.Length + 1];
            parts[0] = Root;
            Array.Copy(segments, 0, parts, 1, segments.Length);
            return System.IO.Path.Combine(parts);
        }

        private static string Locate()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(System.IO.Path.Combine(dir.FullName, "eng", "verify-all.sh")))
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
