using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.Simulation.Reference;

/// <summary>
/// Production absence-filler for <see cref="IWorldSimulationPort"/>.
///
/// The model is intentionally limited to an opaque byte-key to byte-value
/// overwrite table.  It is not a test double and does not define gameplay
/// state.  The mutation sink is out of band: producers only enqueue bytes;
/// the owner invoking <see cref="RunTick"/> applies them at the start of the
/// next call.
/// </summary>
public sealed class ReferenceWorldSimulation : IWorldSimulationPort, IWorldMutationSink
{
    internal const int MutationInboxCapacity = 32;
    private const long MutationInboxMaxBytes = MutationInboxCapacity * 64L * 1024L;
    private const string InvalidArgument = "InvalidArgument";
    private const string WrongContext = "WrongContext";
    private const string QueueFull = "QueueFull";
    private const string ContextClosing = "ContextClosing";
    private const string ContextDestroyed = "ContextDestroyed";
    private const string InternalInvariant = "InternalInvariant";
    private const string StateFormat = "Lumio.Reference.State.v1";
    private const ulong SeedMixConstant = 0x9E3779B97F4A7C15UL;

    private readonly object stateGate = new();
    private readonly IBoundedInbox<QueuedMutation> mutationInbox;
    private readonly Dictionary<ByteKey, byte[]> values = new();
    private readonly ulong creationSeed;

    private HostSimulationState state = HostSimulationState.Created;
    private HostSessionId session;
    private HostWorldSlotId slot;
    private byte[] configuration = Array.Empty<byte>();
    private ulong effectiveSeed;
    private ulong authorityRevision;

    private ReferenceWorldSimulation(ulong deterministicSeed)
    {
        this.creationSeed = deterministicSeed;
        this.effectiveSeed = deterministicSeed;
        this.mutationInbox = PlatformModule.CreateInbox<QueuedMutation>(
            new QueueBudget(MutationInboxCapacity, MutationInboxMaxBytes));
    }

    /// <summary>Creates an isolated simulation instance with a deterministic seed.</summary>
    public static ReferenceWorldSimulation Create(ulong deterministicSeed)
        => new(deterministicSeed);

    /// <inheritdoc />
    public HostSimulationState State
    {
        get
        {
            lock (this.stateGate)
            {
                return this.state;
            }
        }
    }

    /// <summary>
    /// The current authoritative revision.  It changes only when an applied
    /// overwrite changes a stored value.
    /// </summary>
    public ulong AuthorityRevision
    {
        get
        {
            lock (this.stateGate)
            {
                return this.authorityRevision;
            }
        }
    }

    /// <summary>Exposes the queue budget to focused production-shape tests.</summary>
    internal QueueBudget MutationInboxBudget => this.mutationInbox.Budget;

    /// <summary>Exposes pending depth without exposing opaque payloads.</summary>
    internal int MutationInboxCount => this.mutationInbox.Count;

    /// <inheritdoc />
    public HostLifecycleResult Initialize(in HostSessionInit init)
    {
        lock (this.stateGate)
        {
            if (this.state != HostSimulationState.Created)
            {
                return this.LifecycleFailure(this.StateError());
            }

            if (string.IsNullOrWhiteSpace(init.Session.Value) || init.Slot.Value == 0)
            {
                return this.LifecycleFailure(InvalidArgument);
            }

            this.session = init.Session;
            this.slot = init.Slot;
            this.configuration = init.OpaqueConfig.ToArray();
            this.effectiveSeed = MixSeeds(this.creationSeed, init.DeterministicSeed);
            this.state = HostSimulationState.Initialized;
            return new HostLifecycleResult(true, this.state, null);
        }
    }

    /// <inheritdoc />
    public HostLifecycleResult Ready()
    {
        lock (this.stateGate)
        {
            if (this.state != HostSimulationState.Initialized)
            {
                return this.LifecycleFailure(this.StateError());
            }

            this.state = HostSimulationState.Ready;
            return new HostLifecycleResult(true, this.state, null);
        }
    }

    /// <inheritdoc />
    public HostTickOutcome RunTick(in HostTickRequest request)
    {
        lock (this.stateGate)
        {
            if (this.state is not (HostSimulationState.Ready or HostSimulationState.Running))
            {
                return this.RejectedTick(request, this.StateError());
            }

            try
            {
                // The owner is the only caller that mutates the table.  Drain
                // the out-of-band inbox before looking at this call's ingress.
                while (this.mutationInbox.TryDequeue(out var mutation))
                {
                    if (this.Apply(mutation.Bytes))
                    {
                        this.authorityRevision = checked(this.authorityRevision + 1UL);
                    }

                    mutation.Clear();
                }

                // Ingress is intentionally opaque to this absence-filler.  It
                // is consumed (and never retained) after the mutation batch.
                foreach (var frame in request.Ingress.Span)
                {
                    _ = frame.Bytes.Length;
                }

                this.state = HostSimulationState.Running;
                var hash = this.ComputeStateHash(request.Tick.Value, request.DeterministicSeed);
                return new HostTickOutcome(
                    HostTickStatus.Completed,
                    request.Tick,
                    hash,
                    this.authorityRevision,
                    ReadOnlyMemory<WireFrame>.Empty,
                    HostFaultClass.None,
                    null);
            }
            catch (OverflowException)
            {
                return this.FailTick(request);
            }
            catch (IOException)
            {
                return this.FailTick(request);
            }
        }
    }

    /// <inheritdoc />
    public HostLifecycleResult Drain()
    {
        lock (this.stateGate)
        {
            if (this.state is not (HostSimulationState.Ready or HostSimulationState.Running))
            {
                return this.LifecycleFailure(this.StateError());
            }

            this.state = HostSimulationState.Draining;
            this.mutationInbox.Close();
            return new HostLifecycleResult(true, this.state, null);
        }
    }

    /// <inheritdoc />
    public HostLifecycleResult Snapshot(out ReadOnlyMemory<byte> opaqueSnapshot)
    {
        lock (this.stateGate)
        {
            if (this.state != HostSimulationState.Draining)
            {
                opaqueSnapshot = ReadOnlyMemory<byte>.Empty;
                return this.LifecycleFailure(this.StateError());
            }

            try
            {
                // A snapshot is a deterministic opaque representation of the
                // table; it contains no wall-clock or process-local identity.
                opaqueSnapshot = this.SerializeState();
                this.state = HostSimulationState.Snapshotted;
                return new HostLifecycleResult(true, this.state, null);
            }
            catch (IOException)
            {
                opaqueSnapshot = ReadOnlyMemory<byte>.Empty;
                this.state = HostSimulationState.Faulted;
                return new HostLifecycleResult(false, this.state, InternalInvariant);
            }
        }
    }

    /// <inheritdoc />
    public EnqueueResult TryEnqueueOpaqueMutation(ReadOnlyMemory<byte> opaqueCommand)
    {
        lock (this.stateGate)
        {
            if (this.state == HostSimulationState.Disposed || this.state == HostSimulationState.Faulted)
            {
                return new EnqueueResult(EnqueueStatus.Closed, ContextDestroyed);
            }

            if (this.state is HostSimulationState.Draining or HostSimulationState.Snapshotted)
            {
                return new EnqueueResult(EnqueueStatus.Closed, ContextClosing);
            }

            if (this.state == HostSimulationState.Created)
            {
                return new EnqueueResult(EnqueueStatus.Closed, WrongContext);
            }

            var mutation = new QueuedMutation(opaqueCommand);
            var result = this.mutationInbox.TryEnqueue(in mutation);
            mutation.Clear();
            if (result.Status == EnqueueStatus.Full)
            {
                return new EnqueueResult(EnqueueStatus.Full, QueueFull);
            }

            return result;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (this.stateGate)
        {
            if (this.state == HostSimulationState.Disposed)
            {
                return;
            }

            this.mutationInbox.Close();
            while (this.mutationInbox.TryDequeue(out var mutation))
            {
                mutation.Clear();
            }

            foreach (var entry in this.values)
            {
                entry.Key.Clear();
                Array.Clear(entry.Value);
            }

            this.values.Clear();
            Array.Clear(this.configuration);
            this.configuration = Array.Empty<byte>();
            this.state = HostSimulationState.Disposed;
        }
    }

    private bool Apply(ReadOnlySpan<byte> command)
    {
        Decode(command, out var keyBytes, out var valueBytes);
        var key = new ByteKey(keyBytes);

        if (this.values.TryGetValue(key, out var previous)
            && previous.AsSpan().SequenceEqual(valueBytes))
        {
            return false;
        }

        this.values[key] = valueBytes.ToArray();
        return true;
    }

    private HostLifecycleResult LifecycleFailure(string error)
        => new(false, this.state, error);

    private string StateError()
        => this.state is HostSimulationState.Disposed or HostSimulationState.Faulted
            ? ContextDestroyed
            : WrongContext;

    private HostTickOutcome RejectedTick(in HostTickRequest request, string error)
        => new(
            HostTickStatus.Rejected,
            request.Tick,
            ReadOnlyMemory<byte>.Empty,
            this.authorityRevision,
            ReadOnlyMemory<WireFrame>.Empty,
            null,
            error);

    private HostTickOutcome FailTick(in HostTickRequest request)
    {
        this.state = HostSimulationState.Faulted;
        this.mutationInbox.Close();
        return new HostTickOutcome(
            HostTickStatus.Faulted,
            request.Tick,
            this.ComputeStateHash(request.Tick.Value, request.DeterministicSeed),
            this.authorityRevision,
            ReadOnlyMemory<WireFrame>.Empty,
            HostFaultClass.SlotStateUnproven,
            InternalInvariant);
    }

    private byte[] ComputeStateHash(ulong tick, ulong requestSeed)
        => SHA256.HashData(this.SerializeState(tick, requestSeed, includeCallInputs: true));

    private byte[] SerializeState()
        => this.SerializeState(0, 0, includeCallInputs: false);

    private byte[] SerializeState(ulong tick, ulong requestSeed, bool includeCallInputs)
    {
        var entries = new List<KeyValuePair<ByteKey, byte[]>>(this.values);
        entries.Sort(static (left, right) => left.Key.CompareTo(right.Key));

        using var stream = new MemoryStream();
        var marker = Encoding.ASCII.GetBytes(StateFormat);
        stream.Write(marker);
        WriteUInt64(stream, this.effectiveSeed);
        WriteUInt64(stream, this.authorityRevision);
        WriteUInt32(stream, checked((uint)this.configuration.Length));
        stream.Write(this.configuration);

        if (includeCallInputs)
        {
            WriteUInt64(stream, tick);
            WriteUInt64(stream, requestSeed);
        }

        WriteUInt32(stream, checked((uint)entries.Count));
        foreach (var entry in entries)
        {
            WriteUInt32(stream, checked((uint)entry.Key.Length));
            stream.Write(entry.Key.Bytes);
            WriteUInt32(stream, checked((uint)entry.Value.Length));
            stream.Write(entry.Value);
        }

        return stream.ToArray();
    }

    private static void Decode(
        ReadOnlySpan<byte> source,
        out ReadOnlySpan<byte> key,
        out ReadOnlySpan<byte> value)
    {
        // Canonical private framing: u32 key length, u32 value length, key,
        // value.  A u32 key-length plus remainder and a NUL delimiter are
        // accepted as compatibility forms for callers that already own bytes.
        if (source.Length >= 8)
        {
            var keyLength = BinaryPrimitives.ReadUInt32LittleEndian(source);
            var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(source[4..]);
            if (keyLength <= int.MaxValue
                && valueLength <= int.MaxValue
                && 8L + keyLength + valueLength == source.Length)
            {
                var keyEnd = 8 + (int)keyLength;
                key = source[8..keyEnd];
                value = source[keyEnd..];
                return;
            }
        }

        if (source.Length >= 4)
        {
            var keyLength = BinaryPrimitives.ReadUInt32LittleEndian(source);
            if (keyLength <= int.MaxValue && 4L + keyLength <= source.Length)
            {
                key = source[4..(4 + (int)keyLength)];
                value = source[(4 + (int)keyLength)..];
                return;
            }
        }

        var separator = source.IndexOf((byte)0);
        if (separator >= 0)
        {
            key = source[..separator];
            value = source[(separator + 1)..];
            return;
        }

        if (source.Length == 0)
        {
            key = ReadOnlySpan<byte>.Empty;
            value = ReadOnlySpan<byte>.Empty;
            return;
        }

        key = source[..1];
        value = source[1..];
    }

    private static ulong MixSeeds(ulong left, ulong right)
    {
        var mixed = left ^ (right + SeedMixConstant + (left << 6) + (left >> 2));
        mixed ^= mixed >> 30;
        mixed *= 0xBF58476D1CE4E5B9UL;
        mixed ^= mixed >> 27;
        mixed *= 0x94D049BB133111EBUL;
        return mixed ^ (mixed >> 31);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private sealed class QueuedMutation : IDefensiveCopy<QueuedMutation>
    {
        internal QueuedMutation(ReadOnlyMemory<byte> bytes)
        {
            this.Bytes = bytes.ToArray();
        }

        internal byte[] Bytes { get; }

        public QueuedMutation DefensiveCopy()
            => new(this.Bytes);

        internal void Clear()
            => Array.Clear(this.Bytes);
    }

    private sealed class ByteKey : IEquatable<ByteKey>, IComparable<ByteKey>
    {
        internal ByteKey(ReadOnlySpan<byte> bytes)
        {
            this.Bytes = bytes.ToArray();
        }

        internal byte[] Bytes { get; }

        internal int Length => this.Bytes.Length;

        public bool Equals(ByteKey? other)
            => other is not null && this.Bytes.AsSpan().SequenceEqual(other.Bytes);

        public override bool Equals(object? obj)
            => obj is ByteKey other && this.Equals(other);

        public override int GetHashCode()
        {
            var hash = 2166136261u;
            foreach (var value in this.Bytes)
            {
                hash ^= value;
                hash *= 16777619u;
            }

            return unchecked((int)hash);
        }

        public int CompareTo(ByteKey? other)
        {
            if (other is null)
            {
                return 1;
            }

            var length = Math.Min(this.Bytes.Length, other.Bytes.Length);
            for (var index = 0; index < length; index++)
            {
                var comparison = this.Bytes[index].CompareTo(other.Bytes[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return this.Bytes.Length.CompareTo(other.Bytes.Length);
        }

        internal void Clear()
            => Array.Clear(this.Bytes);
    }
}
