using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.Account;
using Lumio.Server.MvpHost.Admission;
using Xunit;

namespace Lumio.Server.MvpHost.App.Tests;

public sealed class LiveElevenPathTests
{
    private static readonly string[] LiveElevenRoutes =
    {
        "/test-control/bindings",
        "/test-control/query",
        "/test-control/chat",
        "/test-control/tick",
        "/test-control/expire",
        "/test-control/snapshot",
        "/test-control/restore",
        "/test-control/room-admit",
    };

    private static readonly string[] SeventeenKeyFields =
    {
        "seq", "kind", "eventId", "timestamp", "category", "severity", "scope",
        "releasePoolId", "sessionId", "reasonCode", "admissionAttemptId", "effect",
        "sessionState", "authorityRevision", "slotEpoch", "connectionEpoch", "grantEpoch",
    };

    [Fact]
    public void FullGraphAdmitPathKeepsTheConnectionBinding()
    {
        var source = File.ReadAllText(SourcePath("FullGraphComposition.cs"));
        var start = source.LastIndexOf("TryAdmitLiveWebsocketClient", StringComparison.Ordinal);
        Assert.True(start >= 0, "Admit path must call TryAdmitLiveWebsocketClient");
        var snippet = source.Substring(start, Math.Min(700, source.Length - start));
        Assert.DoesNotContain("out _", snippet, StringComparison.Ordinal);
        Assert.Contains("out var binding", snippet, StringComparison.Ordinal);
        Assert.Contains("OnAdmitted", source, StringComparison.Ordinal);
        var adapter = File.ReadAllText(SourcePath("ChatRoomWorldAdapter.cs"));
        Assert.Contains("Assembly.LoadFrom", adapter, StringComparison.Ordinal);
        Assert.Contains("Lumio.Game.ServerGameplay", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("class ChatRoomWorld ", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("class ChatRoomWorld ", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TestControlServerDeclaresTheFrozenLiveElevenRoutes()
    {
        var source = File.ReadAllText(SourcePath("TestControlServer.cs"));
        foreach (var route in LiveElevenRoutes)
        {
            Assert.Contains(route, source, StringComparison.Ordinal);
        }

        Assert.Contains("MapGet", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HostTraceProjectsNentOnSeventeenKeyLines()
    {
        var source = File.ReadAllText(SourcePath("HostTrace.cs"));
        Assert.Contains("netEntityId", source, StringComparison.Ordinal);
        Assert.Contains("accountId", source, StringComparison.Ordinal);
        Assert.Contains("entityKind", source, StringComparison.Ordinal);
        Assert.Contains("ProjectBinding", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatInputCommandDecodesFrozenGgPayload()
    {
        Assert.True(App.ChatInputCommand.TryDecode(
            "chat.input",
            "020000006767",
            "5dbd584f1718b8bcd0dab4abeea83169f4a990defab81a8316ed845798d92dab",
            out var text,
            out var error));
        Assert.Equal("gg", text);
        Assert.True(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void GameplayAssemblyDiscoveryFindsSiblingLumioGame()
    {
        Assert.True(App.GameplayAssemblyDiscovery.TryFind(out var path), "Lumio.Game.ServerGameplay.dll must be discoverable");
        Assert.True(File.Exists(path));
        Assert.Equal("Lumio.Game.ServerGameplay.dll", Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TickIsScheduledThroughHostTimerNotAForLoop()
    {
        var path = SourcePath("LiveElevenHost.cs");
        Assert.True(File.Exists(path), "LiveElevenHost.cs must host ChatRoomWorld via LoadFrom and ITimerService tick");
        var liveEleven = File.ReadAllText(path);
        Assert.Contains("ITimerService", liveEleven, StringComparison.Ordinal);
        Assert.Contains("Schedule", liveEleven, StringComparison.Ordinal);
        Assert.DoesNotContain("for (var i = 0; i <", liveEleven, StringComparison.Ordinal);
        Assert.DoesNotContain("for (int i = 0; i <", liveEleven, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BindingsQueryChatTickExpireSnapshotAndSecondRoomRunOnTestControl()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lumio-live11-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var secret = Path.Combine(root, "secret.bin");
        var audit = Path.Combine(root, "host-audit.ndjson");
        File.WriteAllBytes(secret, Encoding.UTF8.GetBytes("live11-secret"));
        var keys = Ed25519Keys.Generate();
        var previousPublic = Environment.GetEnvironmentVariable(App.FullGraphComposition.AdmissionPublicKeyEnv);
        var previousKeyId = Environment.GetEnvironmentVariable(App.FullGraphComposition.AdmissionKeyIdEnv);
        Environment.SetEnvironmentVariable(
            App.FullGraphComposition.AdmissionPublicKeyEnv,
            Convert.ToHexString(keys.PublicKey));
        Environment.SetEnvironmentVariable(App.FullGraphComposition.AdmissionKeyIdEnv, "1");

        var options = new App.HostCommandLineOptions(
            "ws://127.0.0.1:0",
            true,
            App.HostCommandLineOptions.DefaultHostProfile,
            App.HostCommandLineOptions.DefaultProductId,
            App.HostCommandLineOptions.DefaultGameReleaseId,
            secret,
            AdmissionReconnectDefaults.TestReconnectWindowSeconds,
            true,
            "http://127.0.0.1:0",
            audit);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await using var composition = App.HostComposition.Create(options);
            await composition.StartAsync(timeout.Token);
            using var http = new HttpClient { BaseAddress = new Uri(composition.BoundTestControlUri) };

            var empty = await GetBindingsAsync(http, timeout.Token);
            Assert.Empty(empty);

            var botCred = Issue(keys.Seed, "Bot01", botToolContext: true);
            var playerCred = Issue(keys.Seed, "Browser01", botToolContext: false);
            var isoCred = Issue(keys.Seed, "IsoPlayerA", botToolContext: false);

            var botAdmit = await PostJsonAsync(
                http,
                "/test-control/room-admit",
                new Dictionary<string, object?>
                {
                    ["roomId"] = App.FullGraphComposition.ProductionRoomId,
                    ["connectionId"] = "conn-Bot01",
                    ["admissionCredential"] = botCred,
                },
                timeout.Token);
            Assert.True(botAdmit.GetProperty("accepted").GetBoolean(), botAdmit.GetRawText());

            var playerAdmit = await PostJsonAsync(
                http,
                "/test-control/room-admit",
                new Dictionary<string, object?>
                {
                    ["roomId"] = App.FullGraphComposition.ProductionRoomId,
                    ["connectionId"] = "conn-Browser01",
                    ["admissionCredential"] = playerCred,
                },
                timeout.Token);
            Assert.True(playerAdmit.GetProperty("accepted").GetBoolean(), playerAdmit.GetRawText());

            var isoAdmit = await PostJsonAsync(
                http,
                "/test-control/room-admit",
                new Dictionary<string, object?>
                {
                    ["roomId"] = "room-iso",
                    ["connectionId"] = "conn-iso-a",
                    ["admissionCredential"] = isoCred,
                },
                timeout.Token);
            Assert.True(isoAdmit.GetProperty("accepted").GetBoolean(), isoAdmit.GetRawText());

            var bindings = await GetBindingsAsync(http, timeout.Token);
            Assert.Equal(3, bindings.Count);
            Assert.All(bindings, row =>
            {
                Assert.StartsWith("nent_", row.GetProperty("netEntityId").GetString(), StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("accountId").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("sessionId").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("connectionId").GetString()));
                Assert.True(row.GetProperty("generation").GetUInt64() >= 1);
            });

            var bot = FindBinding(bindings, "conn-Bot01");
            var player = FindBinding(bindings, "conn-Browser01");
            var iso = FindBinding(bindings, "conn-iso-a");
            Assert.Equal("bot", bot.GetProperty("entityKind").GetString());
            Assert.Equal("player", player.GetProperty("entityKind").GetString());
            Assert.Equal("room-iso", iso.GetProperty("roomId").GetString());
            Assert.NotEqual(bot.GetProperty("netEntityId").GetString(), player.GetProperty("netEntityId").GetString());

            var botNent = bot.GetProperty("netEntityId").GetString()!;
            var playerNent = player.GetProperty("netEntityId").GetString()!;
            var isoNent = iso.GetProperty("netEntityId").GetString()!;

            var ok = await PostJsonAsync(
                http,
                "/test-control/query",
                new Dictionary<string, object?>
                {
                    ["requesterNetEntityId"] = playerNent,
                    ["targetNetEntityId"] = playerNent,
                    ["attributeId"] = "EntityIdentity.entityType",
                },
                timeout.Token);
            Assert.Equal("ok", ok.GetProperty("outcome").GetString());
            Assert.Equal("player", ok.GetProperty("value").GetString());

            var unauthorized = await PostJsonAsync(
                http,
                "/test-control/query",
                new Dictionary<string, object?>
                {
                    ["requesterNetEntityId"] = botNent,
                    ["targetNetEntityId"] = playerNent,
                    ["attributeId"] = "EntityIdentity.restrictedFlag",
                },
                timeout.Token);
            Assert.Equal("unauthorized", unauthorized.GetProperty("outcome").GetString());

            var invisible = await PostJsonAsync(
                http,
                "/test-control/query",
                new Dictionary<string, object?>
                {
                    ["requesterNetEntityId"] = botNent,
                    ["targetNetEntityId"] = playerNent,
                    ["attributeId"] = "ChatComponent.lastMessageText",
                },
                timeout.Token);
            Assert.Equal("invisible", invisible.GetProperty("outcome").GetString());

            var stale = await PostJsonAsync(
                http,
                "/test-control/query",
                new Dictionary<string, object?>
                {
                    ["requesterNetEntityId"] = playerNent,
                    ["targetNetEntityId"] = playerNent,
                    ["attributeId"] = "EntityIdentity.entityType",
                    ["connectionGeneration"] = 0,
                },
                timeout.Token);
            Assert.Equal("stale_generation", stale.GetProperty("outcome").GetString());

            var missing = await PostJsonAsync(
                http,
                "/test-control/query",
                new Dictionary<string, object?>
                {
                    ["requesterNetEntityId"] = playerNent,
                    ["targetNetEntityId"] = "nent_0000000000000000000000000000dead",
                    ["attributeId"] = "EntityIdentity.entityType",
                },
                timeout.Token);
            Assert.Equal("non_existent", missing.GetProperty("outcome").GetString());

            var crossRoom = await PostJsonAsync(
                http,
                "/test-control/query",
                new Dictionary<string, object?>
                {
                    ["requesterNetEntityId"] = playerNent,
                    ["targetNetEntityId"] = isoNent,
                    ["attributeId"] = "EntityIdentity.entityType",
                },
                timeout.Token);
            Assert.Equal("unauthorized", crossRoom.GetProperty("outcome").GetString());

            var chat = await PostJsonAsync(
                http,
                "/test-control/chat",
                new Dictionary<string, object?>
                {
                    ["connectionId"] = "conn-Bot01",
                    ["mappingId"] = "chat.input",
                    ["payload"] = "0b00000068656c6c6f2d426f743031",
                    ["payloadSha256"] = Sha256Hex(LumioBinUtf8("hello-Bot01")),
                },
                timeout.Token);
            Assert.True(chat.GetProperty("ok").GetBoolean(), chat.GetRawText());
            Assert.Equal("Admitted", chat.GetProperty("kind").GetString());

            var tick = await PostJsonAsync(http, "/test-control/tick", new Dictionary<string, object?>(), timeout.Token);
            Assert.True(tick.GetProperty("ok").GetBoolean(), tick.GetRawText());
            Assert.True(tick.GetProperty("appliedTick").GetUInt64() >= 1);

            var lastMessage = await PostJsonAsync(
                http,
                "/test-control/query",
                new Dictionary<string, object?>
                {
                    ["requesterNetEntityId"] = botNent,
                    ["targetNetEntityId"] = botNent,
                    ["attributeId"] = "ChatComponent.lastMessageText",
                },
                timeout.Token);
            Assert.Equal("ok", lastMessage.GetProperty("outcome").GetString());
            Assert.Equal("hello-Bot01", lastMessage.GetProperty("value").GetString());

            var snapshot = await PostJsonAsync(
                http,
                "/test-control/snapshot",
                new Dictionary<string, object?> { ["roomId"] = App.FullGraphComposition.ProductionRoomId },
                timeout.Token);
            Assert.Equal(0, snapshot.GetProperty("historyCount").GetInt32());
            var entities = snapshot.GetProperty("entities");
            Assert.True(entities.GetArrayLength() >= 2);
            foreach (var entity in entities.EnumerateArray())
            {
                Assert.Equal(0, entity.GetProperty("historyCount").GetInt32());
            }

            var historyReject = await PostJsonAsync(
                http,
                "/test-control/restore",
                new Dictionary<string, object?>
                {
                    ["roomId"] = App.FullGraphComposition.ProductionRoomId,
                    ["historyCount"] = 1,
                    ["entities"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["netEntityId"] = botNent,
                            ["lastMessageText"] = "nope",
                            ["lastMessageTick"] = 1,
                            ["historyCount"] = 1,
                        },
                    },
                },
                timeout.Token);
            Assert.False(historyReject.GetProperty("ok").GetBoolean());

            var expire = await PostJsonAsync(
                http,
                "/test-control/expire",
                new Dictionary<string, object?> { ["netEntityId"] = botNent },
                timeout.Token);
            Assert.True(expire.GetProperty("ok").GetBoolean(), expire.GetRawText());

            var tombstoned = await PostJsonAsync(
                http,
                "/test-control/query",
                new Dictionary<string, object?>
                {
                    ["requesterNetEntityId"] = playerNent,
                    ["targetNetEntityId"] = botNent,
                    ["attributeId"] = "EntityIdentity.entityType",
                },
                timeout.Token);
            Assert.Equal("tombstoned", tombstoned.GetProperty("outcome").GetString());

            var reissued = Issue(keys.Seed, "Bot01", botToolContext: true);
            var reincarnated = await PostJsonAsync(
                http,
                "/test-control/room-admit",
                new Dictionary<string, object?>
                {
                    ["roomId"] = App.FullGraphComposition.ProductionRoomId,
                    ["connectionId"] = "conn-Bot01-b",
                    ["admissionCredential"] = reissued,
                },
                timeout.Token);
            Assert.True(reincarnated.GetProperty("accepted").GetBoolean(), reincarnated.GetRawText());
            var after = await GetBindingsAsync(http, timeout.Token);
            var born = FindBinding(after, "conn-Bot01-b");
            Assert.StartsWith("nent_", born.GetProperty("netEntityId").GetString(), StringComparison.Ordinal);
            Assert.NotEqual(botNent, born.GetProperty("netEntityId").GetString());

            await composition.DisposeAsync();
            var auditText = File.ReadAllText(audit);
            Assert.Contains("nent_", auditText, StringComparison.Ordinal);
            Assert.Contains("\"entityKind\":\"bot\"", auditText, StringComparison.Ordinal);
            Assert.Contains("\"netEntityId\":\"nent_", auditText, StringComparison.Ordinal);
            var sawSeventeen = false;
            foreach (var line in auditText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var document = JsonDocument.Parse(line);
                var ev = document.RootElement;
                if (ev.ValueKind != JsonValueKind.Object
                    || ev.GetProperty("kind").GetString() is not ("audit" or "state"))
                {
                    continue;
                }

                if (!ev.TryGetProperty("netEntityId", out var nent)
                    || nent.GetString() is not { } id
                    || !id.StartsWith("nent_", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var key in SeventeenKeyFields)
                {
                    Assert.True(ev.TryGetProperty(key, out _), "missing 17-key field " + key);
                }

                sawSeventeen = true;
                break;
            }

            Assert.True(sawSeventeen, "host-audit must project nent_* on a 17-key audit/state line");
        }
        finally
        {
            Environment.SetEnvironmentVariable(App.FullGraphComposition.AdmissionPublicKeyEnv, previousPublic);
            Environment.SetEnvironmentVariable(App.FullGraphComposition.AdmissionKeyIdEnv, previousKeyId);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static JsonElement FindBinding(IReadOnlyList<JsonElement> bindings, string connectionId)
    {
        foreach (var row in bindings)
        {
            if (string.Equals(row.GetProperty("connectionId").GetString(), connectionId, StringComparison.Ordinal))
            {
                return row;
            }
        }

        throw new InvalidOperationException("binding not found: " + connectionId);
    }

    private static async Task<IReadOnlyList<JsonElement>> GetBindingsAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("/test-control/bindings", cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("bindings").ValueKind);
        var list = new List<JsonElement>();
        foreach (var row in document.RootElement.GetProperty("bindings").EnumerateArray())
        {
            list.Add(row.Clone());
        }

        return list;
    }

    private static async Task<JsonElement> PostJsonAsync(
        HttpClient http,
        string route,
        Dictionary<string, object?> body,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(route, content, cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return document.RootElement.Clone();
    }

    private static string Issue(byte[] seed, string loginName, bool botToolContext)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(loginName)))
            .ToLowerInvariant();
        var accountId = "acct_" + hex[..32];
        var now = new SystemAdmissionClock().UnixSeconds;
        return AdmissionCredential.Issue(seed, 1, accountId, loginName, botToolContext, now, now + 3600);
    }

    private static byte[] LumioBinUtf8(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        var payload = new byte[4 + utf8.Length];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), (uint)utf8.Length);
        Buffer.BlockCopy(utf8, 0, payload, 4, utf8.Length);
        return payload;
    }

    private static string Sha256Hex(byte[] payload)
        => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static string SourcePath(string fileName)
    {
        var assemblyDir = Path.GetDirectoryName(typeof(App.Program).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(
            assemblyDir,
            "..", "..", "..", "..", "..",
            "src",
            "Lumio.Server.MvpHost.App",
            fileName));
    }
}
