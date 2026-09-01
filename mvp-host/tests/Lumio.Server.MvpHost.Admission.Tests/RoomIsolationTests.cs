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
        var moved = harness.AdmitAccepted("room-2", "conn-2", "alice", false);

        Assert.NotEqual(first.Binding.RoomId, moved.Binding.RoomId);
        Assert.Equal("room-2", moved.Binding.RoomId);
        Assert.Equal("conn-1", moved.SupersededConnectionId);
        Assert.NotNull(moved.TerminationNotice);
        Assert.Empty(harness.Registry.ListBindings("room-1"));
        Assert.True(harness.Registry.TryGetBindingByAccount(harness.AccountIdFor("alice"), out var active));
        Assert.Equal(moved.Binding, active);
        Assert.False(harness.Registry.TryGetBindingByConnection("room-1", "conn-1", out _));
    }
}
