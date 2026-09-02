using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.App;

/// <summary>
/// Hosts <c>Lumio.Game.ServerGameplay.ChatRoomWorld</c> through Assembly.LoadFrom.
/// Owner-thread calls are serialized on a named supervisor thread.
/// </summary>
internal sealed class ChatRoomWorldAdapter : IDisposable
{
    private readonly ConcurrentQueue<Action> work = new();
    private readonly Dictionary<string, object> worlds = new(StringComparer.Ordinal);
    private readonly Assembly? gameplay;
    private readonly Type? worldType;
    private readonly Type? chatInputType;
    private int ownerThreadId;
    private bool disposed;

    internal ChatRoomWorldAdapter(INamedThreadSupervisor threads)
    {
        ArgumentNullException.ThrowIfNull(threads);
        if (GameplayAssemblyDiscovery.TryFind(out var path))
        {
            gameplay = Assembly.LoadFrom(path);
            worldType = gameplay.GetType("Lumio.Game.ServerGameplay.ChatRoomWorld");
            chatInputType = gameplay.GetType("Lumio.Game.ServerGameplay.ChatInput");
        }

        _ = threads.Start("live11-gameplay", new OwnerBody(this));
    }

    internal bool IsLoaded => worldType is not null && chatInputType is not null;

    internal string[] RoomIds
    {
        get
        {
            return OnOwner(() =>
            {
                var ids = new string[worlds.Count];
                worlds.Keys.CopyTo(ids, 0);
                return ids;
            });
        }
    }

    internal bool TryCreateEntity(string roomId, ulong netEntityId)
    {
        return OnOwner(() =>
        {
            if (!TryWorld(roomId, create: true, out var world) || worldType is null)
            {
                return false;
            }

            return (bool)worldType.GetMethod("TryCreateEntity")!.Invoke(world, new object[] { netEntityId })!;
        });
    }

    internal bool DestroyEntity(string roomId, ulong netEntityId)
    {
        return OnOwner(() =>
        {
            if (!TryWorld(roomId, create: false, out var world) || worldType is null)
            {
                return false;
            }

            return (bool)worldType.GetMethod("DestroyEntity")!.Invoke(world, new object[] { netEntityId })!;
        });
    }

    internal bool TryAdmitChat(string roomId, ulong sender, string text, out string kind, out string? error)
    {
        var result = OnOwner(() =>
        {
            if (!TryWorld(roomId, create: false, out var world)
                || worldType is null
                || chatInputType is null)
            {
                return ("Rejected", "invalid_request");
            }

            var input = Activator.CreateInstance(chatInputType, text)!;
            var op = worldType.GetMethod("AdmitChatInput")!.Invoke(world, new object[] { sender, input })!;
            var kindValue = Convert.ToInt32(op.GetType().GetProperty("Kind")!.GetValue(op)!, CultureInfo.InvariantCulture);
            var code = op.GetType().GetProperty("ErrorCode")!.GetValue(op) as string;
            var kindName = kindValue switch
            {
                0 => "Admitted",
                1 => "Committed",
                3 => "Fatal",
                _ => "Rejected",
            };
            return (kindName, code ?? string.Empty);
        });
        kind = result.Item1;
        error = string.IsNullOrEmpty(result.Item2) ? null : result.Item2;
        return string.Equals(kind, "Admitted", StringComparison.Ordinal)
            || string.Equals(kind, "Committed", StringComparison.Ordinal);
    }

    internal TickSnapshot RunTick(string roomId)
    {
        return OnOwner(() =>
        {
            if (!TryWorld(roomId, create: false, out var world) || worldType is null)
            {
                return new TickSnapshot(0, Array.Empty<TickEvent>());
            }

            var tick = worldType.GetMethod("RunTick")!.Invoke(world, Array.Empty<object>())!;
            var applied = Convert.ToUInt64(
                tick.GetType().GetProperty("AppliedTick")!.GetValue(tick)!,
                CultureInfo.InvariantCulture);
            var events = new List<TickEvent>();
            if (tick.GetType().GetProperty("Events")!.GetValue(tick) is Array rows)
            {
                foreach (var row in rows)
                {
                    var type = row.GetType();
                    events.Add(new TickEvent(
                        Convert.ToUInt64(type.GetProperty("MessageId")!.GetValue(row)!, CultureInfo.InvariantCulture),
                        Convert.ToUInt64(type.GetProperty("RoomSequence")!.GetValue(row)!, CultureInfo.InvariantCulture),
                        Convert.ToUInt64(type.GetProperty("SenderNetEntityId")!.GetValue(row)!, CultureInfo.InvariantCulture),
                        type.GetProperty("Text")!.GetValue(row) as string ?? string.Empty,
                        Convert.ToUInt64(type.GetProperty("AppliedTick")!.GetValue(row)!, CultureInfo.InvariantCulture)));
                }
            }

            return new TickSnapshot(applied, events.ToArray());
        });
    }

    internal bool TryGetLastMessage(string roomId, ulong netEntityId, out string text, out ulong tick)
    {
        var result = OnOwner(() =>
        {
            if (!TryWorld(roomId, create: false, out var world) || worldType is null)
            {
                return (false, string.Empty, 0UL);
            }

            object?[] args = { netEntityId, null };
            var ok = (bool)worldType.GetMethod("TryGetComponent")!.Invoke(world, args)!;
            if (!ok || args[1] is null)
            {
                return (false, string.Empty, 0UL);
            }

            var component = args[1]!;
            var lastText = component.GetType().GetProperty("LastMessageText")!.GetValue(component) as string ?? string.Empty;
            var lastTick = Convert.ToUInt64(
                component.GetType().GetProperty("LastMessageTick")!.GetValue(component)!,
                CultureInfo.InvariantCulture);
            return (true, lastText, lastTick);
        });
        text = result.Item2;
        tick = result.Item3;
        return result.Item1;
    }

    internal bool TryRestoreLastMessage(string roomId, ulong netEntityId, string text, ulong lastMessageTick)
    {
        return OnOwner(() =>
        {
            if (!TryWorld(roomId, create: true, out var world) || worldType is null)
            {
                return false;
            }

            _ = (bool)worldType.GetMethod("TryCreateEntity")!.Invoke(world, new object[] { netEntityId })!;
            return (bool)worldType.GetMethod("TryRestoreLastMessage")!.Invoke(
                world,
                new object[] { netEntityId, text, lastMessageTick })!;
        });
    }

    internal static bool TryParseGameplayId(string netEntityId, out ulong id)
    {
        id = 0;
        if (string.IsNullOrEmpty(netEntityId)
            || !netEntityId.StartsWith("nent_", StringComparison.Ordinal)
            || netEntityId.Length != 37)
        {
            return false;
        }

        return ulong.TryParse(
            netEntityId.AsSpan(21),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out id)
            && id != 0;
    }

    public void Dispose()
    {
        disposed = true;
    }

    private bool TryWorld(string roomId, bool create, out object? world)
    {
        world = null;
        if (worldType is null || string.IsNullOrEmpty(roomId))
        {
            return false;
        }

        if (worlds.TryGetValue(roomId, out world))
        {
            return true;
        }

        if (!create)
        {
            return false;
        }

        world = Activator.CreateInstance(worldType);
        if (world is null)
        {
            return false;
        }

        worlds[roomId] = world;
        return true;
    }

    private T OnOwner<T>(Func<T> work)
    {
        if (disposed)
        {
            return default!;
        }

        if (ownerThreadId != 0 && Environment.CurrentManagedThreadId == ownerThreadId)
        {
            return work();
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.work.Enqueue(() =>
        {
            try
            {
                done.TrySetResult(work());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    private sealed class OwnerBody(ChatRoomWorldAdapter owner) : IThreadBody
    {
        public ThreadStepResult Step(CancellationToken ct)
        {
            if (ct.IsCancellationRequested || owner.disposed)
            {
                return new ThreadStepResult(false, null);
            }

            if (owner.ownerThreadId == 0)
            {
                owner.ownerThreadId = Environment.CurrentManagedThreadId;
            }

            var ran = false;
            while (owner.work.TryDequeue(out var action))
            {
                ran = true;
                action();
            }

            if (!ran)
            {
                ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(5));
            }

            return new ThreadStepResult(true, null);
        }
    }

    internal readonly record struct TickSnapshot(ulong AppliedTick, TickEvent[] Events);

    internal readonly record struct TickEvent(
        ulong MessageId,
        ulong RoomSequence,
        ulong SenderNetEntityId,
        string Text,
        ulong AppliedTick);
}
