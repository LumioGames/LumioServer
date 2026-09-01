using System;
using System.Linq;
using Lumio.Server.MvpHost.Admission;
using Lumio.Server.MvpHost.Platform;
using Xunit;

namespace Lumio.Server.MvpHost.Admission.Tests;

public sealed class RoomReconnectLifecycleTests
{
    [Fact]
    public void ProductionReconnectWindowIsFiveHostMinutesNotNativeTick()
    {
        Assert.Equal(300, AdmissionReconnectDefaults.ReconnectWindowSeconds);
        Assert.Equal(10, AdmissionReconnectDefaults.TestReconnectWindowSeconds);
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(AdmissionReconnectDefaults.ReconnectWindowSeconds));

        var names = typeof(ReconnectExpiryCommand)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(
            names,
            name => name.Contains("Tick", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Frame", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DisconnectedConnectionInputIsRejectedWhileOtherRoomClientsContinue()
    {
        var harness = new AdmissionHarness();
        var alice = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-alice", "alice", false);
        var bob = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-bob", "bob", false);

        Assert.True(harness.Registry.Disconnect(AdmissionHarness.MainRoom, "conn-alice"));

        var aliceInput = harness.Registry.TryAcceptInput(AdmissionHarness.MainRoom, "conn-alice");
        var aliceRejected = Assert.IsType<InputAdmissionOutcome.Rejected>(aliceInput);
        Assert.Equal(EntityBindingPort.BindingNotFound, aliceRejected.Code);

        var bobInput = harness.Registry.TryAcceptInput(AdmissionHarness.MainRoom, "conn-bob");
        var bobAccepted = Assert.IsType<InputAdmissionOutcome.Accepted>(bobInput);
        Assert.Equal(bob.Binding, bobAccepted.Binding);

        Assert.False(harness.Registry.TryGetBindingByConnection(AdmissionHarness.MainRoom, "conn-alice", out _));
        Assert.True(harness.Registry.TryGetBindingByConnection(AdmissionHarness.MainRoom, "conn-bob", out var bobLive));
        Assert.Equal(bob.Binding, bobLive);

        Assert.True(harness.Registry.TryGetPresence(
            AdmissionHarness.MainRoom,
            alice.Binding.NetEntityId,
            out var presence));
        Assert.Equal(BindingPresence.Disconnected, presence);

        var bindings = harness.Registry.ListBindings(AdmissionHarness.MainRoom);
        Assert.Equal(2, bindings.Count);
        Assert.Contains(alice.Binding.NetEntityId, bindings.Select(binding => binding.NetEntityId));
        Assert.Contains(bob.Binding, bindings);
        Assert.Equal(2, harness.Registry.CountEntities(AdmissionHarness.MainRoom, BoundEntityKind.Player));
    }

    [Fact]
    public void WindowReconnectDoesNotUnmapAnotherAccountOnTheVacatedConnectionId()
    {
        var harness = new AdmissionHarness();
        var alice = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-1", "alice", false);
        var aliceEntity = alice.Binding.NetEntityId;

        Assert.True(harness.Registry.Disconnect(AdmissionHarness.MainRoom, "conn-1"));

        var bob = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-1", "bob", false);
        Assert.NotEqual(aliceEntity, bob.Binding.NetEntityId);
        Assert.Equal(1UL, bob.Binding.ConnectionGeneration);

        var reconnected = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-2", "alice", false);

        Assert.Equal(aliceEntity, reconnected.Binding.NetEntityId);
        Assert.Equal(alice.Binding.AccountId, reconnected.Binding.AccountId);
        Assert.Equal(2UL, reconnected.Binding.ConnectionGeneration);

        var bobInput = harness.Registry.TryAcceptInput(AdmissionHarness.MainRoom, "conn-1");
        var bobAccepted = Assert.IsType<InputAdmissionOutcome.Accepted>(bobInput);
        Assert.Equal(bob.Binding, bobAccepted.Binding);
        Assert.True(harness.Registry.TryGetBindingByConnection(
            AdmissionHarness.MainRoom,
            "conn-1",
            out var bobLive));
        Assert.Equal(bob.Binding, bobLive);

        var aliceInput = harness.Registry.TryAcceptInput(AdmissionHarness.MainRoom, "conn-2");
        Assert.Equal(
            reconnected.Binding,
            Assert.IsType<InputAdmissionOutcome.Accepted>(aliceInput).Binding);
    }

    [Fact]
    public void ExpiryDoesNotUnmapAnotherAccountOnTheVacatedConnectionId()
    {
        var harness = new AdmissionHarness();
        var alice = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-1", "alice", false);
        var aliceEntity = alice.Binding.NetEntityId;

        Assert.True(harness.Registry.Disconnect(AdmissionHarness.MainRoom, "conn-1"));

        var bob = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-1", "bob", false);
        Assert.NotEqual(aliceEntity, bob.Binding.NetEntityId);
        Assert.Equal(1UL, bob.Binding.ConnectionGeneration);

        harness.Monotonic.Advance(TimeSpan.FromSeconds(AdmissionReconnectDefaults.TestReconnectWindowSeconds));
        harness.FireDueReconnectTimers();

        var bobInput = harness.Registry.TryAcceptInput(AdmissionHarness.MainRoom, "conn-1");
        var bobAccepted = Assert.IsType<InputAdmissionOutcome.Accepted>(bobInput);
        Assert.Equal(bob.Binding, bobAccepted.Binding);
        Assert.True(harness.Registry.TryGetBindingByConnection(
            AdmissionHarness.MainRoom,
            "conn-1",
            out var bobLive));
        Assert.Equal(bob.Binding, bobLive);

        Assert.True(harness.Registry.TryGetPresence(
            AdmissionHarness.MainRoom,
            aliceEntity,
            out var alicePresence));
        Assert.Equal(BindingPresence.Tombstoned, alicePresence);

        var later = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-2", "alice", false);
        Assert.NotEqual(aliceEntity, later.Binding.NetEntityId);
        Assert.Equal(alice.Binding.AccountId, later.Binding.AccountId);
        Assert.Equal(1UL, later.Binding.ConnectionGeneration);

        var staleA = harness.Registry.ResolveByNetEntityId(AdmissionHarness.MainRoom, aliceEntity);
        Assert.Equal(
            EntityBindingPort.Tombstoned,
            Assert.IsType<BindingResolveOutcome.Rejected>(staleA).Code);
        Assert.Equal(
            later.Binding,
            Assert.IsType<InputAdmissionOutcome.Accepted>(
                harness.Registry.TryAcceptInput(AdmissionHarness.MainRoom, "conn-2")).Binding);
        Assert.Equal(
            bob.Binding,
            Assert.IsType<InputAdmissionOutcome.Accepted>(
                harness.Registry.TryAcceptInput(AdmissionHarness.MainRoom, "conn-1")).Binding);
    }

    [Fact]
    public void ReconnectWithinWindowReusesEntityAndReplicationSnapshotOmitsStaleGeneration()
    {
        var harness = new AdmissionHarness();
        var first = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-1", "alice", false);
        Assert.Equal(1UL, first.Binding.ConnectionGeneration);

        Assert.True(harness.Registry.Disconnect(AdmissionHarness.MainRoom, "conn-1"));
        var scheduled = Assert.Single(harness.Timers.Scheduled);
        Assert.Equal(
            TimeSpan.FromSeconds(AdmissionReconnectDefaults.TestReconnectWindowSeconds).Ticks,
            scheduled.DueAt.Ticks - harness.Monotonic.Now.Ticks);
        Assert.IsType<ReconnectExpiryCommand>(scheduled.Command);

        harness.Monotonic.Advance(TimeSpan.FromSeconds(9));
        harness.FireDueReconnectTimers();

        var reconnected = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-2", "alice", false);

        Assert.Equal(first.Binding.NetEntityId, reconnected.Binding.NetEntityId);
        Assert.Equal(first.Binding.AccountId, reconnected.Binding.AccountId);
        Assert.Equal(2UL, reconnected.Binding.ConnectionGeneration);
        Assert.Null(reconnected.TerminationNotice);
        Assert.Equal("conn-1", reconnected.SupersededConnectionId);

        Assert.False(harness.Registry.TryGetBindingByConnection(AdmissionHarness.MainRoom, "conn-1", out _));
        var staleInput = harness.Registry.TryAcceptInput(AdmissionHarness.MainRoom, "conn-1");
        Assert.Equal(
            EntityBindingPort.BindingNotFound,
            Assert.IsType<InputAdmissionOutcome.Rejected>(staleInput).Code);

        var liveInput = harness.Registry.TryAcceptInput(AdmissionHarness.MainRoom, "conn-2");
        Assert.Equal(
            reconnected.Binding,
            Assert.IsType<InputAdmissionOutcome.Accepted>(liveInput).Binding);

        var stale = harness.Registry.ResolveByNetEntityId(
            AdmissionHarness.MainRoom,
            first.Binding.NetEntityId,
            connectionGeneration: 1);
        Assert.Equal(
            EntityBindingPort.StaleGeneration,
            Assert.IsType<BindingResolveOutcome.Rejected>(stale).Code);

        var snapshot = harness.Registry.CaptureReplicationFullSnapshot(AdmissionHarness.MainRoom);
        Assert.Equal(EntityBindingPort.ReplicationFullSnapshotKind, snapshot.Kind);
        Assert.DoesNotContain("persist", snapshot.Kind, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("restore", snapshot.Kind, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AdmissionHarness.MainRoom, snapshot.RoomId);
        var entity = Assert.Single(snapshot.Entities);
        Assert.Equal(first.Binding.NetEntityId, entity.NetEntityId);
        Assert.Equal(2UL, entity.ConnectionGeneration);
        Assert.DoesNotContain(snapshot.Entities, binding => binding.ConnectionGeneration == 1);
    }

    [Fact]
    public void ExpiryTombstonesEntityAndLaterLoginAllocatesANewNetEntityId()
    {
        var harness = new AdmissionHarness();
        var first = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-1", "alice", false);
        var retained = first.Binding.NetEntityId;

        Assert.True(harness.Registry.Disconnect(AdmissionHarness.MainRoom, "conn-1"));
        harness.Monotonic.Advance(TimeSpan.FromSeconds(AdmissionReconnectDefaults.TestReconnectWindowSeconds));
        harness.FireDueReconnectTimers();

        Assert.True(harness.Registry.TryGetPresence(AdmissionHarness.MainRoom, retained, out var presence));
        Assert.Equal(BindingPresence.Tombstoned, presence);

        var resolved = harness.Registry.ResolveByNetEntityId(AdmissionHarness.MainRoom, retained);
        Assert.Equal(
            EntityBindingPort.Tombstoned,
            Assert.IsType<BindingResolveOutcome.Rejected>(resolved).Code);

        Assert.Empty(harness.Registry.ListBindings(AdmissionHarness.MainRoom));
        Assert.Equal(0, harness.Registry.CountEntities(AdmissionHarness.MainRoom, BoundEntityKind.Player));
        Assert.False(harness.Registry.TryGetBindingByAccount(harness.AccountIdFor("alice"), out _));

        var second = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-2", "alice", false);
        Assert.NotEqual(retained, second.Binding.NetEntityId);
        Assert.Equal(first.Binding.AccountId, second.Binding.AccountId);
        Assert.Equal(1UL, second.Binding.ConnectionGeneration);

        var staleA = harness.Registry.ResolveByNetEntityId(AdmissionHarness.MainRoom, retained);
        Assert.Equal(
            EntityBindingPort.Tombstoned,
            Assert.IsType<BindingResolveOutcome.Rejected>(staleA).Code);

        var foundB = Assert.IsType<BindingResolveOutcome.Found>(
            harness.Registry.ResolveByNetEntityId(AdmissionHarness.MainRoom, second.Binding.NetEntityId));
        Assert.Equal(second.Binding, foundB.Binding);
        Assert.NotEqual(retained, foundB.Binding.NetEntityId);

        var snapshot = harness.Registry.CaptureReplicationFullSnapshot(AdmissionHarness.MainRoom);
        var live = Assert.Single(snapshot.Entities);
        Assert.Equal(second.Binding.NetEntityId, live.NetEntityId);
        Assert.DoesNotContain(snapshot.Entities, binding => binding.NetEntityId == retained);
    }

    [Fact]
    public void ProcessRestartDoesNotPreserveTheReconnectWindow()
    {
        var firstProcess = new AdmissionHarness();
        var first = firstProcess.AdmitAccepted(AdmissionHarness.MainRoom, "conn-1", "alice", false);
        Assert.True(firstProcess.Registry.Disconnect(AdmissionHarness.MainRoom, "conn-1"));

        var restarted = new AdmissionHarness();
        var second = restarted.AdmitAccepted(AdmissionHarness.MainRoom, "conn-2", "alice", false);

        Assert.NotEqual(first.Binding.NetEntityId, second.Binding.NetEntityId);
        Assert.Equal(1UL, second.Binding.ConnectionGeneration);
        Assert.Null(second.TerminationNotice);
        var missing = restarted.Registry.ResolveByNetEntityId(
            AdmissionHarness.MainRoom,
            first.Binding.NetEntityId);
        Assert.Equal(
            EntityBindingPort.NonExistent,
            Assert.IsType<BindingResolveOutcome.Rejected>(missing).Code);
        Assert.False(restarted.Registry.TryGetPresence(
            AdmissionHarness.MainRoom,
            first.Binding.NetEntityId,
            out _));
        Assert.True(firstProcess.Registry.TryGetPresence(
            AdmissionHarness.MainRoom,
            first.Binding.NetEntityId,
            out var retainedInOldProcess));
        Assert.Equal(BindingPresence.Disconnected, retainedInOldProcess);
    }

    [Fact]
    public void WallClockAdvanceDoesNotExpireTheHostMonotonicWindow()
    {
        var harness = new AdmissionHarness();
        var first = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-1", "alice", false);
        Assert.True(harness.Registry.Disconnect(AdmissionHarness.MainRoom, "conn-1"));

        harness.Clock.UnixSeconds += 10_000;
        harness.FireDueReconnectTimers();

        Assert.True(harness.Registry.TryGetPresence(
            AdmissionHarness.MainRoom,
            first.Binding.NetEntityId,
            out var presence));
        Assert.Equal(BindingPresence.Disconnected, presence);

        var reconnected = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-2", "alice", false);
        Assert.Equal(first.Binding.NetEntityId, reconnected.Binding.NetEntityId);
    }

    [Fact]
    public void ProductionFiveMinuteDeadlineIsScheduledOnTheHostTimer()
    {
        var harness = new AdmissionHarness(AdmissionReconnectDefaults.ReconnectWindowSeconds);
        harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-1", "alice", false);
        var started = harness.Monotonic.Now;
        Assert.True(harness.Registry.Disconnect(AdmissionHarness.MainRoom, "conn-1"));

        var scheduled = Assert.Single(harness.Timers.Scheduled);
        Assert.Equal(TimeSpan.FromMinutes(5).Ticks, scheduled.DueAt.Ticks - started.Ticks);

        harness.Monotonic.Advance(TimeSpan.FromSeconds(AdmissionReconnectDefaults.TestReconnectWindowSeconds));
        harness.FireDueReconnectTimers();
        Assert.True(harness.Registry.TryGetPresence(
            AdmissionHarness.MainRoom,
            harness.Registry.ListBindings(AdmissionHarness.MainRoom).Single().NetEntityId,
            out var presence));
        Assert.Equal(BindingPresence.Disconnected, presence);
    }

    [Fact]
    public void StaleExpiryCommandCannotTombstoneAReconnectedEntity()
    {
        var harness = new AdmissionHarness();
        var first = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-1", "alice", false);
        Assert.True(harness.Registry.Disconnect(AdmissionHarness.MainRoom, "conn-1"));
        var stale = Assert.IsType<ReconnectExpiryCommand>(Assert.Single(harness.Timers.Scheduled).Command);

        var reconnected = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-2", "alice", false);
        Assert.Equal(first.Binding.NetEntityId, reconnected.Binding.NetEntityId);
        Assert.Empty(harness.Timers.Scheduled);

        harness.ExpiryInbox.TryEnqueue(in stale);
        Assert.True(harness.Registry.TryGetPresence(
            AdmissionHarness.MainRoom,
            first.Binding.NetEntityId,
            out var presence));
        Assert.Equal(BindingPresence.Active, presence);
        Assert.Equal(
            reconnected.Binding,
            Assert.IsType<InputAdmissionOutcome.Accepted>(
                harness.Registry.TryAcceptInput(AdmissionHarness.MainRoom, "conn-2")).Binding);
    }

    [Fact]
    public void AssemblyHasNoResumeTokenPath()
    {
        var types = typeof(RoomAdmissionRegistry).Assembly.GetTypes();
        Assert.DoesNotContain(
            types,
            type => type.Name.Contains("ResumeToken", StringComparison.Ordinal)
                || type.Name.Contains("SessionResume", StringComparison.Ordinal)
                || type.Name.Contains("ReattachSession", StringComparison.Ordinal));
    }
}
