using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ArchUnitNET.Domain.Extensions;
using ArchUnitNET.Loader;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.TestKit;
using Lumio.Server.MvpHost.WorldSlot;
using Xunit;

namespace Lumio.Server.MvpHost.WorldSlot.Tests;

public sealed class WorldSlotFocusedTests
{
    private static readonly ArchUnitNET.Domain.Architecture WorldSlotArchitecture = new ArchLoader()
        .LoadAssemblies(typeof(WorldSlotHost).Assembly)
        .Build();

    private static readonly string[] DeferredTransitionEvents =
    {
        "Resume",
        "BeginSnapshot",
        "SnapshotComplete",
        "BeginReload",
        "ReloadComplete",
        "BeginMigrate",
        "MigrationHandedOff",
    };

    [Fact]
    public void AdmissionAndPacingAdaptersDoNotExpandThePublicHostShape()
    {
        var publicMethods = typeof(WorldSlotHost)
            .GetMethods(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain("ReserveAdmission", publicMethods);
        Assert.DoesNotContain("AbortAdmission", publicMethods);
        Assert.DoesNotContain("EnqueueTickPermit", publicMethods);
    }

    private static readonly string[] QuiesceAckNames =
        { "AdmissionClosed", "Drained", "SnapshotCut", "Stopped" };

    [Fact]
    public void OwnerThreadWaitsForAWorkSignalInsteadOfPollingEveryMillisecond()
    {
        var mvpHostDirectory = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(WorldSlotHost).Assembly.Location)!,
            "..",
            "..",
            "..",
            "..",
            ".."));
        var source = File.ReadAllText(Path.Combine(
            mvpHostDirectory,
            "src",
            "Lumio.Server.MvpHost.WorldSlot",
            "WorldSlotHost.cs"));

        Assert.DoesNotContain("WaitOne(1)", source, StringComparison.Ordinal);
        Assert.Contains("AutoResetEvent", source, StringComparison.Ordinal);
    }

    private static readonly string[] AdmissionClosedOnly = { "AdmissionClosed" };

    private static readonly WorldSlotHostState[] NonSnapshotStates =
        { WorldSlotHostState.Snapshotting };

    [Fact]
    public void StateMachineMatchesPublishedFixture()
    {
        var fixture = JsonNode.Parse(
            ContractMirrorFixtures.Load("fixtures/valid/state-machine-world-slot-host.json").Span)!;
        var names = fixture["states"]!.AsArray().Select(node => node!.GetValue<string>());

        Assert.Equal(names, Enum.GetNames<WorldSlotHostState>());
        Assert.Equal("Allocated", fixture["initialState"]!.GetValue<string>());

        var expected = fixture["transitions"]!.AsArray()
            .Select(node =>
            {
                var item = node!.AsObject();
                return (
                    From: Enum.Parse<WorldSlotHostState>(item["from"]!.GetValue<string>()),
                    To: Enum.Parse<WorldSlotHostState>(item["to"]!.GetValue<string>()),
                    Event: item["event"]!.GetValue<string>());
            })
            .ToArray();

        Assert.Equal(
            expected,
            WorldSlotStateMachine.ForwardTransitions
                .Select(transition => (transition.From, transition.To, transition.Event)));

        var generated = Lumio.Gen.ContractTypes.StateTransitionTable.All
            .Where(transition => transition.Machine == "WorldSlotHost")
            .Select(transition => (
                From: Enum.Parse<WorldSlotHostState>(transition.From),
                To: Enum.Parse<WorldSlotHostState>(transition.To),
                Event: transition.Event));
        Assert.Equal(
            generated,
            WorldSlotStateMachine.ForwardTransitions
                .Select(transition => (transition.From, transition.To, transition.Event)));
    }

    [Fact]
    public void StateMachineKeepsForwardAndFailStopRulesSeparate()
    {
        Assert.Equal(15, WorldSlotStateMachine.ForwardTransitions.Count);
        Assert.DoesNotContain(
            WorldSlotStateMachine.ForwardTransitions,
            transition => transition.To == WorldSlotHostState.Faulted);
        Assert.Equal(WorldSlotHostState.Faulted, WorldSlotStateMachine.AnyActiveToFaulted.Target);
        Assert.Equal(11, WorldSlotStateMachine.AnyActiveToFaulted.Expand().Count());
        Assert.False(WorldSlotStateMachine.AnyActiveToFaulted.AppliesTo(WorldSlotHostState.Faulted));
        Assert.False(WorldSlotStateMachine.AnyActiveToFaulted.AppliesTo(WorldSlotHostState.Destroyed));
    }

    [Fact]
    public void FaultAdjudicatorUsesExplicitWitnessOnly()
    {
        var adjudicator = new MvpFaultAdjudicator();

        Assert.Equal(
            new FaultAdjudication(HostFaultClass.SlotStateUnproven, true, false),
            adjudicator.Classify(null));
        Assert.Equal(
            new FaultAdjudication(HostFaultClass.SessionLocalProven, false, true),
            adjudicator.Classify(HostFaultClass.SessionLocalProven));
        Assert.Equal(
            new FaultAdjudication(HostFaultClass.SlotStateUnproven, true, false),
            adjudicator.Classify(HostFaultClass.SlotStateUnproven));
        Assert.Equal(
            new FaultAdjudication(HostFaultClass.ProcessFault, true, false),
            adjudicator.Classify(HostFaultClass.ProcessFault));
        Assert.Equal(
            new FaultAdjudication(HostFaultClass.None, false, false),
            adjudicator.Classify(HostFaultClass.None));
    }

    [Fact]
    public void NeverInfersFromCatchTest()
    {
        var classify = typeof(MvpFaultAdjudicator).GetMethod(
            nameof(MvpFaultAdjudicator.Classify),
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(classify);
        var parameters = classify.GetParameters();
        var parameter = Assert.Single(parameters);
        Assert.Equal(typeof(HostFaultClass?), parameter.ParameterType);
        Assert.Equal(typeof(FaultAdjudication), classify.ReturnType);

        var fields = typeof(MvpFaultAdjudicator).GetFields(
            BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly);
        Assert.Empty(fields);

        var adjudicator = WorldSlotArchitecture.Types.Single(type =>
            string.Equals(type.FullName, typeof(MvpFaultAdjudicator).FullName, StringComparison.Ordinal));
        var exceptionCalls = adjudicator.Members
            .SelectMany(member => member.GetMethodCallDependencies())
            .Where(dependency => IsExceptionName(dependency.Target.FullName)
                || IsExceptionName(dependency.TargetMember.FullName))
            .Select(dependency => dependency.TargetMember.FullName)
            .ToList();
        var exceptionTypes = adjudicator.Dependencies
            .Where(dependency => IsExceptionName(dependency.Target.FullName))
            .Select(dependency => dependency.Target.FullName)
            .ToList();

        Assert.Empty(exceptionCalls);
        Assert.Empty(exceptionTypes);
    }

    [Fact]
    public void UnattestedOutcomeIsUnprovenTest()
    {
        HostTickOutcome outcome = default;
        Assert.Null(outcome.FaultClass);

        var adjudicator = new MvpFaultAdjudicator();
        Assert.Equal(
            new FaultAdjudication(HostFaultClass.SlotStateUnproven, true, false),
            adjudicator.Classify(outcome.FaultClass));
    }

    [Fact]
    public void ConcurrentReservationsRespectQuota()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Allocate(new SlotBudget(2, 64, 65_536)).Allocated);

        var results = new AllocateResult[8];
        Parallel.For(0, results.Length, i =>
            results[i] = harness.Host.TryReserve(new AdmissionAttemptId((ulong)i + 1), new ServerSessionId($"s-{i}")));

        Assert.Equal(2, results.Count(r => r.Allocated));
        Assert.All(results.Where(r => !r.Allocated), r => Assert.Equal("CapacityExceeded", r.StableErrorId));
        Assert.Equal(2, harness.Host.Capacity.BoundSessions);
    }

    [Fact]
    public void ReservationCommitsOnceAndRetriesIdempotently()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Allocate(new SlotBudget(1, 64, 65_536)).Allocated);

        var allocation = harness.Host.TryReserve(
            new AdmissionAttemptId(1),
            new ServerSessionId("session-1"));
        Assert.True(allocation.Allocated);
        var reservation = harness.Host.LastReservation;

        var bind = harness.Host.BindSession(
            reservation,
            new ServerSessionId("session-1"),
            harness.Host.Epoch);
        Assert.True(bind.Accepted);
        Assert.True(harness.Host.BindSession(
            reservation,
            new ServerSessionId("session-1"),
            harness.Host.Epoch).Accepted);
        Assert.Equal(1, harness.Host.Capacity.BoundSessions);
    }

    [Fact]
    public void CommittedReservationCanBeReleasedIdempotently()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Allocate(new SlotBudget(1, 64, 65_536)).Allocated);

        var session = new ServerSessionId("session-release");
        var reservation = harness.Host.TryReserve(new AdmissionAttemptId(1), session);
        Assert.True(reservation.Allocated);
        var handle = harness.Host.LastReservation;
        Assert.True(harness.Host.BindSession(handle, session, harness.Host.Epoch).Accepted);
        Assert.Equal(1, harness.Host.Capacity.BoundSessions);

        var publicAbort = harness.Host.AbortAdmission(handle, harness.Host.Epoch);
        Assert.False(publicAbort.Accepted);
        Assert.Equal("InvalidArgument", publicAbort.StableErrorId);
        var mismatchedRelease = harness.Host.ReleaseCommittedReservation(
            handle,
            new ServerSessionId("another-session"),
            harness.Host.Epoch);
        Assert.False(mismatchedRelease.Accepted);
        Assert.Equal(1, harness.Host.Capacity.BoundSessions);

        Assert.True(harness.Host.ReleaseCommittedReservation(handle, session, harness.Host.Epoch).Accepted);
        Assert.True(harness.Host.ReleaseCommittedReservation(handle, session, harness.Host.Epoch).Accepted);
        Assert.Equal(0, harness.Host.Capacity.BoundSessions);

        Assert.True(harness.Host.TryReserve(
            new AdmissionAttemptId(2),
            new ServerSessionId("session-after-release")).Allocated);
    }

    [Fact]
    public void ReservationAttemptRetryReturnsTheOriginalReservation()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Allocate(new SlotBudget(1, 64, 65_536)).Allocated);

        var first = harness.Host.TryReserve(
            new AdmissionAttemptId(7),
            new ServerSessionId("session-7"));
        var firstReservation = harness.Host.LastReservation;

        var retry = harness.Host.TryReserve(
            new AdmissionAttemptId(7),
            new ServerSessionId("session-7"));

        Assert.True(first.Allocated);
        Assert.True(retry.Allocated);
        Assert.Equal(firstReservation, harness.Host.LastReservation);
        Assert.Equal(1, harness.Host.Capacity.BoundSessions);
    }

    [Fact]
    public void InternalAdmissionPathReturnsPerCallReservationAndSlotIdentity()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Allocate(new SlotBudget(4, 64, 65_536)).Allocated);
        var port = harness.Host;
        var results = new AdmissionReservationResult[4];

        Parallel.For(0, results.Length, i =>
            results[i] = port.ReserveAdmission(
                new AdmissionAttemptId((ulong)i + 1),
                new ServerSessionId($"public-{i}")));

        Assert.All(results, result =>
        {
            Assert.True(result.Reserved);
            Assert.NotEqual(0UL, result.Reservation.Value);
            Assert.NotEqual(0UL, result.SlotId.Value);
            Assert.Equal(harness.Host.SlotId, result.SlotId);
            Assert.Equal(harness.Host.Epoch, result.Epoch);
        });
        Assert.Equal(results.Length, results.Select(result => result.Reservation.Value).Distinct().Count());

        for (var i = 0; i < results.Length; i++)
        {
            Assert.True(port.BindSession(
                results[i].Reservation,
                new ServerSessionId($"public-{i}"),
                results[i].Epoch).Accepted);
        }

        Assert.Equal(results.Length, harness.Host.Capacity.BoundSessions);
    }

    [Fact]
    public void StaleEpochIsRejected()
    {
        using var harness = new Harness();
        var allocated = harness.Host.Allocate(new SlotBudget(1, 64, 65_536));
        Assert.True(allocated.Allocated);

        var result = harness.Host.BindSession(new SlotReservationId(1), new ServerSessionId("s"), new SlotEpoch(allocated.Epoch.Value - 1));

        Assert.False(result.Accepted);
        Assert.Equal("StaleEpoch", result.StableErrorId);
    }

    [Fact]
    public void StaleCommandsAreRejectedBeforeQueueMutation()
    {
        using var harness = new Harness();
        var allocated = harness.Host.Allocate(new SlotBudget(1, 64, 65_536));
        Assert.True(allocated.Allocated);

        var result = harness.Host.TryEnqueue(
            new WorldSlotCommand.TickPermit(
                new LogicalTickToken(1),
                new SlotEpoch(allocated.Epoch.Value + 1)));

        Assert.Equal(EnqueueStatus.Closed, result.Status);
        Assert.Equal("StaleEpoch", result.StableErrorId);
        Assert.Equal(0, harness.Host.TickPermitCount);
    }

    [Fact]
    public void GateChangesPublishTypedEvents()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Allocate(new SlotBudget(1, 64, 65_536)).Allocated);

        Assert.True(harness.Host.SetGate(AdmissionGateState.Closed, harness.Host.Epoch).Accepted);
        Assert.True(harness.Host.SetGate(AdmissionGateState.Open, harness.Host.Epoch).Accepted);

        var events = new List<WorldSlotEvent>();
        while (harness.EventInbox.TryDequeue(out var evt))
        {
            events.Add(evt);
        }

        Assert.Equal(
            new[]
            {
                AdmissionGateState.Closed,
                AdmissionGateState.Open,
            },
            events.OfType<WorldSlotEvent.GateStateChanged>().Select(evt => evt.State));
    }

    [Fact]
    public void ReservedEventTailCannotBeBypassedByLaterPrimaryEvents()
    {
        using var harness = new Harness(eventCapacity: 1);
        Assert.True(harness.Host.Allocate(new SlotBudget(1, 64, 65_536)).Allocated);

        var occupied = harness.Host.ReserveAdmission(
            new AdmissionAttemptId(99),
            new ServerSessionId("occupied"));
        Assert.True(occupied.Reserved);
        Assert.True(harness.EventInbox.TryDequeue(out _));

        WorldSlotEvent filler = new WorldSlotEvent.TickCompleted(
            new LogicalTickToken(1),
            0,
            new SlotEpoch(0));
        Assert.Equal(EnqueueStatus.Accepted, harness.EventInbox.TryEnqueue(in filler).Status);

        var first = harness.Host.ReserveAdmission(
            new AdmissionAttemptId(1),
            new ServerSessionId("rejected"));
        Assert.False(first.Reserved);
        Assert.Equal("CapacityExceeded", first.StableErrorId);

        Assert.True(harness.EventInbox.TryDequeue(out _));
        Assert.True(harness.Host.AbortAdmission(occupied.Reservation, occupied.Epoch).Accepted);

        var second = harness.Host.ReserveAdmission(
            new AdmissionAttemptId(2),
            new ServerSessionId("after-allocation"));
        Assert.True(second.Reserved);

        var observed = new List<WorldSlotEvent>();
        while (harness.Host.TryDequeueEvent(out var evt))
        {
            observed.Add(evt);
        }

        var rejectedIndex = observed.FindIndex(evt => evt is WorldSlotEvent.AdmissionRejected rejected
            && rejected.Attempt == new AdmissionAttemptId(1));
        var reservedIndex = observed.FindIndex(evt => evt is WorldSlotEvent.AdmissionReserved reserved
            && reserved.Attempt == new AdmissionAttemptId(2));
        Assert.True(rejectedIndex >= 0);
        Assert.True(reservedIndex > rejectedIndex);
    }

    [Fact]
    public void FaultTerminalEventsKeepReservedSlotsAfterNonTerminalTailSaturation()
    {
        using var harness = new Harness(eventCapacity: 1);
        Assert.True(harness.Host.Allocate(new SlotBudget(1, 64, 65_536)).Allocated);

        var occupied = harness.Host.ReserveAdmission(
            new AdmissionAttemptId(99),
            new ServerSessionId("occupied"));
        Assert.True(occupied.Reserved);
        Assert.True(harness.EventInbox.TryDequeue(out _));

        WorldSlotEvent filler = new WorldSlotEvent.TickCompleted(
            new LogicalTickToken(1),
            0,
            new SlotEpoch(0));
        Assert.Equal(EnqueueStatus.Accepted, harness.EventInbox.TryEnqueue(in filler).Status);

        var rejected = harness.Host.ReserveAdmission(
            new AdmissionAttemptId(1),
            new ServerSessionId("rejected"));
        Assert.False(rejected.Reserved);
        Assert.Equal("CapacityExceeded", rejected.StableErrorId);
        Assert.True(harness.EventInbox.TryDequeue(out _));

        Assert.True(harness.Host.AbortAdmission(occupied.Reservation, occupied.Epoch).Accepted);

        // Fill the tail up to the non-critical limit with real gate events. The
        // two remaining slots must stay available for FaultAdjudicated and
        // ReadyToStop.
        for (var i = 0; i < WorldSlotProvisionalDefaults.SlotEventOutboxMaxItems - 1; i++)
        {
            var nextGate = i % 2 == 0
                ? AdmissionGateState.Closed
                : AdmissionGateState.Open;
            Assert.True(harness.Host.SetGate(nextGate, harness.Host.Epoch).Accepted);
        }

        var saturated = harness.Host.SetGate(AdmissionGateState.Open, harness.Host.Epoch);
        Assert.False(saturated.Accepted);
        Assert.Equal("QueueFull", saturated.StableErrorId);

        var fault = harness.Host.ReportFault(
            "InternalInvariant",
            HostFaultClass.ProcessFault,
            harness.Host.Epoch);
        Assert.True(fault.Accepted);
        Assert.Equal(WorldSlotHostState.Faulted, harness.Host.State);

        var observed = new List<WorldSlotEvent>();
        while (harness.Host.TryDequeueEvent(out var evt))
        {
            observed.Add(evt);
        }
        Assert.Contains(observed, evt => evt is WorldSlotEvent.FaultAdjudicated);
        Assert.Contains(observed, evt => evt is WorldSlotEvent.ReadyToStop);
        var faultIndex = observed.FindIndex(evt => evt is WorldSlotEvent.FaultAdjudicated);
        var stopIndex = observed.FindIndex(evt => evt is WorldSlotEvent.ReadyToStop);
        Assert.True(stopIndex > faultIndex);
    }

    [Fact]
    public void ClosedGateRejectsCommitAsWellAsReservation()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Allocate(new SlotBudget(1, 64, 65_536)).Allocated);
        var reservation = harness.Host.TryReserve(
            new AdmissionAttemptId(1),
            new ServerSessionId("session-1"));
        Assert.True(reservation.Allocated);

        Assert.True(harness.Host.SetGate(AdmissionGateState.Closed, harness.Host.Epoch).Accepted);
        var result = harness.Host.BindSession(
            harness.Host.LastReservation,
            new ServerSessionId("session-1"),
            harness.Host.Epoch);

        Assert.False(result.Accepted);
        Assert.Equal("ContextClosing", result.StableErrorId);
        // Closing the gate rejects the late commit but does not silently release
        // an in-flight reservation; the saga must explicitly abort it.
        Assert.Equal(1, harness.Host.Capacity.BoundSessions);
        Assert.False(harness.Host.TryReserve(
                new AdmissionAttemptId(2),
                new ServerSessionId("session-2")).Allocated);
        Assert.True(harness.Host.AbortAdmission(
            harness.Host.LastReservation,
            harness.Host.Epoch).Accepted);
        Assert.True(harness.Host.AbortAdmission(
            harness.Host.LastReservation,
            harness.Host.Epoch).Accepted);
        Assert.Equal(0, harness.Host.Capacity.BoundSessions);
    }

    [Fact]
    public void MismatchedCommitDoesNotConsumeAnotherSessionsReservation()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Allocate(new SlotBudget(2, 64, 65_536)).Allocated);

        var reservation = harness.Host.TryReserve(
            new AdmissionAttemptId(1),
            new ServerSessionId("session-a"));
        Assert.True(reservation.Allocated);

        var mismatched = harness.Host.BindSession(
            harness.Host.LastReservation,
            new ServerSessionId("session-b"),
            harness.Host.Epoch);

        Assert.False(mismatched.Accepted);
        Assert.Equal("InvalidArgument", mismatched.StableErrorId);
        Assert.Equal(1, harness.Host.Capacity.BoundSessions);
        Assert.True(harness.Host.BindSession(
            harness.Host.LastReservation,
            new ServerSessionId("session-a"),
            harness.Host.Epoch).Accepted);
        Assert.Equal(1, harness.Host.Capacity.BoundSessions);
    }

    [Fact]
    public void DestroyClosesGateThroughTypedEvent()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Allocate(new SlotBudget(1, 64, 65_536)).Allocated);
        Assert.True(harness.Host.Advance(WorldSlotHostState.Bootstrapping));
        Assert.True(harness.Host.Advance(WorldSlotHostState.NativeReady));
        Assert.True(harness.Host.Advance(WorldSlotHostState.ManagedReady));
        Assert.True(harness.Host.Advance(WorldSlotHostState.LoadingSession));
        Assert.True(harness.Host.Advance(WorldSlotHostState.Running));
        Assert.True(harness.Host.Advance(WorldSlotHostState.Quiescing));
        Assert.True(harness.Host.Advance(WorldSlotHostState.Stopping));

        Assert.True(harness.Host.Destroy(harness.Host.Epoch).Accepted);
        Assert.Equal(AdmissionGateState.Closed, harness.Host.Gate);
        Assert.Contains(
            DrainEvents(harness),
            evt => evt is WorldSlotEvent.GateStateChanged { State: AdmissionGateState.Closed });
    }

    [Fact]
    public void LifecyclePathReachesDestroyed()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Allocate(new SlotBudget(1, 64, 65_536)).Allocated);

        Assert.True(harness.Host.Advance(WorldSlotHostState.Bootstrapping));
        Assert.True(harness.Host.Advance(WorldSlotHostState.NativeReady));
        Assert.True(harness.Host.Advance(WorldSlotHostState.ManagedReady));
        Assert.True(harness.Host.Advance(WorldSlotHostState.LoadingSession));
        Assert.True(harness.Host.Advance(WorldSlotHostState.Running));
        Assert.True(harness.Host.Quiesce("test", harness.Host.Epoch).Accepted);
        Assert.Equal(WorldSlotHostState.Stopping, harness.Host.State);
        Assert.True(harness.Host.Destroy(harness.Host.Epoch).Accepted);
        Assert.Equal(WorldSlotHostState.Destroyed, harness.Host.State);
    }

    [Fact]
    public void TickRunsOnNamedOwnerThread()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Allocate(new SlotBudget(1, 64, 65_536)).Allocated);
        harness.Host.Advance(WorldSlotHostState.Bootstrapping);
        harness.Host.Advance(WorldSlotHostState.NativeReady);
        harness.Host.Advance(WorldSlotHostState.ManagedReady);
        harness.Host.Advance(WorldSlotHostState.LoadingSession);
        harness.Host.Advance(WorldSlotHostState.Running);

        harness.Host.EnqueueTick(new LogicalTickToken(1), harness.Host.Epoch);
        Assert.True(SpinUntil(() => harness.Simulation.LastThreadName is not null));
        Assert.Equal($"worldslot-{harness.Host.SlotId.Value}", harness.Simulation.LastThreadName);
    }

    [Fact]
    public void StartRunningInitializesAndReadiesSimulationOnOwnerThread()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Allocate(new SlotBudget(1, 64, 65_536)).Allocated);

        var init = new HostSessionInit(
            new HostSessionId("lifecycle-session"),
            new HostWorldSlotId(harness.Host.SlotId.Value),
            ReadOnlyMemory<byte>.Empty,
            17);
        var result = harness.Host.StartRunning(in init);

        Assert.True(result.Accepted);
        Assert.Equal(WorldSlotHostState.Running, harness.Host.State);
        Assert.Equal(HostSimulationState.Ready, harness.Simulation.State);
        Assert.Equal($"worldslot-{harness.Host.SlotId.Value}", harness.Simulation.InitializeThreadName);
        Assert.Equal($"worldslot-{harness.Host.SlotId.Value}", harness.Simulation.ReadyThreadName);
    }

    [Fact]
    public void NativeReadyIsTraversedNotSkippedTest()
    {
        using var harness = new Harness();
        harness.StartRunning();

        Assert.Equal(WorldSlotHostState.Running, harness.Host.State);
        Assert.Contains(
            harness.Trace.States,
            state => state.SessionState == nameof(WorldSlotHostState.NativeReady));
        Assert.NotEqual(WorldSlotHostState.NativeReady, WorldSlotHostState.ManagedReady);
        Assert.NotEqual(WorldSlotHostState.NativeReady, WorldSlotHostState.Bootstrapping);
        Assert.Contains(
            WorldSlotStateMachine.ForwardTransitions,
            transition => transition.From == WorldSlotHostState.Bootstrapping
                && transition.To == WorldSlotHostState.NativeReady
                && transition.Event == "NativeLoaded");
        Assert.Contains(
            WorldSlotStateMachine.ForwardTransitions,
            transition => transition.From == WorldSlotHostState.NativeReady
                && transition.To == WorldSlotHostState.ManagedReady
                && transition.Event == "ManagedLoaded");

        var source = ReadWorldSlotHostSource();
        var startRunningAt = source.IndexOf("ApplyStartRunning", StringComparison.Ordinal);
        Assert.True(startRunningAt >= 0);
        Assert.True(
            source.IndexOf("ABS-WORLDSLOT-NATIVE", startRunningAt, StringComparison.Ordinal) >= 0,
            "ABS-WORLDSLOT-NATIVE must sit on the production StartRunning path, not only Advance.");

        var startRunning = WorldSlotArchitecture.Types
            .Single(type => string.Equals(type.FullName, typeof(WorldSlotHost).FullName, StringComparison.Ordinal))
            .Members
            .Single(member => member.Name.Contains("ApplyStartRunning", StringComparison.Ordinal));
        var called = startRunning.GetMethodCallDependencies()
            .Select(dependency => dependency.TargetMember.Name)
            .ToList();
        Assert.Contains(
            called,
            name => name.Contains("LoadAbsentNativeRuntime", StringComparison.Ordinal));
    }

    [Fact]
    public void DeferredTransitionsAreDefinedButUnreachableTest()
    {
        foreach (var name in DeferredTransitionEvents)
        {
            Assert.Contains(
                WorldSlotStateMachine.ForwardTransitions,
                transition => transition.Event == name);
        }

        Assert.Equal(
            DeferredTransitionEvents.Length,
            WorldSlotStateMachine.ForwardTransitions.Count(transition =>
                DeferredTransitionEvents.Contains(transition.Event, StringComparer.Ordinal)));

        var commandNames = typeof(WorldSlotCommand)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Select(type => type.Name)
            .ToArray();
        Assert.DoesNotContain(
            commandNames,
            name => DeferredTransitionEvents.Contains(name, StringComparer.Ordinal));

        var reachable = typeof(WorldSlotHost).GetMethod(
            "IsMvpReachableEvent",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(reachable);
        foreach (var name in DeferredTransitionEvents)
        {
            Assert.False((bool)reachable.Invoke(null, new object[] { name })!);
        }

        var hostSource = ReadWorldSlotHostSource();
        foreach (var name in DeferredTransitionEvents)
        {
            Assert.DoesNotContain($"\"{name}\"", hostSource, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("this.state = WorldSlotHostState.Snapshotting", hostSource, StringComparison.Ordinal);
        Assert.DoesNotContain("this.state = WorldSlotHostState.Reloading", hostSource, StringComparison.Ordinal);
        Assert.DoesNotContain("this.state = WorldSlotHostState.Migrating", hostSource, StringComparison.Ordinal);

        using var harness = new Harness();
        harness.StartRunning();
        Assert.True(harness.Host.Quiesce("shutdown", harness.Host.Epoch).Accepted);
        Assert.Equal(WorldSlotHostState.Stopping, harness.Host.State);
        Assert.NotEqual(WorldSlotHostState.Snapshotting, harness.Host.State);
        Assert.NotEqual(WorldSlotHostState.Reloading, harness.Host.State);
        Assert.NotEqual(WorldSlotHostState.Migrating, harness.Host.State);
    }

    [Fact]
    public void InternalPacingPathUsesBoundedQueueAndEpochFence()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Allocate(new SlotBudget(1, 64, 65_536)).Allocated);
        Assert.True(harness.Host.StartRunning().Accepted);
        var port = harness.Host;
        harness.Simulation.BlockTicks = true;

        Assert.Equal(
            EnqueueStatus.Accepted,
            port.EnqueueTickPermit(new LogicalTickToken(1), harness.Host.Epoch).Status);
        Assert.True(harness.Simulation.TickStarted.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));
        Assert.Equal(
            EnqueueStatus.Accepted,
            port.EnqueueTickPermit(new LogicalTickToken(2), harness.Host.Epoch).Status);
        var full = port.EnqueueTickPermit(new LogicalTickToken(3), harness.Host.Epoch);
        Assert.Equal(EnqueueStatus.Full, full.Status);
        Assert.Equal("QueueFull", full.StableErrorId);

        var stale = port.EnqueueTickPermit(
            new LogicalTickToken(4),
            new SlotEpoch(harness.Host.Epoch.Value + 1));
        Assert.Equal(EnqueueStatus.Closed, stale.Status);
        Assert.Equal("StaleEpoch", stale.StableErrorId);
        harness.Simulation.ReleaseTicks();
    }

    [Fact]
    public void FaultedStateIsFailStop()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Allocate(new SlotBudget(1, 64, 65_536)).Allocated);

        var fault = harness.Host.ReportFault("InternalInvariant", HostFaultClass.SlotStateUnproven, harness.Host.Epoch);

        Assert.True(fault.Accepted);
        Assert.Equal(WorldSlotHostState.Faulted, harness.Host.State);
        Assert.False(harness.Host.Advance(WorldSlotHostState.Running));
        Assert.False(harness.Host.Destroy(harness.Host.Epoch).Accepted);
    }

    [Fact]
    public void QuiesceEmitsOrderedEpochAcksAndClosesGateFirst()
    {
        using var harness = new Harness();
        harness.StartRunning();

        var epoch = harness.Host.Epoch;
        var result = harness.Host.Quiesce("shutdown", epoch);

        Assert.True(result.Accepted);
        Assert.Equal(AdmissionGateState.Closed, harness.Host.Gate);
        Assert.Equal(WorldSlotHostState.Stopping, harness.Host.State);
        Assert.Equal(
            QuiesceAckNames,
            harness.Trace.Acks.Select(a => a.Effect));
        Assert.All(harness.Trace.Acks, ack => Assert.Equal(epoch.Value, ack.SlotEpoch));
        Assert.NotEqual(0UL, harness.Host.SnapshotCut.Value);
        Assert.DoesNotContain(
            harness.Host.State,
            NonSnapshotStates);
    }

    [Fact]
    public void QuiesceFailureFailsClosedWithoutHalfCompleteState()
    {
        using var harness = new Harness();
        harness.StartRunning();
        harness.Simulation.FailDrain = true;

        var result = harness.Host.Quiesce("shutdown", harness.Host.Epoch);

        Assert.False(result.Accepted);
        Assert.Equal("InternalInvariant", result.StableErrorId);
        Assert.Equal(WorldSlotHostState.Faulted, harness.Host.State);
        Assert.Equal(AdmissionGateState.Closed, harness.Host.Gate);
        Assert.True(harness.Host.IsPacingStopped);
        Assert.Equal(AdmissionClosedOnly, harness.Trace.Acks.Select(a => a.Effect));
    }

    [Theory]
    [InlineData(QuiesceFailureStep.CloseGate)]
    [InlineData(QuiesceFailureStep.Drain)]
    [InlineData(QuiesceFailureStep.SnapshotCut)]
    [InlineData(QuiesceFailureStep.PausePacing)]
    [InlineData(QuiesceFailureStep.Stop)]
    public void QuiesceStepFailureEntersFaultedTest(QuiesceFailureStep step)
    {
        using var harness = new Harness();
        harness.StartRunning();
        harness.ArmQuiesceFailure(step);

        var result = harness.Host.Quiesce("shutdown", harness.Host.Epoch);

        Assert.False(result.Accepted);
        Assert.Equal("InternalInvariant", result.StableErrorId);
        Assert.Equal(WorldSlotHostState.Faulted, harness.Host.State);
        Assert.Equal(AdmissionGateState.Closed, harness.Host.Gate);
        Assert.True(harness.Host.IsPacingStopped);
        Assert.DoesNotContain(harness.Host.State, NonSnapshotStates);
        Assert.Equal(
            AcksBeforeFailedStep(step),
            harness.Trace.Acks.Select(ack => ack.Effect).ToArray());
    }

    [Fact]
    public void ReservedCommandsUseEmergencyLaneWhenInboxIsFull()
    {
        using var harness = new Harness(aggregateCapacity: 64);
        harness.StartRunning();
        harness.Simulation.BlockTicks = true;
        Assert.Equal(
            EnqueueStatus.Accepted,
            harness.Host.EnqueueTick(new LogicalTickToken(1), harness.Host.Epoch).Status);
        Assert.True(harness.Simulation.TickStarted.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));

        try
        {
            var attempt = 0UL;
            while (harness.AggregateInbox.Count < harness.AggregateInbox.Budget.MaxItems)
            {
                var result = harness.Host.TryEnqueue(
                    new WorldSlotCommand.DependencyAck(new AdmissionAttemptId(attempt++), true, null));
                Assert.Equal(EnqueueStatus.Accepted, result.Status);
            }

            Assert.Equal(harness.AggregateInbox.Budget.MaxItems, harness.AggregateInbox.Count);

            var abort = harness.Host.TryEnqueue(new WorldSlotCommand.AbortAdmission(
                new SlotReservationId(1),
                harness.Host.Epoch));
            var quiesce = harness.Host.TryEnqueue(
                new WorldSlotCommand.Quiesce("shutdown", harness.Host.Epoch));
            var stop = harness.Host.TryEnqueue(new WorldSlotCommand.Stop(harness.Host.Epoch));
            var ordinary = harness.Host.TryEnqueue(
                new WorldSlotCommand.DependencyAck(new AdmissionAttemptId(1000), true, null));

            Assert.Equal(EnqueueStatus.Full, abort.Status);
            Assert.Equal("QueueFull", abort.StableErrorId);
            Assert.Equal(EnqueueStatus.Accepted, quiesce.Status);
            Assert.Equal(EnqueueStatus.Accepted, stop.Status);
            Assert.Equal(EnqueueStatus.Full, ordinary.Status);
            Assert.Equal("QueueFull", ordinary.StableErrorId);
        }
        finally
        {
            harness.Simulation.ReleaseTicks();
        }
    }

    [Fact]
    public void ReservedCommandTailCannotBeBypassedWhenPrimaryCapacityReopens()
    {
        using var harness = new Harness(aggregateCapacity: 64);
        harness.StartRunning();
        harness.Simulation.BlockTicks = true;
        Assert.Equal(
            EnqueueStatus.Accepted,
            harness.Host.EnqueueTick(new LogicalTickToken(1), harness.Host.Epoch).Status);
        Assert.True(harness.Simulation.TickStarted.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));

        try
        {
            var attempt = 0UL;
            while (harness.AggregateInbox.Count < harness.AggregateInbox.Budget.MaxItems)
            {
                var result = harness.Host.TryEnqueue(
                    new WorldSlotCommand.DependencyAck(new AdmissionAttemptId(attempt++), true, null));
                Assert.Equal(EnqueueStatus.Accepted, result.Status);
            }

            var quiesce = harness.Host.TryEnqueue(
                new WorldSlotCommand.Quiesce("shutdown", harness.Host.Epoch));
            Assert.Equal(EnqueueStatus.Accepted, quiesce.Status);
            Assert.Equal(1, harness.Host.ReservedCommandCount);

            Assert.True(harness.AggregateInbox.TryDequeue(out _));
            var laterOrdinary = harness.Host.TryEnqueue(
                new WorldSlotCommand.DependencyAck(new AdmissionAttemptId(1000), true, null));
            var laterStop = harness.Host.TryEnqueue(
                new WorldSlotCommand.Stop(harness.Host.Epoch));

            Assert.Equal(EnqueueStatus.Full, laterOrdinary.Status);
            Assert.Equal("QueueFull", laterOrdinary.StableErrorId);
            Assert.Equal(EnqueueStatus.Accepted, laterStop.Status);
            Assert.Equal(2, harness.Host.ReservedCommandCount);
            Assert.Equal(harness.AggregateInbox.Budget.MaxItems - 1, harness.AggregateInbox.Count);
        }
        finally
        {
            harness.Simulation.ReleaseTicks();
        }
    }

    [Fact]
    public async Task TimedOutReservationCannotCreateAnOrphanAfterOwnerResumes()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Allocate(new SlotBudget(2, 64, 65_536)).Allocated);
        Assert.True(harness.Host.StartRunning().Accepted);
        harness.Simulation.BlockTicks = true;
        Assert.Equal(
            EnqueueStatus.Accepted,
            harness.Host.EnqueueTick(new LogicalTickToken(1), harness.Host.Epoch).Status);
        Assert.True(harness.Simulation.TickStarted.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));

        try
        {
            var pending = Task.Run(
                () => harness.Host.ReserveAdmission(
                    new AdmissionAttemptId(1),
                    new ServerSessionId("timed-out")),
                TestContext.Current.CancellationToken);
            var timedOut = await pending.WaitAsync(
                TimeSpan.FromSeconds(4),
                TestContext.Current.CancellationToken);
            Assert.False(timedOut.Reserved);
            Assert.Equal("TimedOut", timedOut.StableErrorId);
        }
        finally
        {
            harness.Simulation.ReleaseTicks();
        }

        var surviving = harness.Host.ReserveAdmission(
            new AdmissionAttemptId(2),
            new ServerSessionId("surviving"));
        Assert.True(surviving.Reserved);
        Assert.Equal(new SlotReservationId(1), surviving.Reservation);
        Assert.Equal(1, harness.Host.Capacity.BoundSessions);
    }

    [Fact]
    public async Task TimedOutCommitCannotAssociateTheSessionAfterOwnerResumes()
    {
        using var harness = new Harness();
        harness.StartRunning();
        var session = new ServerSessionId("timed-out-commit");
        var reservation = harness.Host.ReserveAdmission(new AdmissionAttemptId(1), session);
        Assert.True(reservation.Reserved);

        harness.Simulation.BlockTicks = true;
        Assert.Equal(
            EnqueueStatus.Accepted,
            harness.Host.EnqueueTick(new LogicalTickToken(1), harness.Host.Epoch).Status);
        Assert.True(harness.Simulation.TickStarted.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));

        try
        {
            var pending = Task.Run(
                () => harness.Host.BindSession(reservation.Reservation, session, reservation.Epoch),
                TestContext.Current.CancellationToken);
            var timedOut = await pending.WaitAsync(
                TimeSpan.FromSeconds(4),
                TestContext.Current.CancellationToken);
            Assert.False(timedOut.Accepted);
            Assert.Equal("TimedOut", timedOut.StableErrorId);
        }
        finally
        {
            harness.Simulation.ReleaseTicks();
        }

        Assert.True(harness.Host.AbortAdmission(reservation.Reservation, reservation.Epoch).Accepted);
        Assert.Equal(0, harness.Host.Capacity.BoundSessions);
    }

    [Fact]
    public void TickPermitQueueIsCapacityOneAndDoesNotCatchUp()
    {
        using var harness = new Harness();
        harness.StartRunning();

        Assert.Equal(1, harness.Host.TickPermitBudget.MaxItems);
        harness.Simulation.BlockTicks = true;
        var first = harness.Host.EnqueueTick(new LogicalTickToken(1), harness.Host.Epoch);
        Assert.Equal(EnqueueStatus.Accepted, first.Status);
        Assert.True(harness.Simulation.TickStarted.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));

        var second = harness.Host.EnqueueTick(new LogicalTickToken(2), harness.Host.Epoch);
        Assert.Equal(EnqueueStatus.Accepted, second.Status);

        var third = harness.Host.EnqueueTick(new LogicalTickToken(3), harness.Host.Epoch);
        Assert.Equal(EnqueueStatus.Full, third.Status);
        Assert.Equal("QueueFull", third.StableErrorId);
        Assert.Equal(1, harness.Host.TickOverruns);
        harness.Simulation.ReleaseTicks();
    }

    [Fact]
    public void InFlightTickCannotPublishAfterExternalFailStop()
    {
        using var harness = new Harness();
        harness.StartRunning();
        harness.Simulation.BlockTicks = true;
        Assert.Equal(
            EnqueueStatus.Accepted,
            harness.Host.EnqueueTick(new LogicalTickToken(1), harness.Host.Epoch).Status);
        Assert.True(harness.Simulation.TickStarted.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));

        try
        {
            var fault = harness.Host.ReportFault(
                "InternalInvariant",
                HostFaultClass.SlotStateUnproven,
                harness.Host.Epoch);
            Assert.True(fault.Accepted);
            Assert.Equal(WorldSlotHostState.Faulted, harness.Host.State);
        }
        finally
        {
            harness.Simulation.ReleaseTicks();
        }

        Assert.True(harness.Simulation.TickFinished.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));
        Assert.Equal(0UL, harness.Host.AuthorityRevision);
        Assert.Equal(default, harness.Host.LastTick);
        var events = new List<WorldSlotEvent>();
        while (harness.Host.TryDequeueEvent(out var evt))
        {
            events.Add(evt);
        }

        Assert.DoesNotContain(events, evt => evt is WorldSlotEvent.TickCompleted);
    }

    [Fact]
    public void OwnerSimulationFailureFailsStop()
    {
        using var harness = new Harness();
        harness.StartRunning();
        harness.Simulation.ThrowTick = true;

        Assert.Equal(
            EnqueueStatus.Accepted,
            harness.Host.EnqueueTick(new LogicalTickToken(1), harness.Host.Epoch).Status);
        Assert.True(SpinUntil(() => harness.Host.State == WorldSlotHostState.Faulted));
        Assert.Equal(AdmissionGateState.Closed, harness.Host.Gate);
    }

    [Fact]
    public void FaultedOutcomeWithoutWitnessFailsStop()
    {
        using var harness = new Harness();
        harness.StartRunning();
        harness.Simulation.ReturnFaultedWithoutWitness = true;

        Assert.Equal(
            EnqueueStatus.Accepted,
            harness.Host.EnqueueTick(new LogicalTickToken(1), harness.Host.Epoch).Status);
        Assert.True(SpinUntil(() => harness.Host.State == WorldSlotHostState.Faulted));
        Assert.Equal(AdmissionGateState.Closed, harness.Host.Gate);
    }

    private static string[] AcksBeforeFailedStep(QuiesceFailureStep step) => step switch
    {
        QuiesceFailureStep.CloseGate => Array.Empty<string>(),
        QuiesceFailureStep.Drain => AdmissionClosedOnly,
        QuiesceFailureStep.SnapshotCut => new[] { "AdmissionClosed", "Drained" },
        QuiesceFailureStep.PausePacing => new[] { "AdmissionClosed", "Drained", "SnapshotCut" },
        QuiesceFailureStep.Stop => new[] { "AdmissionClosed", "Drained", "SnapshotCut" },
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
    };

    private static bool IsExceptionName(string? fullName)
        => !string.IsNullOrEmpty(fullName)
            && (string.Equals(fullName, "System.Exception", StringComparison.Ordinal)
                || (fullName.StartsWith("System.", StringComparison.Ordinal)
                    && fullName.Contains("Exception", StringComparison.Ordinal)));

    private static string ReadWorldSlotHostSource()
    {
        var mvpHostDirectory = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(WorldSlotHost).Assembly.Location)!,
            "..",
            "..",
            "..",
            "..",
            ".."));
        return File.ReadAllText(Path.Combine(
            mvpHostDirectory,
            "src",
            "Lumio.Server.MvpHost.WorldSlot",
            "WorldSlotHost.cs"));
    }

    private static bool SpinUntil(Func<bool> predicate)
    {
        var deadline = Environment.TickCount64 + 5_000;
        var spin = new SpinWait();
        while (Environment.TickCount64 < deadline)
        {
            if (predicate())
            {
                return true;
            }

            spin.SpinOnce();
        }

        return predicate();
    }

    private static List<WorldSlotEvent> DrainEvents(Harness harness)
    {
        var events = new List<WorldSlotEvent>();
        while (harness.EventInbox.TryDequeue(out var evt))
        {
            events.Add(evt);
        }

        return events;
    }

    private sealed class Harness : IDisposable
    {
        internal Harness(int aggregateCapacity = 66, int eventCapacity = 256)
        {
            this.Simulation = new RecordingSimulation();
            this.Clock = new FakeMonotonicClock();
            this.Timers = new ControllableTimerService(PlatformModule.CreateTimerService(this.Clock));
            this.Threads = PlatformModule.CreateThreadSupervisor();
            this.AggregateInbox = PlatformModule.CreateInbox<WorldSlotCommand>(new QueueBudget(aggregateCapacity, 65_536));
            this.EventInbox = PlatformModule.CreateInbox<WorldSlotEvent>(new QueueBudget(eventCapacity, 65_536));
            this.EventFilter = new FilteringOutbox(PlatformModule.CreateOutbox(this.EventInbox));
            this.Trace = new RecordingHostTraceSink();
            var audit = PlatformModule.CreateInbox<AuditRecord>(new QueueBudget(16, 65_536));
            var diagnostics = PlatformModule.CreateInbox<DiagnosticRecord>(new QueueBudget(16, 65_536));
            this.Observability = ObservabilityModule.Create(
                audit,
                diagnostics,
                new FakeWallClock("2026-08-30T00:00:00Z"),
                this.Trace,
                new HostIdentity("A", "A-1", "worldslot-tests"));
            this.Host = WorldSlotHost.Create(
                this.Simulation,
                this.Clock,
                this.Timers,
                this.Threads,
                this.AggregateInbox,
                this.EventFilter,
                new EmptyIngress(),
                new MvpFaultAdjudicator(),
                this.Observability);
            this.Host.AttachEventInbox(this.EventInbox);
        }

        internal RecordingSimulation Simulation { get; }

        internal FakeMonotonicClock Clock { get; }

        internal ControllableTimerService Timers { get; }

        internal FilteringOutbox EventFilter { get; }

        internal INamedThreadSupervisor Threads { get; }

        internal IBoundedInbox<WorldSlotCommand> AggregateInbox { get; }

        internal IBoundedInbox<WorldSlotEvent> EventInbox { get; }

        internal RecordingHostTraceSink Trace { get; }

        internal ObservabilityServices Observability { get; }

        internal WorldSlotHost Host { get; }

        internal void StartRunning()
        {
            Assert.True(this.Host.Allocate(new SlotBudget(1, 64, 65_536)).Allocated);
            Assert.True(this.Host.StartRunning().Accepted);
        }

        internal void ArmQuiesceFailure(QuiesceFailureStep step)
        {
            switch (step)
            {
                case QuiesceFailureStep.CloseGate:
                    this.EventFilter.RejectOnce(typeof(WorldSlotEvent.GateStateChanged));
                    break;
                case QuiesceFailureStep.Drain:
                    this.Simulation.FailDrain = true;
                    break;
                case QuiesceFailureStep.SnapshotCut:
                    this.EventFilter.RejectOnce(typeof(WorldSlotEvent.Quiesced));
                    break;
                case QuiesceFailureStep.PausePacing:
                    this.Timers.ThrowOnCancel = true;
                    break;
                case QuiesceFailureStep.Stop:
                    this.EventFilter.RejectOnce(typeof(WorldSlotEvent.ReadyToStop));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(step), step, null);
            }
        }

        public void Dispose()
        {
            this.Host.Dispose();
            this.Timers.Dispose();
            this.Threads.Dispose();
        }
    }

    private sealed class EmptyIngress : IIngressReader
    {
        public int Drain(TransportConnectionId c, int maxItems, long maxBytes, Span<ValidatedEnvelopeBytes> destination) => 0;
    }

    private sealed class RecordingSimulation : IWorldSimulationPort
    {
        private int revision;

        internal ManualResetEventSlim TickStarted { get; } = new(false);

        internal ManualResetEventSlim TickFinished { get; } = new(false);

        internal bool BlockTicks { get; set; }

        internal bool FailDrain { get; set; }

        internal bool ThrowTick { get; set; }

        internal bool ReturnFaultedWithoutWitness { get; set; }

        private ManualResetEventSlim Release { get; } = new(false);

        public HostSimulationState State { get; private set; } = HostSimulationState.Created;

        public ulong AuthorityRevision => (ulong)Volatile.Read(ref this.revision);

        public string? LastThreadName { get; private set; }

        public string? InitializeThreadName { get; private set; }

        public string? ReadyThreadName { get; private set; }

        public HostLifecycleResult Initialize(in HostSessionInit init)
        {
            this.InitializeThreadName = Thread.CurrentThread.Name;
            this.State = HostSimulationState.Initialized;
            return new HostLifecycleResult(true, this.State, null);
        }

        public HostLifecycleResult Ready()
        {
            this.ReadyThreadName = Thread.CurrentThread.Name;
            this.State = HostSimulationState.Ready;
            return new HostLifecycleResult(true, this.State, null);
        }

        public HostTickOutcome RunTick(in HostTickRequest request)
        {
            this.LastThreadName = Thread.CurrentThread.Name;
            this.TickStarted.Set();
            if (this.ThrowTick)
            {
                throw new InvalidOperationException("tick");
            }

            if (this.ReturnFaultedWithoutWitness)
            {
                return new HostTickOutcome(
                    HostTickStatus.Faulted,
                    request.Tick,
                    ReadOnlyMemory<byte>.Empty,
                    AuthorityRevision,
                    ReadOnlyMemory<WireFrame>.Empty,
                    HostFaultClass.None,
                    null);
            }

            if (this.BlockTicks)
            {
                this.Release.Wait(TimeSpan.FromSeconds(5));
            }
            Interlocked.Increment(ref this.revision);
            this.TickFinished.Set();
            this.State = HostSimulationState.Running;
            return new HostTickOutcome(HostTickStatus.Completed, request.Tick, ReadOnlyMemory<byte>.Empty, AuthorityRevision, ReadOnlyMemory<WireFrame>.Empty, HostFaultClass.None, null);
        }

        public HostLifecycleResult Drain()
        {
            if (this.FailDrain)
            {
                return new HostLifecycleResult(false, HostSimulationState.Faulted, "InternalInvariant");
            }

            this.State = HostSimulationState.Draining;
            return new HostLifecycleResult(true, this.State, null);
        }

        public HostLifecycleResult Snapshot(out ReadOnlyMemory<byte> opaqueSnapshot)
        {
            opaqueSnapshot = ReadOnlyMemory<byte>.Empty;
            this.State = HostSimulationState.Snapshotted;
            return new HostLifecycleResult(true, this.State, null);
        }

        public void Dispose() => this.State = HostSimulationState.Disposed;

        internal void ReleaseTicks() => this.Release.Set();
    }

    public enum QuiesceFailureStep
    {
        CloseGate,
        Drain,
        SnapshotCut,
        PausePacing,
        Stop,
    }

    internal sealed class FilteringOutbox : IBoundedOutbox<WorldSlotEvent>
    {
        private readonly IBoundedOutbox<WorldSlotEvent> inner;
        private readonly object gate = new();
        private System.Type? rejectType;
        private int remaining;

        internal FilteringOutbox(IBoundedOutbox<WorldSlotEvent> inner) => this.inner = inner;

        internal void RejectOnce(System.Type eventType)
        {
            lock (this.gate)
            {
                this.rejectType = eventType;
                this.remaining = 1;
            }
        }

        public EnqueueResult TryPublish(in WorldSlotEvent item)
        {
            lock (this.gate)
            {
                if (this.rejectType is not null
                    && this.remaining > 0
                    && this.rejectType.IsInstanceOfType(item))
                {
                    this.remaining--;
                    return new EnqueueResult(EnqueueStatus.Full, "QueueFull");
                }
            }

            return this.inner.TryPublish(in item);
        }
    }

    internal sealed class ControllableTimerService : ITimerService
    {
        private readonly ITimerService inner;

        internal ControllableTimerService(ITimerService inner) => this.inner = inner;

        internal bool ThrowOnCancel { get; set; }

        public TimerId Schedule<TCommand>(
            MonotonicInstant dueAt,
            IBoundedInbox<TCommand> target,
            in TCommand command)
            => this.inner.Schedule(dueAt, target, in command);

        public bool Cancel(TimerId id)
        {
            if (this.ThrowOnCancel)
            {
                throw new InvalidOperationException("pacing-stop");
            }

            return this.inner.Cancel(id);
        }

        public void Dispose() => this.inner.Dispose();
    }
}
