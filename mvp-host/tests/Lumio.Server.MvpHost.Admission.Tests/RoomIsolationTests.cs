using Lumio.Server.MvpHost.Admission;
using Xunit;

namespace Lumio.Server.MvpHost.Admission.Tests;

public sealed class RoomIsolationTests
{
    [Fact]
    public void SecondRoomCannotSeeOrAddressFirstRoomEntities()
    {
        var harness = new AdmissionHarness();
        var first = harness.AdmitAccepted("room-1", "conn-alice", "alice", false);
        harness.AdmitAccepted("room-1", "conn-Bot01", "Bot01", true);

        Assert.Empty(harness.Registry.ListBindings("room-2"));
        Assert.Equal(0, harness.Registry.CountEntities("room-2", BoundEntityKind.Player));
        Assert.Equal(0, harness.Registry.CountEntities("room-2", BoundEntityKind.Bot));
        Assert.False(harness.Registry.TryGetBindingByConnection("room-2", "conn-alice", out _));

        var resolved = harness.Registry.ResolveByNetEntityId("room-2", first.Binding.NetEntityId);
        var rejected = Assert.IsType<BindingResolveOutcome.Rejected>(resolved);
        Assert.Equal(EntityBindingPort.CrossRoomReference, rejected.Code);

        var home = Assert.IsType<BindingResolveOutcome.Found>(
            harness.Registry.ResolveByNetEntityId("room-1", first.Binding.NetEntityId));
        Assert.Equal(first.Binding, home.Binding);
    }

    [Fact]
    public void OneAccountHasASingleActiveRoomBinding()
    {
        var harness = new AdmissionHarness();
        var first = harness.AdmitAccepted("room-1", "conn-1", "alice", false);

        var second = harness.Admit("room-2", "conn-2", "alice", false);
        var rejected = Assert.IsType<RoomAdmitOutcome.Rejected>(second);
        Assert.Equal(EntityBindingPort.InvalidRequest, rejected.Code);

        Assert.True(harness.Registry.TryGetBindingByAccount(harness.AccountIdFor("alice"), out var active));
        Assert.Equal(first.Binding, active);
        Assert.Equal("room-1", active.RoomId);
        Assert.Equal(first.Binding.NetEntityId, active.NetEntityId);
        Assert.Equal(1UL, active.ConnectionGeneration);

        var home = Assert.IsType<BindingResolveOutcome.Found>(
            harness.Registry.ResolveByNetEntityId("room-1", first.Binding.NetEntityId));
        Assert.Equal(first.Binding, home.Binding);

        Assert.True(harness.Registry.TryGetBindingByConnection("room-1", "conn-1", out var stillLive));
        Assert.Equal(first.Binding, stillLive);
        Assert.False(harness.Registry.TryGetTerminationNotice("conn-1", out _));
        Assert.Empty(harness.Registry.ListBindings("room-2"));
        Assert.False(harness.Registry.TryGetBindingByConnection("room-2", "conn-2", out _));
    }
}
