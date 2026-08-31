using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.MvpHost.HostContracts;
using Xunit;

namespace Lumio.Server.MvpHost.App.Tests;

public sealed class PollingByteCarrierTests
{
#pragma warning disable xUnit1051 // CancellationToken.None is intentional: it proves the inner receive is never cancelled by polling.
    [Fact]
    public async Task ReceivePollingKeepsOneUncancelledOperationAndCopiesTheFixedBuffer()
    {
        var inner = new PendingCarrier();
        var polling = CreatePollingCarrier(inner, TimeSpan.FromMilliseconds(1));
        var connection = new TransportConnectionId(7);
        var callerBuffer = new byte[32];

        var first = await polling.ReceiveAsync(connection, callerBuffer, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));
        var second = await polling.ReceiveAsync(connection, callerBuffer, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(first.Received);
        Assert.False(second.Received);
        Assert.Equal(1, inner.ReceiveCalls);
        Assert.Equal(CancellationToken.None, inner.LastToken);
        Assert.True(MemoryMarshal.TryGetArray<byte>(inner.Buffers[0], out var segment));
        Assert.False(ReferenceEquals(callerBuffer, segment.Array));

        new byte[] { 1, 2, 3 }.CopyTo(inner.Buffers[0].Span);
        inner.Completions[0].TrySetResult(new CarrierReceive(true, 3, true, false));

        var completed = await polling.ReceiveAsync(connection, callerBuffer, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(completed.Received);
        Assert.Equal(3, completed.ByteCount);
        Assert.Equal(new byte[] { 1, 2, 3 }, callerBuffer[..3]);
        Assert.Equal(1, inner.ReceiveCalls);

        _ = await polling.ReceiveAsync(connection, callerBuffer, CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, inner.ReceiveCalls);
    }
#pragma warning restore xUnit1051

    private static IByteCarrier CreatePollingCarrier(IByteCarrier inner, TimeSpan interval)
    {
        var outer = typeof(App.Program).Assembly.GetType(
            "Lumio.Server.MvpHost.App.FullGraphComposition",
            throwOnError: true)!;
        var nested = outer.GetNestedType("PollingByteCarrier", BindingFlags.NonPublic)!;
        var constructor = nested.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(IByteCarrier), typeof(TimeSpan) },
            modifiers: null)!;
        return (IByteCarrier)constructor.Invoke(new object[] { inner, interval });
    }

    private sealed class PendingCarrier : IByteCarrier
    {
        internal int ReceiveCalls { get; private set; }
        internal CancellationToken LastToken { get; private set; }
        internal List<Memory<byte>> Buffers { get; } = new();
        internal List<TaskCompletionSource<CarrierReceive>> Completions { get; } = new();

        public ValueTask<CarrierAccept> AcceptAsync(CancellationToken ct)
            => ValueTask.FromResult(new CarrierAccept(false, default, ImmutableArray<string>.Empty));

        public ValueTask<CarrierReceive> ReceiveAsync(
            TransportConnectionId connection,
            Memory<byte> buffer,
            CancellationToken ct)
        {
            _ = connection;
            ReceiveCalls++;
            LastToken = ct;
            Buffers.Add(buffer);
            var completion = new TaskCompletionSource<CarrierReceive>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Completions.Add(completion);
            if (ct.CanBeCanceled)
            {
                ct.Register(() => completion.TrySetResult(new CarrierReceive(false, 0, false, false)));
            }

            return new ValueTask<CarrierReceive>(completion.Task);
        }

        public bool TrySend(TransportConnectionId connection, ReadOnlyMemory<byte> bytes)
        {
            _ = connection;
            _ = bytes;
            return true;
        }

        public bool Close(TransportConnectionId connection, ConnectionCloseReason reason)
        {
            _ = connection;
            _ = reason;
            return true;
        }
    }
}
