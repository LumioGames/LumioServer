using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Lumio.Server.EntityChat.HostEntry;

/// <summary>
/// Native JSON op entry. Holds one Runtime WorldManager; network threads only Enqueue.
/// Single-threaded: the Rust owner thread must serialize every call.
/// </summary>
public static class HostEntry
{
    private const int EntrySuccess = 0;
    private const int EntryInvalidInput = 1;
    private const int EntryBufferTooSmall = 2;
    private const int EntryRuntimeFailure = 3;
    private const ulong DefaultInstanceId = 0x1000000000000001UL;

    private static readonly object Gate = new();
    private static Assembly? Username;
    private static Assembly? Replication;
    private static Assembly? Ecs;
    private static Type? BindingType;
    private static Type? ChatType;
    private static Type? EnvelopeType;
    private static object? Manager;
    private static object? Bindings;
    private static object? Chat;
    private static ulong InstanceId = DefaultInstanceId;
    private static IReadOnlyList<string> LastDeltaFrames = Array.Empty<string>();

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
                "admit" => Admit(root),
                "disconnect" => Disconnect(root),
                "rebind" => Rebind(root),
                "expire" => Expire(root),
                "self_lookup" => SelfLookup(root),
                "resolve" => Resolve(root),
                "query" => Query(root),
                "list_bindings" => ListBindings(root),
                "attach_member" => AttachMember(root),
                "admit_input" => AdmitInput(root),
                "tick" => Tick(root),
                "build_full_snapshot" => BuildFullSnapshot(root),
                "build_delta" => BuildDelta(root),
                "persist" => Persist(root),
                "restore" => Restore(root),
                "shutdown" => (EntrySuccess, Ok()),
                _ => (EntryInvalidInput, Fail("bad_envelope")),
            };
        }
    }

    private static (int, byte[]) Boot(JsonElement root)
    {
        if (!TryReadString(root, "usernameServerAssembly", out string? usernamePath)
            || string.IsNullOrEmpty(usernamePath)
            || !File.Exists(usernamePath))
        {
            if (!TryReadString(root, "replicationAssembly", out string? replicationPath)
                || !TryReadString(root, "ecsAssembly", out string? ecsPath)
                || string.IsNullOrEmpty(replicationPath)
                || string.IsNullOrEmpty(ecsPath)
                || !File.Exists(replicationPath)
                || !File.Exists(ecsPath))
            {
                return (EntrySuccess, Fail("boot_failed"));
            }

            LoadSiblingAssemblies(Path.GetDirectoryName(replicationPath));
            LoadSiblingAssemblies(Path.GetDirectoryName(ecsPath));
        }
        else
        {
            LoadSiblingAssemblies(Path.GetDirectoryName(usernamePath));
            Username = Assembly.LoadFrom(usernamePath);
        }

        if (TryReadString(root, "replicationAssembly", out string? replication) && File.Exists(replication))
        {
            LoadSiblingAssemblies(Path.GetDirectoryName(replication));
            Replication = Assembly.LoadFrom(replication);
        }

        if (TryReadString(root, "ecsAssembly", out string? ecs) && File.Exists(ecs))
        {
            LoadSiblingAssemblies(Path.GetDirectoryName(ecs));
            Ecs = Assembly.LoadFrom(ecs);
        }

        if (TryReadU64(root, "instanceId", out ulong instanceId))
        {
            InstanceId = instanceId;
        }

        if (Username is null)
        {
            return (EntrySuccess, Fail("boot_failed"));
        }

        Type? bootstrap = Username.GetType("Lumio.GameRuntime.Samples.Username.Host.ServerBootstrap");
        MethodInfo? boot = bootstrap?.GetMethod("Boot", new[] { typeof(ulong) });
        if (boot is null)
        {
            return (EntrySuccess, Fail("boot_failed"));
        }

        Manager = boot.Invoke(null, new object[] { InstanceId });
        if (Manager is null)
        {
            return (EntrySuccess, Fail("boot_failed"));
        }

        Replication ??= FindLoaded("Lumio.GameRuntime.Replication");
        Ecs ??= FindLoaded("Lumio.GameRuntime.Ecs");
        BindingType = Replication?.GetType("Lumio.GameRuntime.Replication.Binding.EntityBindingQuery");
        ChatType = Replication?.GetType("Lumio.GameRuntime.Replication.Chat.ChatCommandRuntime");
        EnvelopeType = Replication?.GetType("Lumio.GameRuntime.Replication.Chat.ChatEnvelope");
        Type? managerType = Manager.GetType();
        if (BindingType is null || ChatType is null || managerType is null)
        {
            return (EntrySuccess, Fail("boot_failed"));
        }

        Bindings = BindingType.GetMethod("Create", new[] { managerType })?.Invoke(null, new[] { Manager });
        Chat = ChatType.GetMethod("Create", new[] { BindingType, typeof(bool) })
            ?.Invoke(null, new object?[] { Bindings, false });
        if (Bindings is null || Chat is null)
        {
            return (EntrySuccess, Fail("boot_failed"));
        }

        return (EntrySuccess, Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["instanceId"] = InstanceId.ToString("x16", CultureInfo.InvariantCulture),
        }));
    }

    private static Assembly? FindLoaded(string name)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal))
            {
                return assembly;
            }
        }

        return null;
    }

    private static void LoadSiblingAssemblies(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (string dll in Directory.GetFiles(directory, "Lumio.GameRuntime.*.dll"))
        {
            try
            {
                Assembly.LoadFrom(dll);
            }
            catch (Exception)
            {
                // Best-effort: Boot() is the gate.
            }
        }
    }

    private static (int, byte[]) Admit(JsonElement root)
    {
        if (Bindings is null
            || !TryReadString(root, "connection", out string? connection)
            || !TryReadString(root, "accountId", out string? account)
            || !TryReadString(root, "roomId", out string? room)
            || !TryReadString(root, "entityType", out string? entityType)
            || connection is null || account is null || room is null || entityType is null)
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        object result = BindingType!.GetMethod("Admit", new[] { typeof(string), typeof(string), typeof(string), typeof(string) })!
            .Invoke(Bindings, new object[] { connection, account, room, entityType })!;
        return FromBindingResult(result);
    }

    private static (int, byte[]) Disconnect(JsonElement root)
    {
        if (Bindings is null || !TryReadString(root, "connection", out string? connection) || connection is null)
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        object result = BindingType!.GetMethod("Disconnect", new[] { typeof(string) })!
            .Invoke(Bindings, new object[] { connection })!;
        return FromBindingResult(result);
    }

    private static (int, byte[]) Rebind(JsonElement root)
    {
        if (Bindings is null
            || !TryReadString(root, "connection", out string? connection)
            || !TryReadString(root, "accountId", out string? account)
            || !TryReadString(root, "roomId", out string? room)
            || !TryReadString(root, "mode", out string? mode)
            || connection is null || account is null || room is null || mode is null)
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        Type enumType = Replication!.GetType("Lumio.GameRuntime.Replication.Binding.RebindMode")!;
        object modeValue = Enum.Parse(enumType, mode.Equals("takeover", StringComparison.OrdinalIgnoreCase) ? "Takeover" : "Reconnect");
        object result = BindingType!.GetMethod("Rebind", new[] { typeof(string), typeof(string), typeof(string), enumType })!
            .Invoke(Bindings, new object[] { connection, account, room, modeValue })!;
        return FromBindingResult(result);
    }

    private static (int, byte[]) Expire(JsonElement root)
    {
        if (Bindings is null || !TryReadString(root, "netEntityId", out string? id) || id is null)
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        id = NormalizeNetEntityId(id);
        object result = BindingType!.GetMethod("Expire", new[] { typeof(string) })!
            .Invoke(Bindings, new object[] { id })!;
        return FromBindingResult(result);
    }

    private static (int, byte[]) SelfLookup(JsonElement root)
    {
        if (Bindings is null || !TryReadString(root, "connection", out string? connection) || connection is null)
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        object result = BindingType!.GetMethod("SelfLookup")!
            .Invoke(Bindings, new object[] { connection, "client-replica" })!;
        return FromBindingResult(result);
    }

    private static (int, byte[]) Resolve(JsonElement root)
    {
        if (Bindings is null
            || !TryReadString(root, "roomId", out string? room)
            || !TryReadString(root, "netEntityId", out string? id)
            || room is null || id is null)
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        id = NormalizeNetEntityId(id);
        object result = BindingType!.GetMethod(
                "ResolveByNetEntityId",
                new[] { typeof(string), typeof(string), typeof(ulong?), typeof(string) })!
            .Invoke(Bindings, new object?[] { room, id, null, "server-authoritative" })!;
        return FromBindingResult(result);
    }

    private static (int, byte[]) Query(JsonElement root)
    {
        if (Bindings is null)
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        Type requestType = Replication!.GetType("Lumio.GameRuntime.Replication.Binding.AttributeQueryRequest")!;
        object request = Activator.CreateInstance(requestType)!;
        requestType.GetProperty("CallerScope")!.SetValue(request, ReadString(root, "callerScope"));
        requestType.GetProperty("RoomId")!.SetValue(request, ReadString(root, "roomId"));
        string? netEntityId = ReadString(root, "netEntityId");
        requestType.GetProperty("NetEntityId")!.SetValue(
            request,
            string.IsNullOrEmpty(netEntityId) ? netEntityId : NormalizeNetEntityId(netEntityId));
        requestType.GetProperty("AttributeId")!.SetValue(request, ReadString(root, "attributeId"));
        if (root.TryGetProperty("connectionGeneration", out JsonElement gen) && gen.ValueKind == JsonValueKind.Number
            && gen.TryGetUInt64(out ulong generation))
        {
            requestType.GetProperty("ConnectionGeneration")!.SetValue(request, generation);
        }

        object result = BindingType!.GetMethod("QueryAttribute")!
            .Invoke(Bindings, new object?[] { request, null })!;
        return FromBindingResult(result);
    }

    private static (int, byte[]) ListBindings(JsonElement root)
    {
        if (Bindings is null || !TryReadString(root, "roomId", out string? room) || room is null)
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        object result = BindingType!.GetMethod("ListBindings", new[] { typeof(string) })!
            .Invoke(Bindings, new object[] { room })!;
        return FromBindingResult(result);
    }

    private static (int, byte[]) AttachMember(JsonElement root)
    {
        if (Chat is null
            || !TryReadString(root, "roomId", out string? room)
            || !TryReadString(root, "connection", out string? connection)
            || room is null || connection is null)
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        object result = ChatType!.GetMethod("AttachMember")!.Invoke(Chat, new object[] { room, connection })!;
        bool ok = Convert.ToBoolean(result.GetType().GetProperty("Succeeded")!.GetValue(result)!, CultureInfo.InvariantCulture);
        return (EntrySuccess, ok ? Ok() : Fail("runtime_failure"));
    }

    private static (int, byte[]) AdmitInput(JsonElement root)
    {
        if (Chat is null
            || EnvelopeType is null
            || !TryReadString(root, "roomId", out string? room)
            || !TryReadString(root, "connection", out string? connection)
            || !TryReadString(root, "envelope", out string? envelope)
            || room is null || connection is null || envelope is null
            || !TryReadU64(root, "connectionGeneration", out ulong generation))
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        object?[] parseArgs = { envelope, string.Empty, null! };
        MethodInfo? parse = EnvelopeType.GetMethod("TryParseInputCommand", BindingFlags.Public | BindingFlags.Static);
        bool parsed = parse is not null && Convert.ToBoolean(parse.Invoke(null, parseArgs)!, CultureInfo.InvariantCulture);
        if (!parsed)
        {
            return (EntrySuccess, Fail("bad_envelope"));
        }

        string text = parseArgs[1] as string ?? string.Empty;
        Type chatInput = Replication!.GetType("Lumio.GameRuntime.Replication.Chat.ChatInput")!;
        object input = Activator.CreateInstance(chatInput, text)!;
        object result = ChatType!.GetMethod("AdmitInput")!
            .Invoke(Chat, new object[] { room, connection, generation, input })!;
        bool ok = Convert.ToBoolean(result.GetType().GetProperty("Succeeded")!.GetValue(result)!, CultureInfo.InvariantCulture);
        if (ok)
        {
            return (EntrySuccess, Ok());
        }

        string? code = result.GetType().GetProperty("Code")!.GetValue(result) as string;
        return (EntrySuccess, Fail(code ?? "invalid_request"));
    }

    private static (int, byte[]) Tick(JsonElement root)
    {
        if (Chat is null)
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        _ = root;
        object result = ChatType!.GetMethod("RunTick")!.Invoke(Chat, new object[] { 0UL })!;
        Type tickType = result.GetType();
        ulong applied = Convert.ToUInt64(tickType.GetProperty("AppliedTick")!.GetValue(result)!, CultureInfo.InvariantCulture);
        ulong revision = Convert.ToUInt64(tickType.GetProperty("Revision")!.GetValue(result)!, CultureInfo.InvariantCulture);
        object? eventsObj = tickType.GetProperty("Events")!.GetValue(result);
        int eventCount = eventsObj is System.Collections.ICollection events ? events.Count : 0;
        LastDeltaFrames = EncodeDeltaFrames(applied, revision, eventsObj);
        return (EntrySuccess, Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["appliedTick"] = applied,
            ["revision"] = revision,
            ["eventCount"] = eventCount,
            ["code"] = null,
        }));
    }

    private static IReadOnlyList<string> EncodeDeltaFrames(ulong tick, ulong revision, object? eventsObj)
    {
        if (EnvelopeType is null)
        {
            return Array.Empty<string>();
        }

        MethodInfo? frames = EnvelopeType.GetMethod("DeltaFrames", BindingFlags.Public | BindingFlags.Static);
        if (frames is null)
        {
            return Array.Empty<string>();
        }

        object? list = frames.Invoke(null, new object?[] { tick, revision, eventsObj });
        if (list is not System.Collections.IEnumerable rows)
        {
            return Array.Empty<string>();
        }

        var encoded = new List<string>();
        foreach (object row in rows)
        {
            if (row is string text)
            {
                encoded.Add(text);
            }
        }

        return encoded;
    }

    private static (int, byte[]) BuildFullSnapshot(JsonElement root)
    {
        if (Manager is null)
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        _ = root;
        object world = Manager.GetType().GetProperty("World")!.GetValue(Manager)!;
        ulong tick = Convert.ToUInt64(world.GetType().GetProperty("Tick")!.GetValue(world)!, CultureInfo.InvariantCulture);
        ulong revision = Convert.ToUInt64(world.GetType().GetProperty("Revision")!.GetValue(world)!, CultureInfo.InvariantCulture);
        string json = EncodeFullSnapshot(world, tick, revision);
        if (string.IsNullOrEmpty(json))
        {
            return (EntrySuccess, Fail("runtime_failure"));
        }

        return (EntrySuccess, Json(new Dictionary<string, object?> { ["ok"] = true, ["json"] = json }));
    }

    private static string EncodeFullSnapshot(object world, ulong tick, ulong revision)
    {
        object? issued = world.GetType().GetProperty("IssuedIds")?.GetValue(world);
        var identities = new List<(ulong Counter, string EntityType)>();
        if (issued is System.Collections.IEnumerable rows)
        {
            MethodInfo? isLive = world.GetType().GetMethod("IsLive");
            object? registry = world.GetType().GetProperty("Registry")?.GetValue(world);
            MethodInfo? typeOf = world.GetType().GetMethod("TypeOf");
            foreach (object id in rows)
            {
                if (isLive?.Invoke(world, new[] { id }) is not true)
                {
                    continue;
                }

                object? handle = typeOf?.Invoke(world, new[] { id });
                object? clr = handle?.GetType().GetProperty("ClrType")?.GetValue(handle);
                string entityType = registry?
                    .GetType()
                    .GetMethod("WireName")?
                    .Invoke(registry, new[] { clr }) as string ?? string.Empty;
                if (entityType is not ("player" or "bot"))
                {
                    continue;
                }

                ulong counter = Convert.ToUInt64(id.GetType().GetProperty("Counter")!.GetValue(id)!, CultureInfo.InvariantCulture);
                identities.Add((counter, entityType));
            }
        }

        identities.Sort(static (left, right) => left.Counter.CompareTo(right.Counter));
        byte[] payload = EncodeIdentityPayload(identities);
        string hex = Convert.ToHexString(payload).ToLowerInvariant();
        string digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        string blocks = identities.Count == 0
            ? "[]"
            : "[{\"mappingId\":\"entity.identity\",\"payload\":\"" + hex + "\",\"payloadSha256\":\"" + digest + "\"}]";
        return "{\"messageType\":\"FullSnapshot\",\"tickId\":" + tick.ToString(CultureInfo.InvariantCulture)
               + ",\"revision\":" + revision.ToString(CultureInfo.InvariantCulture)
               + ",\"stateBlocks\":" + blocks + "}";
    }

    private static byte[] EncodeIdentityPayload(List<(ulong Counter, string EntityType)> identities)
    {
        var typeUtf8 = new byte[identities.Count][];
        int size = 4;
        for (int i = 0; i < identities.Count; i++)
        {
            typeUtf8[i] = Encoding.UTF8.GetBytes(identities[i].EntityType);
            size += 8 + 4 + typeUtf8[i].Length + 4;
        }

        byte[] bytes = new byte[size];
        int offset = 0;
        WriteU32(bytes, ref offset, (uint)identities.Count);
        for (int i = 0; i < identities.Count; i++)
        {
            WriteU64(bytes, ref offset, identities[i].Counter);
            WriteUtf8(bytes, ref offset, typeUtf8[i]);
            WriteUtf8(bytes, ref offset, Array.Empty<byte>());
        }

        return bytes;
    }

    private static void WriteU32(byte[] dest, ref int offset, uint value)
    {
        dest[offset] = (byte)value;
        dest[offset + 1] = (byte)(value >> 8);
        dest[offset + 2] = (byte)(value >> 16);
        dest[offset + 3] = (byte)(value >> 24);
        offset += 4;
    }

    private static void WriteU64(byte[] dest, ref int offset, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            dest[offset + i] = (byte)(value >> (8 * i));
        }

        offset += 8;
    }

    private static void WriteUtf8(byte[] dest, ref int offset, byte[] utf8)
    {
        WriteU32(dest, ref offset, (uint)utf8.Length);
        utf8.CopyTo(dest, offset);
        offset += utf8.Length;
    }

    private static (int, byte[]) BuildDelta(JsonElement root)
    {
        _ = root;
        return (EntrySuccess, Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["frames"] = LastDeltaFrames,
        }));
    }

    private static (int, byte[]) Persist(JsonElement root)
    {
        if (Manager is null)
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        _ = root;
        MethodInfo? capture = Manager.GetType().GetMethod("CaptureSnapshot", Type.EmptyTypes);
        if (capture is null)
        {
            return (EntrySuccess, Fail("runtime_failure"));
        }

        if (capture.Invoke(Manager, Array.Empty<object>()) is not byte[] bytes || bytes.Length == 0)
        {
            return (EntrySuccess, Fail("runtime_failure"));
        }

        return (EntrySuccess, Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["bytesHex"] = Convert.ToHexString(bytes).ToLowerInvariant(),
        }));
    }

    private static (int, byte[]) Restore(JsonElement root)
    {
        if (Username is null || !TryReadString(root, "bytesHex", out string? hex) || hex is null)
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        Type? bootstrap = Username.GetType("Lumio.GameRuntime.Samples.Username.Host.ServerBootstrap");
        MethodInfo? restore = bootstrap?.GetMethod("Restore", new[] { typeof(byte[]) });
        if (restore is null)
        {
            return (EntrySuccess, Fail("runtime_failure"));
        }

        byte[] bytes = Convert.FromHexString(hex);
        object? manager = restore.Invoke(null, new object[] { bytes });
        if (manager is null)
        {
            return (EntrySuccess, Fail("runtime_failure"));
        }

        manager.GetType().GetMethod("Start")!.Invoke(manager, new object[] { Thread.CurrentThread });
        Manager = manager;
        Type managerType = manager.GetType();
        Bindings = BindingType!.GetMethod("Create", new[] { managerType })!.Invoke(null, new[] { manager });
        Chat = ChatType!.GetMethod("Create", new[] { BindingType, typeof(bool) })!
            .Invoke(null, new object?[] { Bindings, false });
        LastDeltaFrames = Array.Empty<string>();
        return (EntrySuccess, Ok());
    }

    private static (int, byte[]) FromBindingResult(object result)
    {
        Type type = result.GetType();
        string outcome = type.GetProperty("Outcome")!.GetValue(result) as string ?? "request_error";
        string? code = type.GetProperty("Code")!.GetValue(result) as string;
        if (string.IsNullOrEmpty(code) && outcome is not ("ok" or "request_error"))
        {
            code = outcome;
        }

        object? binding = type.GetProperty("Binding")!.GetValue(result);
        object? bindings = type.GetProperty("Bindings")!.GetValue(result);
        object? value = type.GetProperty("Value")!.GetValue(result);
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = outcome == "ok",
            ["outcome"] = outcome,
            ["code"] = code,
        };
        if (binding is not null)
        {
            payload["binding"] = BindingDict(binding);
        }
        else if (type.GetProperty("NetEntityId")!.GetValue(result) is string netEntityId
                 && !string.IsNullOrEmpty(netEntityId))
        {
            payload["binding"] = new Dictionary<string, object?>
            {
                ["accountId"] = string.Empty,
                ["roomId"] = type.GetProperty("RoomId")!.GetValue(result) as string ?? string.Empty,
                ["netEntityId"] = netEntityId,
                ["entityType"] = type.GetProperty("EntityType")!.GetValue(result) as string ?? "player",
                ["connectionGeneration"] = 0UL,
            };
        }

        if (bindings is Array array)
        {
            var rows = new List<Dictionary<string, object?>>();
            foreach (object row in array)
            {
                rows.Add(BindingDict(row));
            }

            payload["bindings"] = rows;
        }

        if (value is not null)
        {
            payload["value"] = Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        return (EntrySuccess, Json(payload));
    }

    private static string NormalizeNetEntityId(string id)
    {
        string lower = id.Trim().ToLowerInvariant();
        if (lower.Length == 32)
        {
            bool hex = true;
            foreach (char c in lower)
            {
                if (!Uri.IsHexDigit(c))
                {
                    hex = false;
                    break;
                }
            }

            if (hex)
            {
                return lower;
            }
        }

        if (ulong.TryParse(lower, NumberStyles.None, CultureInfo.InvariantCulture, out ulong dec))
        {
            return dec.ToString("x32", CultureInfo.InvariantCulture);
        }

        if (ulong.TryParse(lower, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hexValue))
        {
            return hexValue.ToString("x32", CultureInfo.InvariantCulture);
        }

        return lower;
    }

    private static Dictionary<string, object?> BindingDict(object binding)
    {
        Type type = binding.GetType();
        return new Dictionary<string, object?>
        {
            ["accountId"] = type.GetProperty("AccountId")!.GetValue(binding) as string,
            ["roomId"] = type.GetProperty("RoomId")!.GetValue(binding) as string,
            ["netEntityId"] = type.GetProperty("NetEntityId")!.GetValue(binding) as string,
            ["entityType"] = type.GetProperty("EntityType")!.GetValue(binding) as string,
            ["connectionGeneration"] = Convert.ToUInt64(type.GetProperty("ConnectionGeneration")!.GetValue(binding)!, CultureInfo.InvariantCulture),
        };
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

    private static string? ReadString(JsonElement root, string name)
    {
        return TryReadString(root, name, out string? value) ? value : null;
    }

    private static bool TryReadU64(JsonElement root, string name, out ulong value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out JsonElement el))
        {
            return false;
        }

        if (el.ValueKind == JsonValueKind.Number)
        {
            return el.TryGetUInt64(out value);
        }

        if (el.ValueKind == JsonValueKind.String)
        {
            string? text = el.GetString();
            return !string.IsNullOrEmpty(text)
                && ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }

        return false;
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
