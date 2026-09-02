using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Lumio.Client.Bot;

/// <summary>
/// Loaded into <c>Lumio.Client.Bot.Host</c> via <c>DOTNET_STARTUP_HOOKS</c>.
/// Drains production <see cref="ClientTimerManager"/> (native tickFrame) and
/// submits pre-encoded chat.input envelopes on the Room WebSocket.
/// No-op unless <c>LUMIO_BOT_FLEET_SPEC</c> is set, so a bare Bot.Host still runs.
/// </summary>
internal static class StartupHook
{
    public static void Initialize()
    {
        string? specPath = Environment.GetEnvironmentVariable("LUMIO_BOT_FLEET_SPEC");
        if (string.IsNullOrWhiteSpace(specPath))
        {
            return;
        }

        int code = 2;
        try
        {
            code = Run(specPath);
        }
        catch (Exception ex)
        {
            TryWriteBlocked(specPath, ex.ToString());
            code = 2;
        }

        Environment.Exit(code);
    }

    private static int Run(string specPath)
    {
        FleetSpec spec = JsonSerializer.Deserialize<FleetSpec>(
            File.ReadAllText(specPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("fleet spec missing");

        if (string.IsNullOrWhiteSpace(spec.EngineNative) || !File.Exists(spec.EngineNative))
        {
            WriteTrace(spec, false, "", Array.Empty<ulong>(), 0, "BLOCKED: LUMIO_ENGINE_NATIVE missing for Client Timer Manager");
            return 2;
        }

        var sockets = new List<ClientWebSocket>(spec.Bots.Count);
        try
        {
            Uri room = new Uri(spec.RoomUri.TrimEnd('/') + "/");
            foreach (BotSpec bot in spec.Bots)
            {
                var ws = new ClientWebSocket();
                ws.ConnectAsync(room, CancellationToken.None).GetAwaiter().GetResult();
                SendText(ws, "{\"connectionId\":\"" + bot.ConnectionId + "\"}");
                DrainInBackground(ws);
                sockets.Add(ws);
            }

            using var abi = new NativeTickFrameAbi(spec.EngineNative);
            using var timer = new ClientTimerManager(abi);
            if (!timer.ScheduleBotChatCadence())
            {
                WriteTrace(spec, false, "", Array.Empty<ulong>(), 0, "BLOCKED: ClientTimerManager.ScheduleBotChatCadence failed");
                return 2;
            }

            ulong advanceTo = spec.AdvanceToTick == 0 ? 15UL : spec.AdvanceToTick;
            IReadOnlyList<ulong> dues = timer.Advance(advanceTo);
            ulong[] ticks = timer.Trace.UtteranceTicks.ToArray();
            bool invoked = ticks.Length > 0;
            string source = invoked ? "native-kernel/tickFrame" : "";

            int sent = 0;
            int n = spec.Bots.Count;
            int parts = Math.Max(dues.Count, 1);
            for (int p = 0; p < dues.Count; p++)
            {
                int start = p * n / parts;
                int end = (p + 1) * n / parts;
                for (int i = start; i < end; i++)
                {
                    SendText(sockets[i], spec.Bots[i].Envelope);
                    sent++;
                    if (!string.IsNullOrWhiteSpace(spec.SentPath))
                    {
                        File.WriteAllText(spec.SentPath, sent.ToString());
                    }
                }

                Thread.Sleep(400);
            }

            WriteTrace(spec, invoked && sent == n, source, ticks, sent, null);
            return invoked && sent == n ? 0 : 2;
        }
        finally
        {
            foreach (ClientWebSocket ws in sockets)
            {
                try
                {
                    ws.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private static void SendText(ClientWebSocket ws, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private static void DrainInBackground(ClientWebSocket ws)
    {
        _ = Task.Run(async () =>
        {
            var buffer = new byte[65536];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    await ws.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
            }
        });
    }

    private static void WriteTrace(
        FleetSpec spec,
        bool ok,
        string tickSource,
        ulong[] utteranceTicks,
        int submitted,
        string? blocked)
    {
        var body = new Dictionary<string, object?>
        {
            ["ok"] = ok,
            ["tickSource"] = tickSource,
            ["cadence"] = tickSource,
            ["utteranceTicks"] = utteranceTicks.Select(tick => (long)tick).ToArray(),
            ["timerManagerInvoked"] = utteranceTicks.Length > 0,
            ["submitted"] = submitted,
            ["process"] = "Lumio.Client.Bot.Host",
            ["pid"] = Environment.ProcessId,
            ["blocked"] = blocked,
        };
        string json = JsonSerializer.Serialize(body);
        if (!string.IsNullOrWhiteSpace(spec.TracePath))
        {
            string? dir = Path.GetDirectoryName(spec.TracePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(spec.TracePath, json + "\n");
        }
    }

    private static void TryWriteBlocked(string specPath, string reason)
    {
        try
        {
            FleetSpec? spec = JsonSerializer.Deserialize<FleetSpec>(
                File.ReadAllText(specPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (spec != null)
            {
                WriteTrace(spec, false, "", Array.Empty<ulong>(), 0, reason);
            }
        }
        catch (Exception)
        {
        }
    }

    private sealed class FleetSpec
    {
        public string RoomUri { get; set; } = "";

        public string EngineNative { get; set; } = "";

        public string TracePath { get; set; } = "";

        public string SentPath { get; set; } = "";

        public ulong AdvanceToTick { get; set; }

        public List<BotSpec> Bots { get; set; } = new List<BotSpec>();
    }

    private sealed class BotSpec
    {
        public string ConnectionId { get; set; } = "";

        public string Envelope { get; set; } = "";
    }
}

internal sealed class NativeTickFrameAbi : INativeTimerAbi, IDisposable
{
    private const int Success = 0;
    private const int DrainRecordBytes = 40;

    private readonly IntPtr _module;
    private readonly CreateManagerFn _createManager;
    private readonly DestroyManagerFn _destroyManager;
    private readonly RegisterDispatchFn _registerDispatch;
    private readonly RegisterScopeFn _registerScope;
    private readonly CreateSlotFn _createSlot;
    private readonly BindSlotFn _bindSlot;
    private readonly ScheduleRepeatingFn _scheduleRepeating;
    private readonly AdvanceFn _advance;
    private readonly DrainFn _drain;

    public NativeTickFrameAbi(string nativePath)
    {
        _module = LoadLibraryW(nativePath);
        if (_module == IntPtr.Zero)
        {
            throw new InvalidOperationException("LoadLibraryW failed for engine native");
        }

        IntPtr proc = GetProcAddress(_module, "lumio_engine_get_api_v1");
        if (proc == IntPtr.Zero)
        {
            throw new InvalidOperationException("lumio_engine_get_api_v1 missing");
        }

        var getApi = Marshal.GetDelegateForFunctionPointer<GetApiV1>(proc);
        int rc = getApi(1, out IntPtr table);
        if (rc != Success || table == IntPtr.Zero)
        {
            throw new InvalidOperationException("lumio_engine_get_api_v1 status " + rc);
        }

        uint size = (uint)Marshal.ReadInt32(table, 4);
        if (size < 200)
        {
            throw new InvalidOperationException("native ABI struct_size missing timer slots");
        }

        _createManager = Fn<CreateManagerFn>(table, 88);
        _destroyManager = Fn<DestroyManagerFn>(table, 96);
        _registerDispatch = Fn<RegisterDispatchFn>(table, 104);
        _registerScope = Fn<RegisterScopeFn>(table, 112);
        _createSlot = Fn<CreateSlotFn>(table, 128);
        _bindSlot = Fn<BindSlotFn>(table, 136);
        _scheduleRepeating = Fn<ScheduleRepeatingFn>(table, 160);
        _advance = Fn<AdvanceFn>(table, 176);
        _drain = Fn<DrainFn>(table, 192);
    }

    public int CreateManager(uint mode, out IntPtr manager)
    {
        return _createManager(mode, out manager);
    }

    public int DestroyManager(IntPtr manager)
    {
        return _destroyManager(manager);
    }

    public int RegisterDispatch(IntPtr manager, uint dispatchId)
    {
        return _registerDispatch(manager, dispatchId);
    }

    public int RegisterScope(IntPtr manager, ulong scopeId, uint scopeKind, out uint generation)
    {
        return _registerScope(manager, scopeId, scopeKind, out generation);
    }

    public int CreateSlot(IntPtr manager, out IntPtr slot)
    {
        return _createSlot(manager, out slot);
    }

    public int BindSlot(IntPtr manager, IntPtr slot, uint dispatchId)
    {
        return _bindSlot(manager, slot, dispatchId);
    }

    public int ScheduleRepeating(
        IntPtr manager,
        ulong scopeId,
        uint scopeKind,
        uint scopeGeneration,
        ulong firstDue,
        ulong interval,
        IntPtr slot,
        out NativeTimerHandle handle)
    {
        int rc = _scheduleRepeating(
            manager,
            scopeId,
            scopeKind,
            scopeGeneration,
            firstDue,
            interval,
            slot,
            out TimerHandleAbi abi);
        handle = new NativeTimerHandle(abi.Index, abi.Generation, abi.Context);
        return rc;
    }

    public int Advance(IntPtr manager, ulong toTick)
    {
        return _advance(manager, toTick);
    }

    public int Drain(IntPtr manager, Span<NativeTimerDrainRecord> records, out int count)
    {
        count = 0;
        int cap = records.Length;
        IntPtr buf = IntPtr.Zero;
        try
        {
            if (cap > 0)
            {
                buf = Marshal.AllocHGlobal(checked(cap * DrainRecordBytes));
            }

            int status = _drain(manager, buf, (uint)cap, out uint nativeCount);
            count = (int)nativeCount;
            if (status != Success || buf == IntPtr.Zero)
            {
                return status;
            }

            int copy = Math.Min(count, cap);
            for (int i = 0; i < copy; i++)
            {
                IntPtr row = buf + (i * DrainRecordBytes);
                ulong due = unchecked((ulong)Marshal.ReadInt64(row, 16));
                ulong seq = unchecked((ulong)Marshal.ReadInt64(row, 24));
                uint dispatch = unchecked((uint)Marshal.ReadInt32(row, 32));
                records[i] = new NativeTimerDrainRecord(due, seq, dispatch);
            }

            return status;
        }
        finally
        {
            if (buf != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    public void Dispose()
    {
    }

    private static T Fn<T>(IntPtr table, int offset)
        where T : Delegate
    {
        IntPtr ptr = Marshal.ReadIntPtr(table, offset);
        if (ptr == IntPtr.Zero)
        {
            throw new InvalidOperationException("null timer slot at +" + offset);
        }

        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetApiV1(uint version, out IntPtr table);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateManagerFn(uint mode, out IntPtr manager);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DestroyManagerFn(IntPtr manager);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RegisterDispatchFn(IntPtr manager, uint dispatchId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RegisterScopeFn(IntPtr manager, ulong scopeId, uint scopeKind, out uint generation);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateSlotFn(IntPtr manager, out IntPtr slot);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BindSlotFn(IntPtr manager, IntPtr slot, uint dispatchId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ScheduleRepeatingFn(
        IntPtr manager,
        ulong scopeId,
        uint scopeKind,
        uint scopeGeneration,
        ulong firstDue,
        ulong interval,
        IntPtr slot,
        out TimerHandleAbi handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AdvanceFn(IntPtr manager, ulong toTick);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DrainFn(IntPtr manager, IntPtr records, uint capacity, out uint count);

    [StructLayout(LayoutKind.Sequential)]
    private struct TimerHandleAbi
    {
        public uint Index;
        public uint Generation;
        public ulong Context;
    }
}
