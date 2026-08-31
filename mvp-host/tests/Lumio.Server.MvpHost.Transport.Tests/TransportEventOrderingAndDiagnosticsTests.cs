using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Platform;
using Xunit;

namespace Lumio.Server.MvpHost.Transport.Tests;

public sealed class TransportEventOrderingAndDiagnosticsTests
{
    [Fact]
    public void LateTerminalEventDoesNotOvertakeQueuedConnectionEvents()
    {
        using var harness = new TransportHarness();
        var id = ConnectionLifecycleTest.AcceptOne(harness);
        harness.Carrier.QueueInbound(id, TransportHarness.ValidEnvelope(sequence: 0));
        Assert.True(harness.Service.PumpReceiveOnce(id));
        Assert.Equal(
            EnqueueStatus.Accepted,
            harness.Service.TrySend(new ConnectionCommand.Bind(
                id,
                harness.Service.EpochOf(id),
                new PermissionGrantRef(1),
                new ServerSessionId("session-001"))).Status);
        harness.Carrier.QueueInbound(id, TransportHarness.ValidEnvelope(sequence: 1));
        Assert.True(harness.Service.PumpReceiveOnce(id));

        harness.Service.FillEventOutboxForTest();
        harness.Service.RaiseClosedForTest(id, ConnectionCloseReason.OwnerRequest);

        var events = ConnectionLifecycleTest.DrainEvents(harness);
        var accepted = Assert.IsType<ConnectionEvent.Accepted>(events[0]);
        var handshake = Assert.IsType<ConnectionEvent.HandshakeEnvelope>(events[1]);
        var ingress = Assert.IsType<ConnectionEvent.IngressReady>(events[2]);
        Assert.Equal(id, accepted.Id);
        Assert.Equal(id, handshake.Id);
        Assert.Equal(id, ingress.Id);
        Assert.IsType<ConnectionEvent.Closed>(events[^1]);
    }

    [Fact]
    public void NoDataPollingKeepsReceiveBufferDiagnosticsBounded()
    {
        using var harness = new TransportHarness();
        var id = ConnectionLifecycleTest.AcceptOne(harness);

        PollWithNoData(harness, id, 512);
        var countAtLimit = harness.Service.ReceiveBufferSizesForTest.Count;
        PollWithNoData(harness, id, 512);

        Assert.InRange(countAtLimit, 1, 256);
        Assert.Equal(countAtLimit, harness.Service.ReceiveBufferSizesForTest.Count);
    }

    [Fact]
    public void NonTerminalTailCannotConsumeFutureTerminalSlots()
    {
        using var harness = new TransportHarness(maxConnections: 2);
        var first = ConnectionLifecycleTest.AcceptOne(harness);
        var second = new TransportConnectionId(2);
        harness.Carrier.QueueAccept(second, "lumio.mvp.v0");
        Assert.True(harness.Service.TryAcceptOne());
        harness.Service.FillEventOutboxForTest();
        harness.Service.RaiseClosedForTest(first, ConnectionCloseReason.OwnerRequest);

        for (var i = 0; i < 300; i++)
        {
            harness.Service.RaiseBackpressuredForTest(second);
        }

        Assert.Equal(0, harness.Service.ConnectionCountForTest);
        var events = ConnectionLifecycleTest.DrainEvents(harness);
        Assert.Contains(events, evt => evt is ConnectionEvent.Closed closed && closed.Id == first);
        Assert.Contains(events, evt => evt is ConnectionEvent.Closed closed && closed.Id == second);
    }

    private static void PollWithNoData(
        TransportHarness harness,
        TransportConnectionId id,
        int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.False(harness.Service.PumpReceiveOnce(id));
        }
    }
}
