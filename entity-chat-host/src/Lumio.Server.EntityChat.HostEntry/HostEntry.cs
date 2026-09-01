using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Lumio.Server.EntityChat.HostEntry;

/// <summary>
/// Native JSON op entry that hosts <c>Lumio.Game.ServerGameplay.ChatRoomWorld</c>
/// through Assembly.LoadFrom. Single-threaded calling model: the Rust owner
/// thread must serialize every call.
/// </summary>
public static class HostEntry
{
    private const int EntrySuccess = 0;
    private const int EntryInvalidInput = 1;
    private const int EntryBufferTooSmall = 2;
    private const int EntryRuntimeFailure = 3;

    private static readonly object Gate = new();
    private static Assembly? GameplayAssembly;
    private static Type? WorldType;
    private static Type? ChatInputType;
    private static readonly Dictionary<string, object> Worlds = new(StringComparer.Ordinal);

    /// <summary>Native entry point used by CoreCLR hostfxr.</summary>
    [UnmanagedCallersOnly(EntryPoint = "lumio_entity_chat_entry")]
    public static unsafe int LumioEntityChatEntry(
        byte* input,
        int inputLength,
        byte* output,
        int outputCapacity,
        int* bytesWritten)
    {
        if (bytesWritten is null)
        {
            return EntryInvalidInput;
        }

        bytesWritten[0] = 0;
        if (inputLength < 0 || outputCapacity < 0 || (inputLength > 0 && input is null) || (outputCapacity > 0 && output is null))
        {
            return EntryInvalidInput;
        }

        int returnCode;
        byte[] response;
        try
        {
            (returnCode, response) = Execute(input, inputLength);
        }
        catch (Exception)
        {
            returnCode = EntryRuntimeFailure;
            response = Encoding.UTF8.GetBytes("{\"ok\":false,\"code\":\"runtime_failure\"}");
        }

        if (response.Length > outputCapacity)
        {
            bytesWritten[0] = response.Length;
            return EntryBufferTooSmall;
        }

        response.AsSpan().CopyTo(new Span<byte>(output, response.Length));
        bytesWritten[0] = response.Length;
        return returnCode;
    }

    private static unsafe (int ReturnCode, byte[] Response) Execute(byte* input, int inputLength)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(new ReadOnlySpan<byte>(input, inputLength).ToArray());
            return Dispatch(document.RootElement);
        }
        catch (JsonException)
        {
            return (EntryInvalidInput, Fail("bad_envelope"));
        }
    }

    private static (int ReturnCode, byte[] Response) Dispatch(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("op", out JsonElement op)
            || op.ValueKind != JsonValueKind.String)
        {
            return (EntryInvalidInput, Fail("bad_envelope"));
        }

        string? name = op.GetString();
        lock (Gate)
        {
            return name switch
            {
                "boot" => Boot(root),
                "create_room" => CreateRoom(root),
                "create_entity" => CreateEntity(root),
                "destroy_entity" => DestroyEntity(root),
                "admit_chat" => AdmitChat(root),
                "tick" => Tick(root),
                "persist" => Persist(root),
                "restore" => Restore(root),
                "get_component" => GetComponent(root),
                "current_tick" => CurrentTick(root),
                "shutdown" => (EntrySuccess, Ok()),
                _ => (EntryInvalidInput, Fail("bad_envelope")),
            };
        }
    }

    private static (int, byte[]) Boot(JsonElement root)
    {
        if (!TryReadString(root, "gameplayAssembly", out string? path) || string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return (EntrySuccess, Fail("boot_failed"));
        }

        GameplayAssembly = Assembly.LoadFrom(path);
        WorldType = GameplayAssembly.GetType("Lumio.Game.ServerGameplay.ChatRoomWorld");
        ChatInputType = GameplayAssembly.GetType("Lumio.Game.ServerGameplay.ChatInput");
        if (WorldType is null || ChatInputType is null)
        {
            return (EntrySuccess, Fail("boot_failed"));
        }

        Worlds.Clear();
        return (EntrySuccess, Ok());
    }

    private static (int, byte[]) CreateRoom(JsonElement root)
    {
        if (!TryWorld(root, create: true, out object? world))
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        _ = world;
        return (EntrySuccess, Ok());
    }

    private static (int, byte[]) CreateEntity(JsonElement root)
    {
        if (!TryWorld(root, create: true, out object? world) || !TryReadU64(root, "netEntityId", out ulong id))
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        bool ok = (bool)WorldType!.GetMethod("TryCreateEntity")!.Invoke(world, new object[] { id })!;
        return (EntrySuccess, ok ? Ok() : Fail("invalid_request"));
    }

    private static (int, byte[]) DestroyEntity(JsonElement root)
    {
        if (!TryWorld(root, create: false, out object? world) || !TryReadU64(root, "netEntityId", out ulong id))
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        bool ok = (bool)WorldType!.GetMethod("DestroyEntity")!.Invoke(world, new object[] { id })!;
        return (EntrySuccess, ok ? Ok() : Fail("invalid_request"));
    }

    private static (int, byte[]) AdmitChat(JsonElement root)
    {
        if (!TryWorld(root, create: false, out object? world)
            || !TryReadU64(root, "senderNetEntityId", out ulong sender)
            || !TryReadString(root, "text", out string? text)
            || text is null)
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        object input = Activator.CreateInstance(ChatInputType!, text)!;
        object result = WorldType!.GetMethod("AdmitChatInput")!.Invoke(world, new object[] { sender, input })!;
        object kind = result.GetType().GetProperty("Kind")!.GetValue(result)!;
        if (Convert.ToInt32(kind, CultureInfo.InvariantCulture) == 0)
        {
            return (EntrySuccess, Ok());
        }

        string? code = result.GetType().GetProperty("ErrorCode")!.GetValue(result) as string;
        return (EntrySuccess, Fail(code ?? "invalid_request"));
    }

    private static (int, byte[]) Tick(JsonElement root)
    {
        if (!TryWorld(root, create: false, out object? world))
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        object tick = WorldType!.GetMethod("RunTick")!.Invoke(world, Array.Empty<object>())!;
        ulong applied = Convert.ToUInt64(tick.GetType().GetProperty("AppliedTick")!.GetValue(tick)!, CultureInfo.InvariantCulture);
        var events = new List<Dictionary<string, object?>>();
        if (tick.GetType().GetProperty("Events")!.GetValue(tick) is Array rows)
        {
            foreach (object row in rows)
            {
                Type type = row.GetType();
                events.Add(new Dictionary<string, object?>
                {
                    ["messageId"] = Convert.ToUInt64(type.GetProperty("MessageId")!.GetValue(row)!, CultureInfo.InvariantCulture),
                    ["roomSequence"] = Convert.ToUInt64(type.GetProperty("RoomSequence")!.GetValue(row)!, CultureInfo.InvariantCulture),
                    ["senderNetEntityId"] = Convert.ToUInt64(type.GetProperty("SenderNetEntityId")!.GetValue(row)!, CultureInfo.InvariantCulture),
                    ["text"] = type.GetProperty("Text")!.GetValue(row) as string,
                    ["appliedTick"] = Convert.ToUInt64(type.GetProperty("AppliedTick")!.GetValue(row)!, CultureInfo.InvariantCulture),
                });
            }
        }

        return (EntrySuccess, Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["appliedTick"] = applied,
            ["events"] = events,
        }));
    }

    private static (int, byte[]) Persist(JsonElement root)
    {
        if (!TryWorld(root, create: false, out object? world))
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        object states = WorldType!.GetMethod("CapturePersistState")!.Invoke(world, Array.Empty<object>())!;
        var entities = new List<Dictionary<string, object?>>();
        if (states is Array rows)
        {
            foreach (object row in rows)
            {
                Type type = row.GetType();
                entities.Add(new Dictionary<string, object?>
                {
                    ["netEntityId"] = Convert.ToUInt64(type.GetProperty("NetEntityId")!.GetValue(row)!, CultureInfo.InvariantCulture),
                    ["lastMessageText"] = type.GetProperty("LastMessageText")!.GetValue(row) as string,
                    ["lastMessageTick"] = Convert.ToUInt64(type.GetProperty("LastMessageTick")!.GetValue(row)!, CultureInfo.InvariantCulture),
                });
            }
        }

        return (EntrySuccess, Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["entities"] = entities,
        }));
    }

    private static (int, byte[]) Restore(JsonElement root)
    {
        if (!TryWorld(root, create: true, out object? world)
            || !TryReadU64(root, "netEntityId", out ulong id)
            || !TryReadString(root, "text", out string? text)
            || text is null
            || !TryReadU64(root, "lastMessageTick", out ulong tick))
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        bool ok = (bool)WorldType!.GetMethod("TryRestoreLastMessage")!.Invoke(world, new object[] { id, text, tick })!;
        return (EntrySuccess, ok ? Ok() : Fail("invalid_request"));
    }

    private static (int, byte[]) GetComponent(JsonElement root)
    {
        if (!TryWorld(root, create: false, out object? world) || !TryReadU64(root, "netEntityId", out ulong id))
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        object?[] args = { id, null };
        bool ok = (bool)WorldType!.GetMethod("TryGetComponent")!.Invoke(world, args)!;
        if (!ok || args[1] is null)
        {
            return (EntrySuccess, Fail("non_existent"));
        }

        object component = args[1]!;
        return (EntrySuccess, Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["lastMessageText"] = component.GetType().GetProperty("LastMessageText")!.GetValue(component) as string,
            ["lastMessageTick"] = Convert.ToUInt64(component.GetType().GetProperty("LastMessageTick")!.GetValue(component)!, CultureInfo.InvariantCulture),
        }));
    }

    private static (int, byte[]) CurrentTick(JsonElement root)
    {
        if (!TryWorld(root, create: false, out object? world))
        {
            return (EntrySuccess, Json(new Dictionary<string, object?> { ["ok"] = true, ["tick"] = 0UL }));
        }

        ulong tick = Convert.ToUInt64(WorldType!.GetProperty("CurrentTick")!.GetValue(world)!, CultureInfo.InvariantCulture);
        return (EntrySuccess, Json(new Dictionary<string, object?> { ["ok"] = true, ["tick"] = tick }));
    }

    private static bool TryWorld(JsonElement root, bool create, out object? world)
    {
        world = null;
        if (WorldType is null || !TryReadString(root, "roomId", out string? roomId) || string.IsNullOrEmpty(roomId))
        {
            return false;
        }

        if (Worlds.TryGetValue(roomId, out world))
        {
            return true;
        }

        if (!create)
        {
            return false;
        }

        world = Activator.CreateInstance(WorldType);
        if (world is null)
        {
            return false;
        }

        Worlds[roomId] = world;
        return true;
    }

    private static bool TryReadString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = el.GetString();
        return true;
    }

    private static bool TryReadU64(JsonElement root, string name, out ulong value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return el.TryGetUInt64(out value);
    }

    private static byte[] Ok() => Encoding.UTF8.GetBytes("{\"ok\":true}");

    private static byte[] Fail(string code)
    {
        return Json(new Dictionary<string, object?> { ["ok"] = false, ["code"] = code });
    }

    private static byte[] Json(Dictionary<string, object?> payload)
    {
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
    }
}
