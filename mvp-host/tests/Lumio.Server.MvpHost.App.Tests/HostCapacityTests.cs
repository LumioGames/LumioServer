using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Lumio.Server.Account;
using Lumio.Server.MvpHost.Admission;
using Lumio.Server.MvpHost.Platform;
using Xunit;

namespace Lumio.Server.MvpHost.App.Tests;

public sealed class HostCapacityTests
{
    private const int OneHundredOneEntitySlice = 101;

    [Fact]
    public void FullGraphProductionConstantsAllowOneHundredOneLiveClients()
    {
        var type = typeof(App.FullGraphComposition);
        var maxConnections = ReadPrivateInt32(type, "MaxConnections");
        var maxSessions = ReadPrivateInt32(type, "MaxSessions");

        Assert.True(
            maxConnections >= OneHundredOneEntitySlice,
            $"MaxConnections={maxConnections} cannot admit 100 Bot + 1 Browser");
        Assert.True(
            maxSessions >= OneHundredOneEntitySlice,
            $"MaxSessions={maxSessions} cannot admit 100 Bot + 1 Browser");
    }

    [Fact]
    public void FullGraphCreateWiresRoomAdmissionOnTheAdmitPath()
    {
        var source = File.ReadAllText(FullGraphSourcePath());

        Assert.Contains("CreateRoomAdmissionRegistry", source, StringComparison.Ordinal);
        Assert.Contains(".Admit(", source, StringComparison.Ordinal);
        Assert.Contains("TryGetBindingByConnection", source, StringComparison.Ordinal);
        Assert.Contains("AccountAdmissionVerifier", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AdmitPathClassifiesBotAndPlayerViaVerifyAdmission()
    {
        var method = typeof(App.FullGraphComposition).GetMethod(
            "TryAdmitLiveWebsocketClient",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);

        var clock = new TestAdmissionClock(1_700_000_000);
        var monotonic = PlatformModule.CreateClock();
        using var timers = PlatformModule.CreateTimerService(monotonic);
        var keys = Ed25519Keys.Generate();
        var registry = App.HostComposition.CreateRoomAdmissionRegistry(
            1,
            keys.PublicKey,
            clock,
            monotonic,
            timers,
            reconnectWindowSeconds: 10);

        var botCredential = Issue(keys.Seed, clock.UnixSeconds, "Bot01", botToolContext: true);
        var playerCredential = Issue(keys.Seed, clock.UnixSeconds, "Browser", botToolContext: false);

        var botBinding = GetOutBinding(method!, registry, "room-main", "conn-Bot01", botCredential);
        Assert.Equal(BoundEntityKind.Bot, botBinding.EntityType);
        Assert.Equal("bot", botBinding.EntityType.ToContractValue());

        var playerBinding = GetOutBinding(method!, registry, "room-main", "conn-Browser", playerCredential);
        Assert.Equal(BoundEntityKind.Player, playerBinding.EntityType);
        Assert.Equal("player", playerBinding.EntityType.ToContractValue());
        Assert.Equal(2, registry.ListBindings("room-main").Count);
    }

    private static ConnectionBinding GetOutBinding(
        MethodInfo method,
        RoomAdmissionRegistry registry,
        string roomId,
        string connectionId,
        string credential)
    {
        var arguments = new object?[] { registry, roomId, connectionId, credential, null };
        var accepted = (bool)method.Invoke(obj: null, parameters: arguments)!;
        Assert.True(accepted);
        return Assert.IsType<ConnectionBinding>(arguments[4]);
    }

    private static string Issue(byte[] seed, ulong now, string loginName, bool botToolContext)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(loginName)))
            .ToLowerInvariant();
        var accountId = "acct_" + hex[..32];
        return AdmissionCredential.Issue(
            seed,
            1,
            accountId,
            loginName,
            botToolContext,
            now,
            now + 300);
    }

    private static int ReadPrivateInt32(Type type, string name)
    {
        var field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return Assert.IsType<int>(field!.GetValue(null));
    }

    private static string FullGraphSourcePath()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(App.Program).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(
            assemblyDir,
            "..", "..", "..", "..", "..",
            "src",
            "Lumio.Server.MvpHost.App",
            "FullGraphComposition.cs"));
    }

    private sealed class TestAdmissionClock(ulong unixSeconds) : IAdmissionClock
    {
        public ulong UnixSeconds { get; } = unixSeconds;
    }
}
