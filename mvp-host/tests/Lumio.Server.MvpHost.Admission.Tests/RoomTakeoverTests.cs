using Lumio.Server.MvpHost.Admission;
using Xunit;

namespace Lumio.Server.MvpHost.Admission.Tests;

public sealed class RoomTakeoverTests
{
    [Fact]
    public void DuplicateLiveAdmissionTakeoverKeepsNetEntityIdAndIncrementsGeneration()
    {
        var harness = new AdmissionHarness();
        var first = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-old", "alice", botToolContext: false);
        Assert.Equal(1UL, first.Binding.ConnectionGeneration);
        Assert.Null(first.TerminationNotice);

        var second = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-new", "alice", botToolContext: false);

        Assert.Equal(first.Binding.NetEntityId, second.Binding.NetEntityId);
        Assert.Equal(first.Binding.AccountId, second.Binding.AccountId);
        Assert.Equal(AdmissionHarness.MainRoom, second.Binding.RoomId);
        Assert.Equal(BoundEntityKind.Player, second.Binding.EntityType);
        Assert.Equal(2UL, second.Binding.ConnectionGeneration);
        Assert.Equal("conn-old", second.SupersededConnectionId);

        var notice = Assert.IsType<TakeoverNotice>(second.TerminationNotice);
        Assert.Equal(EntityBindingPort.TakeoverReasonCode, notice.ReasonCode);
        Assert.Equal("connection_superseded", notice.ReasonCode);
        Assert.True(notice.ReconnectEligible);
        Assert.Equal(harness.Clock.UnixSeconds, notice.IssuedAt);

        Assert.True(harness.Registry.TryGetTerminationNotice("conn-old", out var observed));
        Assert.Equal(notice, observed);

        Assert.False(harness.Registry.TryGetBindingByConnection(AdmissionHarness.MainRoom, "conn-old", out _));
        Assert.True(harness.Registry.TryGetBindingByConnection(
            AdmissionHarness.MainRoom,
            "conn-new",
            out var live));
        Assert.Equal(second.Binding, live);
    }

    [Fact]
    public void RepeatedTakeoverStrictlyIncrementsConnectionGeneration()
    {
        var harness = new AdmissionHarness();
        var first = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-1", "alice", false);
        var second = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-2", "alice", false);
        var third = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-3", "alice", false);

        Assert.Equal(first.Binding.NetEntityId, third.Binding.NetEntityId);
        Assert.Equal(1UL, first.Binding.ConnectionGeneration);
        Assert.Equal(2UL, second.Binding.ConnectionGeneration);
        Assert.Equal(3UL, third.Binding.ConnectionGeneration);
        Assert.True(third.Binding.ConnectionGeneration > second.Binding.ConnectionGeneration);
        Assert.True(second.Binding.ConnectionGeneration > first.Binding.ConnectionGeneration);
    }

    [Fact]
    public void StaleGenerationAfterTakeoverIsRejected()
    {
        var harness = new AdmissionHarness();
        var first = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-1", "alice", false);
        harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-2", "alice", false);

        var resolved = harness.Registry.ResolveByNetEntityId(
            AdmissionHarness.MainRoom,
            first.Binding.NetEntityId,
            connectionGeneration: 1);
        var rejected = Assert.IsType<BindingResolveOutcome.Rejected>(resolved);
        Assert.Equal(EntityBindingPort.StaleGeneration, rejected.Code);
    }
}
