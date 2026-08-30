using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.TestKit;
using Lumio.Server.MvpHost.WorldSlot;
using Xunit;

namespace Lumio.Server.MvpHost.WorldSlot.Tests;

public sealed class WorldSlotFocusedTests
{
    private static readonly string[] QuiesceAckNames =
        { "AdmissionClosed", "Drained", "SnapshotCut", "Stopped" };

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
    public void PublicAdmissionPortReturnsPerCallReservationAndSlotIdentity()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Allocate(new SlotBudget(4, 64, 65_536)).Allocated);
        Assert.IsAssignableFrom<IWorldSlotAdmissionPort>(harness.Host);
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
    public void PublicPacingPortUsesBoundedQueueAndEpochFence()
    {
        using var harness = new Harness();
        Assert.True(harness.Host.Allocate(new SlotBudget(1, 64, 65_536)).Allocated);
        Assert.True(harness.Host.StartRunning().Accepted);
        Assert.IsAssignableFrom<IWorldSlotPacingPort>(harness.Host);
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

    [Fact]
    public void ReservedCommandsUseEmergencyLaneWhenInboxIsFull()
    {
        using var harness = new Harness(aggregateCapacity: 64);
        Assert.True(harness.Host.Allocate(new SlotBudget(1, 64, 65_536)).Allocated);

        for (var i = 0; i < 64; i++)
        {
            var result = harness.Host.TryEnqueue(
                new WorldSlotCommand.DependencyAck(new AdmissionAttemptId((ulong)i), true, null));
            Assert.Equal(EnqueueStatus.Accepted, result.Status);
        }

        var quiesce = harness.Host.TryEnqueue(
            new WorldSlotCommand.Quiesce("shutdown", harness.Host.Epoch));
        var stop = harness.Host.TryEnqueue(new WorldSlotCommand.Stop(harness.Host.Epoch));
        var ordinary = harness.Host.TryEnqueue(
            new WorldSlotCommand.DependencyAck(new AdmissionAttemptId(1000), true, null));

        Assert.Equal(EnqueueStatus.Accepted, quiesce.Status);
        Assert.Equal(EnqueueStatus.Accepted, stop.Status);
        Assert.Equal(EnqueueStatus.Full, ordinary.Status);
        Assert.Equal("QueueFull", ordinary.StableErrorId);
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
        internal Harness(int aggregateCapacity = 66)
        {
            this.Simulation = new RecordingSimulation();
            this.Clock = new FakeMonotonicClock();
            this.Timers = PlatformModule.CreateTimerService(this.Clock);
            this.Threads = PlatformModule.CreateThreadSupervisor();
            this.AggregateInbox = PlatformModule.CreateInbox<WorldSlotCommand>(new QueueBudget(aggregateCapacity, 65_536));
            this.EventInbox = PlatformModule.CreateInbox<WorldSlotEvent>(new QueueBudget(256, 65_536));
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
                PlatformModule.CreateOutbox(this.EventInbox),
                new EmptyIngress(),
                new MvpFaultAdjudicator(),
                this.Observability);
        }

        internal RecordingSimulation Simulation { get; }

        internal FakeMonotonicClock Clock { get; }

        internal ITimerService Timers { get; }

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
}
