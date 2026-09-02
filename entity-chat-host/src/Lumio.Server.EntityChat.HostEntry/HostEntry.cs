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
/// Native JSON op entry that consumes Runtime EntityBindingQuery / ChatCommandRuntime.
/// Single-threaded: the Rust owner thread must serialize every call.
/// </summary>
public static class HostEntry
{
    private const int EntrySuccess = 0;
    private const int EntryInvalidInput = 1;
    private const int EntryBufferTooSmall = 2;
    private const int EntryRuntimeFailure = 3;

    private static readonly object Gate = new();
    private static Assembly? Replication;
    private static Assembly? Ecs;
    private static Type? BindingType;
    private static Type? ChatType;
    private static Type? PersistType;
    private static object? Bindings;
    private static object? Chat;

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
        Replication = Assembly.LoadFrom(replicationPath);
        Ecs = Assembly.LoadFrom(ecsPath);
        BindingType = Replication.GetType("Lumio.GameRuntime.Replication.Binding.EntityBindingQuery");
        ChatType = Replication.GetType("Lumio.GameRuntime.Replication.Chat.ChatCommandRuntime");
        PersistType = Ecs.GetType("Lumio.GameRuntime.Ecs.EcsPersistSnapshotPipeline");
        if (BindingType is null || ChatType is null)
        {
            return (EntrySuccess, Fail("boot_failed"));
        }

        Bindings = BindingType.GetMethod("Create", Type.EmptyTypes)!.Invoke(null, Array.Empty<object>());
        Chat = ChatType.GetMethod("Create", new[] { BindingType, typeof(bool) })
            ?.Invoke(null, new object?[] { Bindings, false });
        Chat ??= ChatType.GetMethod("Create", Type.EmptyTypes)?.Invoke(null, Array.Empty<object>());
        if (Bindings is null || Chat is null)
        {
            return (EntrySuccess, Fail("boot_failed"));
        }

        return (EntrySuccess, Ok());
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
                // Best-effort: the requested Replication/Ecs LoadFrom is the gate.
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

        object result = BindingType!.GetMethod("ResolveByNetEntityId")!
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
        requestType.GetProperty("NetEntityId")!.SetValue(request, ReadString(root, "netEntityId"));
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
            || !TryReadString(root, "roomId", out string? room)
            || !TryReadString(root, "connection", out string? connection)
            || !TryReadString(root, "envelope", out string? envelope)
            || room is null || connection is null || envelope is null
            || !TryReadU64(root, "connectionGeneration", out ulong generation))
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        object result = ChatType!.GetMethod("AdmitInputCommand")!
            .Invoke(Chat, new object[] { room, connection, generation, envelope })!;
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
        if (Chat is null || !TryReadString(root, "roomId", out string? room) || room is null
            || !TryReadU64(root, "tickId", out ulong tickId))
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        object result = ChatType!.GetMethod("RunTick")!.Invoke(Chat, new object[] { tickId })!;
        Type tickType = result.GetType();
        ulong applied = Convert.ToUInt64(tickType.GetProperty("AppliedTick")!.GetValue(result)!, CultureInfo.InvariantCulture);
        ulong revision = Convert.ToUInt64(tickType.GetProperty("Revision")!.GetValue(result)!, CultureInfo.InvariantCulture);
        int eventCount = 0;
        if (tickType.GetProperty("Events")!.GetValue(result) is System.Collections.ICollection events)
        {
            eventCount = events.Count;
        }

        string? failed = null;
        if (tickType.GetProperty("Results")!.GetValue(result) is System.Collections.IEnumerable rows)
        {
            foreach (object row in rows)
            {
                bool succeeded = Convert.ToBoolean(row.GetType().GetProperty("Succeeded")!.GetValue(row)!, CultureInfo.InvariantCulture);
                if (!succeeded)
                {
                    failed = row.GetType().GetProperty("Code")!.GetValue(row) as string;
                    break;
                }
            }
        }

        return (EntrySuccess, Json(new Dictionary<string, object?>
        {
            ["ok"] = failed is null,
            ["appliedTick"] = applied,
            ["revision"] = revision,
            ["eventCount"] = eventCount,
            ["code"] = failed,
        }));
    }

    private static (int, byte[]) BuildFullSnapshot(JsonElement root)
    {
        if (Chat is null || !TryReadString(root, "roomId", out string? room) || room is null
            || !TryReadU64(root, "tickId", out ulong tickId)
            || !TryReadU64(root, "revision", out ulong revision))
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        string json = (string)ChatType!.GetMethod("BuildFullSnapshot")!.Invoke(Chat, new object[] { room, tickId, revision })!;
        return (EntrySuccess, Json(new Dictionary<string, object?> { ["ok"] = true, ["json"] = json }));
    }

    private static (int, byte[]) BuildDelta(JsonElement root)
    {
        if (Chat is null || !TryReadString(root, "roomId", out string? room) || room is null
            || !TryReadU64(root, "tickId", out ulong tickId)
            || !TryReadU64(root, "revision", out ulong revision))
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        object frames = ChatType!.GetMethod("BuildDelta")!.Invoke(Chat, new object[] { room, tickId, revision })!;
        var list = new List<string>();
        if (frames is System.Collections.IEnumerable rows)
        {
            foreach (object row in rows)
            {
                if (row is string text)
                {
                    list.Add(text);
                }
            }
        }

        return (EntrySuccess, Json(new Dictionary<string, object?> { ["ok"] = true, ["frames"] = list }));
    }

    private static (int, byte[]) Persist(JsonElement root)
    {
        if (Chat is null)
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        object? world = GetChatWorld();
        Type? persistType = PersistPipelineOf(world);
        if (world is null || persistType is null)
        {
            return (EntrySuccess, Fail("runtime_failure"));
        }

        MethodInfo? capture = FindStatic(
            persistType,
            "CapturePersist",
            static parameters => parameters.Length == 2 && parameters[1].ParameterType.IsByRef);
        if (capture is null)
        {
            return (EntrySuccess, Fail("runtime_failure"));
        }

        object[] args = { world, null! };
        object result = capture.Invoke(null, args)!;
        bool accepted = Convert.ToInt32(result.GetType().GetProperty("Status")!.GetValue(result)!, CultureInfo.InvariantCulture) == 0;
        byte[]? bytes = args[1] as byte[];
        if (!accepted || bytes is null || bytes.Length == 0)
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
        if (Chat is null || !TryReadString(root, "bytesHex", out string? hex) || hex is null)
        {
            return (EntrySuccess, Fail("invalid_request"));
        }

        object? world = GetChatWorld();
        Type? persistType = PersistPipelineOf(world);
        if (world is null || persistType is null)
        {
            return (EntrySuccess, Fail("runtime_failure"));
        }

        MethodInfo? restore = FindStatic(
            persistType,
            "RestorePersist",
            static parameters =>
                parameters.Length == 2
                && parameters[1].ParameterType.IsGenericType
                && parameters[1].ParameterType.GetGenericTypeDefinition() == typeof(ReadOnlyMemory<>));
        if (restore is null)
        {
            return (EntrySuccess, Fail("runtime_failure"));
        }

        byte[] bytes = Convert.FromHexString(hex);
        ReadOnlyMemory<byte> memory = bytes;
        object result = restore.Invoke(null, new object[] { world, memory })!;
        bool accepted = Convert.ToInt32(result.GetType().GetProperty("Status")!.GetValue(result)!, CultureInfo.InvariantCulture) == 0;
        return (EntrySuccess, accepted ? Ok() : Fail("runtime_failure"));
    }

    private static Type? PersistPipelineOf(object? world)
    {
        return world?.GetType().Assembly.GetType("Lumio.GameRuntime.Ecs.EcsPersistSnapshotPipeline") ?? PersistType;
    }

    private static MethodInfo? FindStatic(Type type, string name, Func<ParameterInfo[], bool> match)
    {
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name == name && match(method.GetParameters()))
            {
                return method;
            }
        }

        return null;
    }

    private static object? GetChatWorld()
    {
        if (Chat is null || ChatType is null)
        {
            return null;
        }

        FieldInfo? field = ChatType.GetField("_world", BindingFlags.Instance | BindingFlags.NonPublic);
        object? ingress = field?.GetValue(Chat);
        return ingress?.GetType().GetProperty("World")?.GetValue(ingress);
    }

    private static (int, byte[]) FromBindingResult(object result)
    {
        Type type = result.GetType();
        string outcome = type.GetProperty("Outcome")!.GetValue(result) as string ?? "request_error";
        string? code = type.GetProperty("Code")!.GetValue(result) as string;
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
