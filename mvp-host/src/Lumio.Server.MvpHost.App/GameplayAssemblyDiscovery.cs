using System;
using System.Collections.Generic;
using System.IO;

namespace Lumio.Server.MvpHost.App;

/// <summary>
/// Locates sibling <c>Lumio.Game.ServerGameplay.dll</c> the same way rust
/// <c>discover.rs</c> does. Missing files are named, never invented.
/// </summary>
internal static class GameplayAssemblyDiscovery
{
    private const string FileName = "Lumio.Game.ServerGameplay.dll";
    private const string RelativeGameplay =
        "modules/server-gameplay/src/Lumio.Game.ServerGameplay/bin/Debug/net10.0/Lumio.Game.ServerGameplay.dll";

    internal static bool TryFind(out string path)
    {
        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    private static IEnumerable<string> Candidates()
    {
        var env = Environment.GetEnvironmentVariable("LUMIO_GAMEPLAY_ASSEMBLY");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return env;
        }

        foreach (var root in GameRoots())
        {
            yield return Path.Combine(root, RelativeGameplay.Replace('/', Path.DirectorySeparatorChar));
            yield return Path.Combine(
                root,
                "modules",
                "server-gameplay",
                "src",
                "Lumio.Game.ServerGameplay",
                "bin",
                "Release",
                "net10.0",
                FileName);
        }
    }

    private static IEnumerable<string> GameRoots()
    {
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "LumioGame"));
        yield return @"C:\Work\LumioGames\LumioGame";
        yield return @"C:\Work\LumioGames\wt-game\r-00354-live11";
        yield return @"C:\Work\LumioGames\wt-game\r-00354";
        yield return @"C:\Work\LumioGames\wt-game\r-00354-review";

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Cargo.toml"))
                && Directory.Exists(Path.Combine(dir.FullName, "mvp-host")))
            {
                var serverRoot = dir.FullName;
                var work = Directory.GetParent(serverRoot)?.Parent;
                if (work is not null)
                {
                    yield return Path.Combine(work.FullName, "LumioGame");
                    yield return Path.Combine(work.FullName, "wt-game", "r-00354-live11");
                    yield return Path.Combine(work.FullName, "wt-game", "r-00354");
                    yield return Path.Combine(work.FullName, "wt-game", "r-00354-review");
                }

                break;
            }

            dir = dir.Parent;
        }
    }
}
