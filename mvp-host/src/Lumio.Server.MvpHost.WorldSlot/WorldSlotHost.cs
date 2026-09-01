using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Lumio.Gen.ContractTypes;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.WorldSlot;

/// <summary>
/// Host-owned WorldSlot aggregate. All externally supplied commands cross a bounded
/// inbox, and the owner thread is the only path that touches the simulation port.
/// </summary>
public sealed class WorldSlotHost : IWorldSlotHost, IDisposable
{
    private const int CriticalTerminalReserveSlots = 2;
    private static long nextSlotId;

    private readonly object sync = new();
    private readonly AutoResetEvent ownerSignal = new(false);
    private readonly IWorldSimulationPort simulation;
    private readonly IMonotonicClock clock;
    private readonly ITimerService timers;
    private readonly INamedThreadSupervisor threads;
    private readonly IBoundedInbox<WorldSlotCommand> aggregateInbox;
    private readonly IBoundedOutbox<WorldSlotEvent> eventOutbox;
    private IBoundedInbox<WorldSlotEvent>? eventInbox;
    private readonly IIngressReader ingress;
    private readonly IFaultAdjudicator adjudicator;
    private readonly ObservabilityServices observability;
    private readonly IBoundedInbox<WorldSlotCommand> tickPermitQueue;
    private readonly Dictionary<ulong, ServerSessionId> reservations = new();
    private readonly Dictionary<ulong, SlotReservationId> reservationsByAttempt = new();
    private readonly HashSet<string> boundSessions = new(StringComparer.Ordinal);
    private readonly Queue<WorldSlotEvent> terminalEvents = new();
    private readonly Queue<WorldSlotCommand> reservedCommands = new();
    private readonly Queue<PendingLifecycle> lifecycleRequests = new();
    private readonly Dictionary<WorldSlotCommand, PendingCommand> pendingCommands =
        new(CommandReferenceComparer.Instance);
    private readonly Dictionary<ulong, ServerSessionId> committedReservations = new();

    private SlotBudget budget;
    private WorldSlotId slotId;
    private SlotEpoch epoch;
    private SnapshotCutRef snapshotCut;
    private WorldSlotHostState state;
    private AdmissionGateState admissionGate;
    private ulong nextReservationId;
    private SlotReservationId lastReservation;
    private ulong nextSnapshotCut;
    private ulong authorityRevision;
    private ulong lastTick;
    private int tickOverruns;
    private bool allocated;
    private bool pacingStopped;
    private bool disposed;
    private bool ownerStarted;
    private bool disposeSimulationRequested;
    private bool simulationDisposed;
    private bool simulationInitialized;
    private bool simulationReady;
    private int ownerThreadId;
    private ThreadHandle ownerHandle;

    private WorldSlotHost(
        IWorldSimulationPort simulation,
        IMonotonicClock clock,
        ITimerService timers,
        INamedThreadSupervisor threads,
        IBoundedInbox<WorldSlotCommand> aggregateInbox,
        IBoundedOutbox<WorldSlotEvent> eventOutbox,
        IIngressReader ingress,
        IFaultAdjudicator adjudicator,
        ObservabilityServices observability)
    {
        this.simulation = simulation;
        this.clock = clock;
        this.timers = timers;
        this.threads = threads;
        this.aggregateInbox = aggregateInbox;
        this.eventOutbox = eventOutbox;
        this.ingress = ingress;
        this.adjudicator = adjudicator;
        this.observability = observability;
        this.tickPermitQueue = PlatformModule.CreateInbox<WorldSlotCommand>(
            new QueueBudget(
                WorldSlotProvisionalDefaults.TickPermitCapacity,
                WorldSlotProvisionalDefaults.IngressDrainBytesPerTick));
        this.state = WorldSlotStateMachine.InitialState;
        this.admissionGate = AdmissionGateState.Open;
        this.epoch = new SlotEpoch(1);
    }

    /// <summary>Composition-root factory; dependencies are explicit and never defaulted.</summary>
    public static WorldSlotHost Create(
        IWorldSimulationPort simulation,
        IMonotonicClock clock,
        ITimerService timers,
        INamedThreadSupervisor threads,
        IBoundedInbox<WorldSlotCommand> aggregateInbox,
        IBoundedOutbox<WorldSlotEvent> eventOutbox,
        IIngressReader ingress,
        IFaultAdjudicator adjudicator,
        ObservabilityServices observability)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(threads);
        ArgumentNullException.ThrowIfNull(aggregateInbox);
        ArgumentNullException.ThrowIfNull(eventOutbox);
        ArgumentNullException.ThrowIfNull(ingress);
        ArgumentNullException.ThrowIfNull(adjudicator);
        ArgumentNullException.ThrowIfNull(observability);

        return new WorldSlotHost(
            simulation,
            clock,
            timers,
            threads,
            aggregateInbox,
            eventOutbox,
            ingress,
            adjudicator,
            observability);
    }

    /// <summary>State-machine projections exposed alongside the aggregate for fixture checks.</summary>
    public static IReadOnlyList<WorldSlotTransition> ForwardTransitions
        => WorldSlotStateMachine.ForwardTransitions;

    public static AnyActiveToRule AnyActiveToFaulted
        => WorldSlotStateMachine.AnyActiveToFaulted;

    public WorldSlotHostState State
    {
        get
        {
            lock (this.sync)
            {
                return this.state;
            }
        }
    }

    public SlotEpoch Epoch
    {
        get
        {
            lock (this.sync)
            {
                return this.epoch;
            }
        }
    }

    internal WorldSlotId SlotId
    {
        get
        {
            lock (this.sync)
            {
                return this.slotId;
            }
        }
    }

    public AdmissionGateState Gate
    {
        get
        {
            lock (this.sync)
            {
                return this.admissionGate;
            }
        }
    }

    public QuotaView Capacity
    {
        get
        {
            lock (this.sync)
            {
                return new QuotaView(this.budget.MaxSessions, this.OccupiedSessionsUnsafe());
            }
        }
    }

    /// <summary>Name assigned by Platform to the sole simulation owner thread.</summary>
    internal string OwnerThreadName => this.ownerHandle.Name;

    /// <summary>Number of pacing permits discarded because the capacity-one queue was full.</summary>
    internal int TickOverruns => Volatile.Read(ref this.tickOverruns);

    internal LogicalTickToken LastTick
    {
        get
        {
            lock (this.sync)
            {
                return new LogicalTickToken(this.lastTick);
            }
        }
    }

    internal ulong AuthorityRevision
    {
        get
        {
            lock (this.sync)
            {
                return this.authorityRevision;
            }
        }
    }

    internal SlotReservationId LastReservation
    {
        get
        {
            lock (this.sync)
            {
                return this.lastReservation;
            }
        }
    }

    /// <summary>Whether pacing has been stopped by quiesce/fail-stop.</summary>
    internal bool IsPacingStopped
    {
        get
        {
            lock (this.sync)
            {
                return this.pacingStopped;
            }
        }
    }

    private bool IsDisposed
    {
        get
        {
            lock (this.sync)
            {
                return this.disposed;
            }
        }
    }

    /// <summary>The in-memory cut used by the MVP persistence absence.</summary>
    internal SnapshotCutRef SnapshotCut
    {
        get
        {
            lock (this.sync)
            {
                return this.snapshotCut;
            }
        }
    }

    /// <summary>
    /// Forwards simulation initialization through the owner queue.  The
    /// simulation port is never invoked on the caller's thread.
    /// </summary>
    public HostLifecycleResult Initialize(in HostSessionInit init)
        => this.SubmitLifecycle(LifecycleRequestKind.Initialize, in init);

    /// <summary>Forwards simulation readiness through the owner queue.</summary>
    public HostLifecycleResult Ready()
    {
        var emptyInit = default(HostSessionInit);
        return this.SubmitLifecycle(LifecycleRequestKind.Ready, in emptyInit);
    }

    /// <summary>
    /// Starts the complete MVP host path with a deterministic default runtime
    /// context.  Composition roots that own a richer context should use the
    /// overload accepting <see cref="HostSessionInit"/>.
    /// </summary>
    public AckResult StartRunning()
    {
        HostSessionInit init;
        lock (this.sync)
        {
            init = this.DefaultSimulationInitUnsafe();
        }

        return this.StartRunning(in init);
    }

    /// <summary>
    /// Runs Allocated → Bootstrapping → NativeReady → ManagedReady →
    /// LoadingSession → Running on the simulation owner thread, including the
    /// simulation Initialize/Ready calls.
    /// </summary>
    public AckResult StartRunning(in HostSessionInit init)
    {
        var result = this.SubmitLifecycle(LifecycleRequestKind.StartRunning, in init);
        return result.Accepted
            ? new AckResult(true, null)
            : new AckResult(false, result.StableErrorId ?? "InternalInvariant");
    }

    public AllocateResult Allocate(in SlotBudget budget)
    {
        WorldSlotId allocatedSlot;
        lock (this.sync)
        {
            if (this.disposed)
            {
                return this.AllocateFailure("ContextDestroyed");
            }

            if (this.allocated)
            {
                if (this.state is WorldSlotHostState.Destroyed or WorldSlotHostState.Faulted)
                {
                    return this.AllocateFailure("ContextDestroyed");
                }

                return new AllocateResult(true, this.slotId, this.epoch, null);
            }

            if (budget.MaxSessions <= 0
                || budget.MaxIngressItemsPerTick <= 0
                || budget.MaxIngressBytesPerTick <= 0)
            {
                return this.AllocateFailure("InvalidArgument");
            }

            this.budget = budget;
            this.slotId = new WorldSlotId((ulong)Interlocked.Increment(ref nextSlotId));
            this.epoch = new SlotEpoch(1);
            this.reservations.Clear();
            this.reservationsByAttempt.Clear();
            this.boundSessions.Clear();
            this.committedReservations.Clear();
            this.nextReservationId = 0;
            this.lastReservation = default;
            this.snapshotCut = default;
            this.nextSnapshotCut = 0;
            this.authorityRevision = 0;
            this.lastTick = 0;
            this.tickOverruns = 0;
            this.simulationInitialized = false;
            this.simulationReady = false;
            this.state = WorldSlotHostState.Allocated;
            this.admissionGate = AdmissionGateState.Open;
            this.allocated = true;
            this.pacingStopped = false;
            allocatedSlot = this.slotId;
        }

        try
        {
            var handle = this.threads.Start(
                $"worldslot-{allocatedSlot.Value}",
                new OwnerThreadBody(this));
            var abandon = false;
            lock (this.sync)
            {
                this.ownerHandle = handle;
                this.ownerStarted = true;
                abandon = this.disposed;
                if (!abandon)
                {
                    return new AllocateResult(true, this.slotId, this.epoch, null);
                }
            }

            if (abandon)
            {
                this.threads.Dispose();
                return new AllocateResult(false, allocatedSlot, new SlotEpoch(1), "ContextDestroyed");
            }

            return new AllocateResult(false, allocatedSlot, new SlotEpoch(1), "InternalInvariant");
        }
        catch
        {
            lock (this.sync)
            {
                this.allocated = false;
                this.slotId = default;
                return this.AllocateFailure("InternalInvariant");
            }
        }
    }

    /// <summary>
    /// Enqueues a reservation and waits for the owner reducer when one is available.
    /// The reservation itself is counted against quota before it is committed.
    /// </summary>
    internal AllocateResult TryReserve(AdmissionAttemptId attempt, ServerSessionId session)
    {
        var result = this.ReserveAdmission(attempt, session);
        lock (this.sync)
        {
            return new AllocateResult(
                result.Reserved,
                result.SlotId,
                result.Epoch,
                result.StableErrorId);
        }
    }

    /// <summary>
    /// Reserves one admission and returns the exact handle created by the owner
    /// reducer.  The returned value is per invocation; callers must not read a
    /// mutable "last reservation" property to identify their reservation.
    /// </summary>
    internal AdmissionReservationResult ReserveAdmission(
        AdmissionAttemptId attempt,
        ServerSessionId session)
    {
        if (attempt.Value == 0 || string.IsNullOrWhiteSpace(session.Value))
        {
            return this.ReservationFailure("InvalidArgument");
        }

        var command = new WorldSlotCommand.ReserveAdmission(attempt, session);
        var pending = new PendingCommand();
        var enqueue = this.Enqueue(command, pending);
        if (enqueue.Status != EnqueueStatus.Accepted)
        {
            return this.ReservationFailure(enqueue.StableErrorId ?? "QueueFull");
        }

        this.AwaitPendingCommand(pending);

        lock (this.sync)
        {
            return pending.ReservationResult ?? new AdmissionReservationResult(
                false,
                default,
                this.epoch,
                this.slotId,
                pending.Completed.IsSet
                    ? pending.Ack.StableErrorId ?? "InternalInvariant"
                    : "TimedOut");
        }
    }

    public AckResult BindSession(SlotReservationId reservation, ServerSessionId session, SlotEpoch epoch)
    {
        var command = new WorldSlotCommand.CommitAdmission(reservation, session, epoch);
        return this.SubmitAck(command);
    }

    internal AckResult AbortAdmission(SlotReservationId reservation, SlotEpoch epoch)
    {
        var command = new WorldSlotCommand.AbortAdmission(reservation, epoch);
        return this.SubmitAck(command);
    }

    internal AckResult ReleaseCommittedReservation(
        SlotReservationId reservation,
        ServerSessionId session,
        SlotEpoch epoch)
    {
        if (reservation.Value == 0 || string.IsNullOrWhiteSpace(session.Value))
        {
            return new AckResult(false, "InvalidArgument");
        }

        var command = new WorldSlotCommand.AbortAdmission(reservation, epoch);
        var pending = new PendingCommand { ReleaseSession = session };
        var enqueue = this.Enqueue(command, pending);
        if (enqueue.Status != EnqueueStatus.Accepted)
        {
            return new AckResult(false, enqueue.StableErrorId ?? "QueueFull");
        }

        this.AwaitPendingCommand(pending);

        lock (this.sync)
        {
            return pending.Completed.IsSet
                ? pending.Ack
                : new AckResult(false, "TimedOut");
        }
    }

    /// <summary>
    /// Enqueues one typed pacing permit into the capacity-one owner queue.
    /// Epoch and lifecycle checks are shared with the internal test path.
    /// </summary>
    internal EnqueueResult EnqueueTickPermit(LogicalTickToken tick, SlotEpoch epoch)
        => this.EnqueueTick(tick, epoch);

    public AckResult Quiesce(string reason, SlotEpoch epoch)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return new AckResult(false, "InvalidArgument");
        }

        var command = new WorldSlotCommand.Quiesce(reason, epoch);
        return this.SubmitAck(command);
    }

    /// <summary>
    /// MVP persistence absence: retain an in-memory cut only; do not enter Snapshotting.
    /// See absences.json ABS-PERSISTENCE-SNAPSHOT.
    /// </summary>
    public SnapshotCutRef FixSnapshotCut(SlotEpoch epoch)
    {
        lock (this.sync)
        {
            if (!this.CheckEpochUnsafe(epoch)
                || !this.allocated
                || this.disposed
                || this.state is WorldSlotHostState.Destroyed or WorldSlotHostState.Faulted)
            {
                return default;
            }

            return this.FixSnapshotCutUnsafe();
        }
    }

    public AckResult Destroy(SlotEpoch epoch)
    {
        lock (this.sync)
        {
            if (!this.CheckEpochUnsafe(epoch))
            {
                return new AckResult(false, "StaleEpoch");
            }

            if (this.disposed
                || this.state == WorldSlotHostState.Faulted
                || this.state == WorldSlotHostState.Destroyed)
            {
                return new AckResult(false, "ContextDestroyed");
            }

            if (this.state != WorldSlotHostState.Stopping)
            {
                return new AckResult(false, "InvalidArgument");
            }

            if (this.admissionGate != AdmissionGateState.Closed
                && !this.SetGateUnsafe(AdmissionGateState.Closed))
            {
                return new AckResult(false, "QueueFull");
            }

            this.state = WorldSlotHostState.Destroyed;
            this.pacingStopped = true;
            this.tickPermitQueue.Close();
            this.aggregateInbox.Close();
            this.disposeSimulationRequested = true;
        }

        if (this.IsOwnerThread())
        {
            this.OnOwnerDisposeRequested();
        }

        return new AckResult(true, null);
    }

    public AckResult ReportFault(string registeredErrorCode, HostFaultClass faultClass, SlotEpoch epoch)
    {
        if (string.IsNullOrWhiteSpace(registeredErrorCode)
            || Array.IndexOf(Catalog.StableErrorIds, registeredErrorCode) < 0)
        {
            return new AckResult(false, "InvalidArgument");
        }

        lock (this.sync)
        {
            if (!this.CheckEpochUnsafe(epoch))
            {
                return new AckResult(false, "StaleEpoch");
            }

            if (!this.allocated
                || this.disposed
                || this.state is WorldSlotHostState.Faulted or WorldSlotHostState.Destroyed)
            {
                return new AckResult(false, "ContextDestroyed");
            }

            FaultAdjudication adjudication;
            try
            {
                adjudication = this.adjudicator.Classify(faultClass);
            }
            catch
            {
                adjudication = new FaultAdjudication(HostFaultClass.SlotStateUnproven, true, false);
            }

            if (adjudication.SlotMustFailStop)
            {
                this.EnterFaultedUnsafe(adjudication);
            }
            else if (!this.PublishEventUnsafe(new WorldSlotEvent.FaultAdjudicated(adjudication, this.epoch)))
            {
                return new AckResult(false, "QueueFull");
            }

            return new AckResult(true, null);
        }
    }

    /// <summary>
    /// Applies one of the eight MVP lifecycle edges. Deferred edges remain in
    /// <see cref="WorldSlotStateMachine.ForwardTransitions"/> but are intentionally
    /// unreachable until their owning modules land.
    /// </summary>
    internal bool Advance(WorldSlotHostState target)
    {
        lock (this.sync)
        {
            if (!this.allocated || this.disposed || WorldSlotStateMachine.IsTerminal(this.state))
            {
                return false;
            }

            if (!WorldSlotStateMachine.TryGetForward(this.state, target, out var transition))
            {
                return false;
            }

            if (!IsMvpReachableEvent(transition.Event))
            {
                return false;
            }

            this.state = target;
            return true;
        }
    }

    /// <summary>Bounded external command ingress used by Session/maintenance/pacing.</summary>
    internal EnqueueResult EnqueueCommand(WorldSlotCommand command)
        => this.Enqueue(command, pending: null);

    /// <summary>Internal test/owner hook for deterministic command pumping.</summary>
    internal EnqueueResult TryEnqueueCommand(WorldSlotCommand command)
        => this.Enqueue(command, pending: null);

    internal AggregateQueueAdmission TryEnqueueCommand(
        WorldSlotCommand command,
        out EnqueueResult outward)
    {
        outward = this.Enqueue(command, pending: null);
        return outward.Status switch
        {
            EnqueueStatus.Accepted => AggregateQueueAdmission.Accepted,
            EnqueueStatus.Full => AggregateQueueAdmission.AggregateBusy,
            _ => AggregateQueueAdmission.Closed,
        };
    }

    /// <summary>Internal aggregate-only gate mutation used by lifecycle reducers.</summary>
    internal AckResult SetGate(AdmissionGateState state, SlotEpoch epoch)
    {
        if (state is not AdmissionGateState.Open and not AdmissionGateState.Closed)
        {
            return new AckResult(false, "InvalidArgument");
        }

        lock (this.sync)
        {
            if (!this.CheckEpochUnsafe(epoch))
            {
                return new AckResult(false, "StaleEpoch");
            }

            if (!this.allocated || this.disposed)
            {
                return new AckResult(false, "ContextDestroyed");
            }

            return this.SetGateUnsafe(state)
                ? new AckResult(true, null)
                : new AckResult(false, "QueueFull");
        }
    }

    internal EnqueueResult EnqueueTick(LogicalTickToken tick, SlotEpoch expectedEpoch)
    {
        var command = new WorldSlotCommand.TickPermit(tick, expectedEpoch);
        lock (this.sync)
        {
            if (!this.CheckEpochUnsafe(expectedEpoch))
            {
                return new EnqueueResult(EnqueueStatus.Closed, "StaleEpoch");
            }

            if (this.pacingStopped || this.state != WorldSlotHostState.Running)
            {
                return new EnqueueResult(EnqueueStatus.Closed, "ContextClosing");
            }

            WorldSlotCommand queued = command;
            var result = this.tickPermitQueue.TryEnqueue(in queued);
            if (result.Status == EnqueueStatus.Full)
            {
                Interlocked.Increment(ref this.tickOverruns);
                return new EnqueueResult(EnqueueStatus.Full, "QueueFull");
            }

            if (result.Status == EnqueueStatus.Accepted)
            {
                this.ownerSignal.Set();
            }

            return result;
        }
    }

    internal int TickPermitCount => this.tickPermitQueue.Count;

    internal QueueBudget AggregateInboxBudget => this.aggregateInbox.Budget;

    internal QueueBudget TickPermitBudget => this.tickPermitQueue.Budget;

    internal IMonotonicClock Clock => this.clock;

    internal ITimerService Timers => this.timers;

    internal int ReservedCommandCount
    {
        get
        {
            lock (this.sync)
            {
                return this.reservedCommands.Count;
            }
        }
    }

    internal void PumpOnce()
    {
        if (this.IsOwnerThread())
        {
            this.PumpOwnerQueues();
            return;
        }

        this.WaitForOwnerProgress();
    }

    /// <summary>Consumes one bounded aggregate command batch on the owner thread.</summary>
    internal void PumpCommands()
    {
        if (this.IsOwnerThread())
        {
            this.PumpOwnerQueues();
            return;
        }

        this.WaitForOwnerProgress();
    }

    /// <summary>Enqueues a typed command and maps a full aggregate lane to QueueFull.</summary>
    internal EnqueueResult TryEnqueue(WorldSlotCommand command) => this.Enqueue(command, pending: null);

    internal bool TryDequeueEvent(out WorldSlotEvent evt)
    {
        lock (this.sync)
        {
            if (this.eventInbox is not null
                && this.eventInbox.TryDequeue(out evt!))
            {
                return true;
            }

            if (this.terminalEvents.Count > 0)
            {
                evt = this.terminalEvents.Dequeue();
                return true;
            }

            evt = default!;
            return false;
        }
    }

    /// <summary>Attaches the composition-root read lane for the unified FIFO view.</summary>
    internal void AttachEventInbox(IBoundedInbox<WorldSlotEvent> inbox)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        lock (this.sync)
        {
            this.eventInbox = inbox;
        }
    }

    private void WaitForOwnerProgress()
    {
        lock (this.sync)
        {
            if (this.aggregateInbox.Count == 0
                && this.reservedCommands.Count == 0
                && this.tickPermitQueue.Count == 0
                && this.lifecycleRequests.Count == 0)
            {
                return;
            }

            if (this.state is WorldSlotHostState.Faulted
                or WorldSlotHostState.Stopping
                or WorldSlotHostState.Destroyed)
            {
                return;
            }

            // The owner pump pulses this monitor after consuming a batch. This
            // keeps the synchronous test/control facade bounded without adding a
            // second timing or polling loop outside Platform.
            Monitor.Wait(this.sync, TimeSpan.FromSeconds(2));
        }
    }

    public void Dispose()
    {
        bool shouldJoin;
        bool disposingOnOwner;
        lock (this.sync)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.pacingStopped = true;
            this.disposeSimulationRequested = true;
            foreach (var pending in this.pendingCommands.Values)
            {
                pending.Complete(new AckResult(false, "ContextDestroyed"));
            }

            this.pendingCommands.Clear();
            this.reservedCommands.Clear();
            while (this.lifecycleRequests.Count > 0)
            {
                this.lifecycleRequests.Dequeue().Complete(
                    new HostLifecycleResult(false, this.simulation.State, "ContextDestroyed"));
            }
            shouldJoin = this.ownerStarted;
            disposingOnOwner = this.ownerStarted
                && this.ownerThreadId == Environment.CurrentManagedThreadId;
        }

        this.aggregateInbox.Close();
        this.tickPermitQueue.Close();
        this.ownerSignal.Set();

        if (shouldJoin && !disposingOnOwner)
        {
            // The injected supervisor owns the join boundary. It is idempotent and
            // therefore safe for a composition root to dispose it again.
            this.threads.Dispose();
        }

        lock (this.sync)
        {
            if (!this.simulationDisposed)
            {
                this.OnOwnerDisposeRequestedUnsafe();
            }
        }
    }

    private AckResult SubmitAck(WorldSlotCommand command)
    {
        var pending = new PendingCommand();
        var enqueue = this.Enqueue(command, pending);
        if (enqueue.Status != EnqueueStatus.Accepted)
        {
            return new AckResult(false, enqueue.StableErrorId ?? "QueueFull");
        }

        this.AwaitPendingCommand(pending);

        lock (this.sync)
        {
            if (!pending.Completed.IsSet)
            {
                // The command remains in the bounded queue, but this synchronous
                // facade cannot claim an acknowledgement it has not observed.
                return new AckResult(false, "TimedOut");
            }

            return pending.Ack;
        }
    }

    private void AwaitPendingCommand(PendingCommand pending)
    {
        if (this.IsOwnerThread())
        {
            this.PumpOwnerQueues();
            return;
        }

        if (pending.Completed.Wait(TimeSpan.FromSeconds(2)))
        {
            return;
        }

        if (!pending.TryCancel(new AckResult(false, "TimedOut")))
        {
            pending.Completed.Wait();
        }
    }

    private EnqueueResult Enqueue(WorldSlotCommand command, PendingCommand? pending)
    {
        ArgumentNullException.ThrowIfNull(command);

        lock (this.sync)
        {
            if (this.disposed)
            {
                return new EnqueueResult(EnqueueStatus.Closed, "ContextDestroyed");
            }

            if (!this.allocated)
            {
                return new EnqueueResult(EnqueueStatus.Closed, "ContextClosing");
            }

            if (this.state is WorldSlotHostState.Faulted or WorldSlotHostState.Destroyed)
            {
                return new EnqueueResult(EnqueueStatus.Closed, "ContextDestroyed");
            }

            if (this.state == WorldSlotHostState.Stopping
                && command is not WorldSlotCommand.Stop
                && command is not WorldSlotCommand.CommitAdmission
                && command is not WorldSlotCommand.AbortAdmission)
            {
                return new EnqueueResult(EnqueueStatus.Closed, "ContextClosing");
            }

            if (!this.CommandEpochIsCurrentUnsafe(command))
            {
                return new EnqueueResult(EnqueueStatus.Closed, "StaleEpoch");
            }

            if (command is WorldSlotCommand.TickPermit)
            {
                return this.EnqueueTickCommandUnsafe(command);
            }

            var configuredCapacity = this.aggregateInbox.Budget.MaxItems;
            var ordinaryLimit = configuredCapacity >=
                WorldSlotProvisionalDefaults.AggregateInboxMaxItems
                + WorldSlotProvisionalDefaults.AggregateInboxReservedSlots
                ? configuredCapacity - WorldSlotProvisionalDefaults.AggregateInboxReservedSlots
                : configuredCapacity;
            var reserved = command is WorldSlotCommand.Quiesce
                or WorldSlotCommand.Stop;
            if (this.reservedCommands.Count > 0)
            {
                if (!reserved
                    || this.reservedCommands.Count >= WorldSlotProvisionalDefaults.AggregateInboxReservedSlots)
                {
                    return new EnqueueResult(EnqueueStatus.Full, "QueueFull");
                }

                if (pending is not null)
                {
                    this.pendingCommands[command] = pending;
                }

                this.reservedCommands.Enqueue(command);
                this.ownerSignal.Set();
                return new EnqueueResult(EnqueueStatus.Accepted, null);
            }

            if (!reserved && this.aggregateInbox.Count >= ordinaryLimit)
            {
                return new EnqueueResult(EnqueueStatus.Full, "QueueFull");
            }

            if (pending is not null)
            {
                this.pendingCommands[command] = pending;
            }

            var result = this.aggregateInbox.TryEnqueue(in command);
            if (reserved && result.Status == EnqueueStatus.Full
                && this.reservedCommands.Count < WorldSlotProvisionalDefaults.AggregateInboxReservedSlots)
            {
                this.reservedCommands.Enqueue(command);
                this.ownerSignal.Set();
                return new EnqueueResult(EnqueueStatus.Accepted, null);
            }

            if (result.Status != EnqueueStatus.Accepted)
            {
                if (pending is not null)
                {
                    this.pendingCommands.Remove(command);
                }

                return result.Status == EnqueueStatus.Full
                    ? new EnqueueResult(EnqueueStatus.Full, "QueueFull")
                    : result;
            }

            this.ownerSignal.Set();
            return result;
        }
    }

    private EnqueueResult EnqueueTickCommandUnsafe(WorldSlotCommand command)
    {
        if (this.pacingStopped || this.state != WorldSlotHostState.Running)
        {
            return new EnqueueResult(EnqueueStatus.Closed, "ContextClosing");
        }

        var result = this.tickPermitQueue.TryEnqueue(in command);
        if (result.Status == EnqueueStatus.Full)
        {
            Interlocked.Increment(ref this.tickOverruns);
            return new EnqueueResult(EnqueueStatus.Full, "QueueFull");
        }

        if (result.Status == EnqueueStatus.Accepted)
        {
            this.ownerSignal.Set();
        }

        return result;
    }

    private void PumpOwnerQueues()
    {
        this.ProcessLifecycleRequests();

        while (this.aggregateInbox.TryDequeue(out var command))
        {
            this.ProcessCommand(command);
        }

        while (true)
        {
            WorldSlotCommand? reserved;
            lock (this.sync)
            {
                reserved = this.reservedCommands.Count > 0
                    ? this.reservedCommands.Dequeue()
                    : null;
            }

            if (reserved is null)
            {
                break;
            }

            this.ProcessCommand(reserved);
        }

        while (this.tickPermitQueue.TryDequeue(out var permit))
        {
            this.ProcessTickPermit(permit);
        }

        lock (this.sync)
        {
            Monitor.PulseAll(this.sync);
            if (this.disposeSimulationRequested && !this.simulationDisposed)
            {
                this.OnOwnerDisposeRequestedUnsafe();
            }
        }
    }

    private void ProcessLifecycleRequests()
    {
        while (true)
        {
            PendingLifecycle request;
            lock (this.sync)
            {
                if (this.lifecycleRequests.Count == 0)
                {
                    return;
                }

                request = this.lifecycleRequests.Dequeue();
                if (!request.TryClaim())
                {
                    continue;
                }
            }

            HostLifecycleResult result;
            try
            {
                result = request.Kind switch
                {
                    LifecycleRequestKind.Initialize => this.ApplyInitialize(request.Init),
                    LifecycleRequestKind.Ready => this.ApplyReady(),
                    LifecycleRequestKind.StartRunning => this.ApplyStartRunning(request.Init),
                    _ => new HostLifecycleResult(false, this.simulation.State, "InvalidArgument"),
                };
            }
            catch
            {
                lock (this.sync)
                {
                    this.EnterFaultedUnsafe(this.ClassifyUnproven());
                    result = new HostLifecycleResult(false, this.simulation.State, "InternalInvariant");
                }
            }

            request.Complete(result);
        }
    }

    private HostLifecycleResult SubmitLifecycle(
        LifecycleRequestKind kind,
        in HostSessionInit init)
    {
        var pending = new PendingLifecycle(kind, in init);
        var owner = this.IsOwnerThread();

        lock (this.sync)
        {
            if (this.disposed || this.state is WorldSlotHostState.Faulted or WorldSlotHostState.Destroyed)
            {
                return new HostLifecycleResult(false, this.simulation.State, "ContextDestroyed");
            }

            if (!this.allocated)
            {
                return new HostLifecycleResult(false, this.simulation.State, "ContextClosing");
            }

            if (this.state is WorldSlotHostState.Quiescing
                or WorldSlotHostState.Stopping)
            {
                return new HostLifecycleResult(false, this.simulation.State, "ContextClosing");
            }

            if (this.lifecycleRequests.Count >= WorldSlotProvisionalDefaults.AggregateInboxReservedSlots + 2)
            {
                return new HostLifecycleResult(false, this.simulation.State, "QueueFull");
            }

            this.lifecycleRequests.Enqueue(pending);
            this.ownerSignal.Set();
        }

        this.AwaitPendingLifecycle(pending, owner);

        lock (this.sync)
        {
            return pending.Completed.IsSet
                ? pending.Result
                : new HostLifecycleResult(false, this.simulation.State, "TimedOut");
        }
    }

    private void AwaitPendingLifecycle(PendingLifecycle pending, bool owner)
    {
        if (owner)
        {
            this.ProcessLifecycleRequests();
            return;
        }

        if (pending.Completed.Wait(TimeSpan.FromSeconds(2)))
        {
            return;
        }

        HostSimulationState state;
        lock (this.sync)
        {
            state = this.simulation.State;
        }

        if (!pending.TryCancel(new HostLifecycleResult(false, state, "TimedOut")))
        {
            pending.Completed.Wait();
        }
    }

    private HostLifecycleResult ApplyInitialize(in HostSessionInit init)
    {
        lock (this.sync)
        {
            return this.ApplyInitializeUnsafe(in init);
        }
    }

    private HostLifecycleResult ApplyInitializeUnsafe(in HostSessionInit init)
    {
        if (!this.allocated || this.disposed)
        {
            return new HostLifecycleResult(false, this.simulation.State, "ContextClosing");
        }

        if (this.state is WorldSlotHostState.Faulted or WorldSlotHostState.Destroyed)
        {
            return new HostLifecycleResult(false, this.simulation.State, "ContextDestroyed");
        }

        if (string.IsNullOrWhiteSpace(init.Session.Value) || init.Slot.Value == 0)
        {
            return new HostLifecycleResult(false, this.simulation.State, "InvalidArgument");
        }

        if (this.simulationInitialized)
        {
            return new HostLifecycleResult(false, this.simulation.State, "WrongContext");
        }

        if (this.simulation.State != HostSimulationState.Created)
        {
            return new HostLifecycleResult(false, this.simulation.State, "WrongContext");
        }

        var result = this.simulation.Initialize(in init);
        if (!result.Accepted)
        {
            this.FailLifecycleIfUnprovenUnsafe(result.StableErrorId);
            return result;
        }

        if (result.State != HostSimulationState.Initialized)
        {
            this.EnterFaultedUnsafe(this.ClassifyUnproven());
            return new HostLifecycleResult(false, this.simulation.State, "InternalInvariant");
        }

        this.simulationInitialized = true;
        return result;
    }

    private HostLifecycleResult ApplyReady()
    {
        lock (this.sync)
        {
            return this.ApplyReadyUnsafe();
        }
    }

    private HostLifecycleResult ApplyReadyUnsafe()
    {
        if (!this.allocated || this.disposed)
        {
            return new HostLifecycleResult(false, this.simulation.State, "ContextClosing");
        }

        if (this.state is WorldSlotHostState.Faulted or WorldSlotHostState.Destroyed)
        {
            return new HostLifecycleResult(false, this.simulation.State, "ContextDestroyed");
        }

        if (!this.simulationInitialized)
        {
            return new HostLifecycleResult(false, this.simulation.State, "WrongContext");
        }

        if (this.simulationReady)
        {
            return new HostLifecycleResult(false, this.simulation.State, "WrongContext");
        }

        var result = this.simulation.Ready();
        if (!result.Accepted)
        {
            this.FailLifecycleIfUnprovenUnsafe(result.StableErrorId);
            return result;
        }

        if (result.State != HostSimulationState.Ready)
        {
            this.EnterFaultedUnsafe(this.ClassifyUnproven());
            return new HostLifecycleResult(false, this.simulation.State, "InternalInvariant");
        }

        this.simulationReady = true;
        return result;
    }

    private HostLifecycleResult ApplyStartRunning(in HostSessionInit init)
    {
        lock (this.sync)
        {
            if (!this.allocated || this.disposed)
            {
                return new HostLifecycleResult(false, this.simulation.State, "ContextClosing");
            }

            if (this.state is WorldSlotHostState.Faulted or WorldSlotHostState.Destroyed)
            {
                return new HostLifecycleResult(false, this.simulation.State, "ContextDestroyed");
            }

            if (this.state is WorldSlotHostState.Quiescing or WorldSlotHostState.Stopping)
            {
                return new HostLifecycleResult(false, this.simulation.State, "ContextClosing");
            }

            var simulationState = this.simulation.State;
            if (simulationState == HostSimulationState.Faulted)
            {
                this.EnterFaultedUnsafe(this.ClassifyUnproven());
                return new HostLifecycleResult(false, simulationState, "InternalInvariant");
            }

            if (simulationState == HostSimulationState.Disposed)
            {
                return new HostLifecycleResult(false, simulationState, "ContextDestroyed");
            }

            if (simulationState is HostSimulationState.Draining or HostSimulationState.Snapshotted)
            {
                return new HostLifecycleResult(false, simulationState, "ContextClosing");
            }

            if (!this.simulationInitialized
                && (string.IsNullOrWhiteSpace(init.Session.Value) || init.Slot.Value == 0))
            {
                return new HostLifecycleResult(false, simulationState, "InvalidArgument");
            }

            // A composition root may have initialized the injected simulation
            // before calling this aggregate entrypoint.  Adopt only the frozen
            // lifecycle states; no simulation call is made off the owner thread.
            this.ObserveSimulationLifecycleUnsafe();

            if (this.state == WorldSlotHostState.Allocated)
            {
                this.state = WorldSlotHostState.Bootstrapping;
            }

            if (this.state == WorldSlotHostState.Bootstrapping && !this.simulationInitialized)
            {
                var initialized = this.ApplyInitializeUnsafe(in init);
                if (!initialized.Accepted)
                {
                    if (initialized.StableErrorId is "InvalidArgument" or "WrongContext")
                    {
                        this.state = WorldSlotHostState.Allocated;
                    }

                    return initialized;
                }
            }

            if (this.state == WorldSlotHostState.Bootstrapping)
            {
                // ABS-WORLDSLOT-NATIVE: MVP has no Native library to load.
                // NativeReady remains a distinct state; the empty load below is
                // the explicit NativeLoaded traversal.
                this.LoadAbsentNativeRuntimeUnsafe();
                this.state = WorldSlotHostState.NativeReady;
            }

            if (this.state == WorldSlotHostState.NativeReady)
            {
                this.state = WorldSlotHostState.ManagedReady;
            }

            this.ObserveSimulationLifecycleUnsafe();
            if (this.state == WorldSlotHostState.ManagedReady && !this.simulationReady)
            {
                var ready = this.ApplyReadyUnsafe();
                if (!ready.Accepted)
                {
                    return ready;
                }
            }

            if (this.state == WorldSlotHostState.ManagedReady)
            {
                this.state = WorldSlotHostState.LoadingSession;
            }

            if (this.state == WorldSlotHostState.LoadingSession)
            {
                this.state = WorldSlotHostState.Running;
            }

            return new HostLifecycleResult(
                true,
                this.simulation.State,
                null);
        }
    }

    private void LoadAbsentNativeRuntimeUnsafe()
    {
        this.observability.Trace.State(
            null,
            nameof(WorldSlotHostState.NativeReady),
            this.authorityRevision,
            this.epoch.Value,
            null);
    }

    private void ObserveSimulationLifecycleUnsafe()
    {
        var simulationState = this.simulation.State;
        this.simulationInitialized |= simulationState is
            HostSimulationState.Initialized
            or HostSimulationState.Ready
            or HostSimulationState.Running
            or HostSimulationState.Draining
            or HostSimulationState.Snapshotted;
        this.simulationReady |= simulationState is
            HostSimulationState.Ready
            or HostSimulationState.Running;
    }

    private void FailLifecycleIfUnprovenUnsafe(string? stableErrorId)
    {
        if (stableErrorId is not ("InvalidArgument" or "WrongContext" or "ContextClosing" or "ContextDestroyed"))
        {
            this.EnterFaultedUnsafe(this.ClassifyUnproven());
        }
    }

    private void ProcessCommand(WorldSlotCommand command)
    {
        PendingCommand? pending;
        lock (this.sync)
        {
            if (this.pendingCommands.Remove(command, out pending)
                && !pending.TryClaim())
            {
                return;
            }
        }

        try
        {
            switch (command)
            {
                case WorldSlotCommand.ReserveAdmission reserve:
                    this.ApplyReserve(reserve, pending);
                    break;
                case WorldSlotCommand.CommitAdmission commit:
                    this.ApplyCommit(commit, pending);
                    break;
                case WorldSlotCommand.AbortAdmission abort:
                    this.ApplyAbort(abort, pending);
                    break;
                case WorldSlotCommand.Quiesce quiesce:
                    this.ApplyQuiesce(quiesce, pending);
                    break;
                case WorldSlotCommand.Stop stop:
                    this.ApplyStop(stop, pending);
                    break;
                case WorldSlotCommand.DependencyAck dependency:
                    this.ApplyDependencyAck(dependency, pending);
                    break;
                case WorldSlotCommand.TickPermit:
                    // Tick permits belong to the dedicated SPSC queue. If one is
                    // delivered through the aggregate lane, process it safely anyway.
                    this.ProcessTickPermit(command);
                    pending?.Complete(new AckResult(true, null));
                    break;
            }
        }
        catch
        {
            lock (this.sync)
            {
                var adjudication = this.ClassifyUnproven();
                this.EnterFaultedUnsafe(adjudication);
                pending?.Complete(new AckResult(false, "InternalInvariant"));
            }
        }
    }

    private void ApplyReserve(WorldSlotCommand.ReserveAdmission command, PendingCommand? pending)
    {
        lock (this.sync)
        {
            if (command.Attempt.Value == 0 || string.IsNullOrWhiteSpace(command.Session.Value))
            {
                pending?.SetReservation(new AdmissionReservationResult(
                    false,
                    default,
                    this.epoch,
                    this.slotId,
                    "InvalidArgument"));
                this.PublishEventUnsafe(new WorldSlotEvent.AdmissionRejected(command.Attempt, "InvalidArgument"));
                pending?.Complete(new AckResult(false, "InvalidArgument"));
                return;
            }

            if (!this.allocated || this.disposed || this.admissionGate != AdmissionGateState.Open)
            {
                pending?.SetReservation(new AdmissionReservationResult(
                    false,
                    default,
                    this.epoch,
                    this.slotId,
                    "ContextClosing"));
                this.PublishEventUnsafe(new WorldSlotEvent.AdmissionRejected(command.Attempt, "ContextClosing"));
                pending?.Complete(new AckResult(false, "ContextClosing"));
                return;
            }

            if (this.reservationsByAttempt.TryGetValue(command.Attempt.Value, out var priorReservation))
            {
                if (this.reservations.TryGetValue(priorReservation.Value, out var priorSession)
                    && priorSession == command.Session)
                {
                    pending?.SetReservation(new AdmissionReservationResult(
                        true,
                        priorReservation,
                        this.epoch,
                        this.slotId,
                        null));
                    pending?.SetAllocation(
                        new AllocateResult(true, this.slotId, this.epoch, null),
                        priorReservation);
                    pending?.Complete(new AckResult(true, null));
                    return;
                }

                if (this.committedReservations.TryGetValue(priorReservation.Value, out var committedSession)
                    && committedSession == command.Session)
                {
                    pending?.SetReservation(new AdmissionReservationResult(
                        true,
                        priorReservation,
                        this.epoch,
                        this.slotId,
                        null));
                    pending?.SetAllocation(
                        new AllocateResult(true, this.slotId, this.epoch, null),
                        priorReservation);
                    pending?.Complete(new AckResult(true, null));
                    return;
                }

                pending?.SetReservation(new AdmissionReservationResult(
                    false,
                    default,
                    this.epoch,
                    this.slotId,
                    "InvalidArgument"));
                pending?.Complete(new AckResult(false, "InvalidArgument"));
                return;
            }

            if (this.OccupiedSessionsUnsafe() >= this.budget.MaxSessions)
            {
                pending?.SetReservation(new AdmissionReservationResult(
                    false,
                    default,
                    this.epoch,
                    this.slotId,
                    "CapacityExceeded"));
                this.PublishEventUnsafe(new WorldSlotEvent.AdmissionRejected(command.Attempt, "CapacityExceeded"));
                pending?.Complete(new AckResult(false, "CapacityExceeded"));
                return;
            }

            if (this.ReservationsContainUnsafe(command.Session)
                || this.boundSessions.Contains(command.Session.Value))
            {
                pending?.SetReservation(new AdmissionReservationResult(
                    false,
                    default,
                    this.epoch,
                    this.slotId,
                    "CapacityExceeded"));
                this.PublishEventUnsafe(new WorldSlotEvent.AdmissionRejected(command.Attempt, "CapacityExceeded"));
                pending?.Complete(new AckResult(false, "CapacityExceeded"));
                return;
            }

            var reservation = new SlotReservationId(++this.nextReservationId);
            this.reservations[reservation.Value] = command.Session;
            var evt = new WorldSlotEvent.AdmissionReserved(command.Attempt, reservation, this.epoch);
            if (!this.PublishEventUnsafe(evt))
            {
                this.reservations.Remove(reservation.Value);
                pending?.SetReservation(new AdmissionReservationResult(
                    false,
                    default,
                    this.epoch,
                    this.slotId,
                    "QueueFull"));
                pending?.Complete(new AckResult(false, "QueueFull"));
                return;
            }

            this.reservationsByAttempt[command.Attempt.Value] = reservation;
            this.lastReservation = reservation;

            pending?.SetReservation(new AdmissionReservationResult(
                true,
                reservation,
                this.epoch,
                this.slotId,
                null));
            pending?.SetAllocation(new AllocateResult(true, this.slotId, this.epoch, null), reservation);
            pending?.Complete(new AckResult(true, null));
        }
    }

    private void ApplyCommit(WorldSlotCommand.CommitAdmission command, PendingCommand? pending)
    {
        lock (this.sync)
        {
            if (!this.CheckEpochUnsafe(command.Epoch))
            {
                pending?.Complete(new AckResult(false, "StaleEpoch"));
                return;
            }

            if (string.IsNullOrWhiteSpace(command.Session.Value))
            {
                pending?.Complete(new AckResult(false, "InvalidArgument"));
                return;
            }

            if (this.state is WorldSlotHostState.Quiescing
                or WorldSlotHostState.Stopping
                or WorldSlotHostState.Faulted
                or WorldSlotHostState.Destroyed)
            {
                pending?.Complete(new AckResult(false, "ContextClosing"));
                return;
            }

            if (this.admissionGate != AdmissionGateState.Open)
            {
                pending?.Complete(new AckResult(false, "ContextClosing"));
                return;
            }

            var removed = this.reservations.Remove(command.Reservation.Value, out var reservedSession);
            if (!removed || reservedSession != command.Session)
            {
                // A mismatched commit must never consume the reservation owned by
                // another session; restore it so the saga can retry or abort.
                if (removed)
                {
                    this.reservations[command.Reservation.Value] = reservedSession;
                }

                if (this.committedReservations.TryGetValue(command.Reservation.Value, out var committed)
                    && committed == command.Session)
                {
                    pending?.Complete(new AckResult(true, null));
                    return;
                }

                pending?.Complete(new AckResult(false, "InvalidArgument"));
                return;
            }

            if (this.boundSessions.Contains(command.Session.Value))
            {
                this.reservations[command.Reservation.Value] = reservedSession;
                pending?.Complete(new AckResult(false, "CapacityExceeded"));
                return;
            }

            this.boundSessions.Add(command.Session.Value);
            this.committedReservations[command.Reservation.Value] = command.Session;
            var evt = new WorldSlotEvent.SessionAssociated(command.Session, this.slotId, this.epoch);
            if (!this.PublishEventUnsafe(evt))
            {
                this.boundSessions.Remove(command.Session.Value);
                this.committedReservations.Remove(command.Reservation.Value);
                this.reservations[command.Reservation.Value] = reservedSession;
                pending?.Complete(new AckResult(false, "QueueFull"));
                return;
            }

            pending?.Complete(new AckResult(true, null));
        }
    }

    private void ApplyAbort(WorldSlotCommand.AbortAdmission command, PendingCommand? pending)
    {
        lock (this.sync)
        {
            if (!this.CheckEpochUnsafe(command.Epoch))
            {
                pending?.Complete(new AckResult(false, "StaleEpoch"));
                return;
            }

            if (this.state is WorldSlotHostState.Faulted
                or WorldSlotHostState.Destroyed)
            {
                pending?.Complete(new AckResult(false, "ContextClosing"));
                return;
            }

            if (pending?.ReleaseSession is { } releaseSession)
            {
                if (this.committedReservations.TryGetValue(command.Reservation.Value, out var committed))
                {
                    if (committed != releaseSession)
                    {
                        pending.Complete(new AckResult(false, "InvalidArgument"));
                        return;
                    }

                    this.committedReservations.Remove(command.Reservation.Value);
                    this.boundSessions.Remove(releaseSession.Value);
                    this.RemoveReservationAttemptUnsafe(command.Reservation);
                    pending.Complete(new AckResult(true, null));
                    return;
                }

                pending.Complete(this.reservations.ContainsKey(command.Reservation.Value)
                    ? new AckResult(false, "InvalidArgument")
                    : new AckResult(true, null));
                return;
            }

            if (this.committedReservations.ContainsKey(command.Reservation.Value))
            {
                pending?.Complete(new AckResult(false, "InvalidArgument"));
                return;
            }

            var removed = this.reservations.Remove(command.Reservation.Value);
            if (removed)
            {
                this.RemoveReservationAttemptUnsafe(command.Reservation);
            }
            pending?.Complete(removed
                ? new AckResult(true, null)
                : command.Reservation.Value > 0
                    && command.Reservation.Value <= this.nextReservationId
                    ? new AckResult(true, null)
                    : new AckResult(false, "InvalidArgument"));
        }
    }

    private void ApplyDependencyAck(WorldSlotCommand.DependencyAck command, PendingCommand? pending)
    {
        lock (this.sync)
        {
            if (!command.Accepted)
            {
                pending?.Complete(new AckResult(false, NormalizeStableErrorId(command.StableErrorId)));
                return;
            }

            var next = this.state switch
            {
                WorldSlotHostState.Allocated => WorldSlotHostState.Bootstrapping,
                WorldSlotHostState.Bootstrapping => WorldSlotHostState.NativeReady,
                WorldSlotHostState.NativeReady => WorldSlotHostState.ManagedReady,
                WorldSlotHostState.ManagedReady => WorldSlotHostState.LoadingSession,
                WorldSlotHostState.LoadingSession => WorldSlotHostState.Running,
                _ => (WorldSlotHostState?)null,
            };

            if (next is null)
            {
                pending?.Complete(new AckResult(false, "InvalidArgument"));
                return;
            }

            this.state = next.Value;
            pending?.Complete(new AckResult(true, null));
        }
    }

    private void ApplyQuiesce(WorldSlotCommand.Quiesce command, PendingCommand? pending)
    {
        lock (this.sync)
        {
            if (!this.CheckEpochUnsafe(command.Epoch))
            {
                pending?.Complete(new AckResult(false, "StaleEpoch"));
                return;
            }

            if (this.state == WorldSlotHostState.Faulted || this.state == WorldSlotHostState.Destroyed)
            {
                pending?.Complete(new AckResult(false, "ContextDestroyed"));
                return;
            }

            if (this.state != WorldSlotHostState.Running)
            {
                pending?.Complete(new AckResult(false, "InvalidArgument"));
                return;
            }

            try
            {
                this.state = WorldSlotHostState.Quiescing;
                if (!this.SetGateUnsafe(AdmissionGateState.Closed))
                {
                    throw new InvalidOperationException();
                }
                this.observability.Trace.Ack("AdmissionClosed", null, this.epoch.Value, null);

                // Drain is an explicit Runtime port call and therefore runs only on
                // this owner thread. In-flight admission reservations are discarded
                // after the gate closes; committed sessions remain accounted for.
                this.reservations.Clear();
                this.reservationsByAttempt.Clear();
                var drained = this.simulation.Drain();
                if (!drained.Accepted)
                {
                    throw new InvalidOperationException();
                }

                this.observability.Trace.Ack("Drained", null, this.epoch.Value, null);

                // ABS-PERSISTENCE-SNAPSHOT: this MVP records a cut in memory and
                // deliberately does not enter WorldSlotHostState.Snapshotting.
                this.FixSnapshotCutUnsafe();
                if (!this.PublishEventUnsafe(new WorldSlotEvent.Quiesced(this.snapshotCut, this.epoch)))
                {
                    throw new InvalidOperationException();
                }
                this.observability.Trace.Ack("SnapshotCut", null, this.epoch.Value, null);

                this.pacingStopped = true;
                this.tickPermitQueue.Close();
                this.timers.Cancel(default);

                this.state = WorldSlotHostState.Stopping;
                if (!this.TryPublishPrimaryUnsafe(new WorldSlotEvent.ReadyToStop(this.epoch)))
                {
                    throw new InvalidOperationException();
                }
                this.observability.Trace.Ack("Stopped", null, this.epoch.Value, null);
                pending?.Complete(new AckResult(true, null));
            }
            catch
            {
                var adjudication = this.ClassifyUnproven();
                this.EnterFaultedUnsafe(adjudication);
                pending?.Complete(new AckResult(false, "InternalInvariant"));
            }
        }
    }

    private void ApplyStop(WorldSlotCommand.Stop command, PendingCommand? pending)
    {
        lock (this.sync)
        {
            if (!this.CheckEpochUnsafe(command.Epoch))
            {
                pending?.Complete(new AckResult(false, "StaleEpoch"));
                return;
            }

            if (this.state == WorldSlotHostState.Stopping)
            {
                pending?.Complete(new AckResult(true, null));
                return;
            }

            if (this.state != WorldSlotHostState.Quiescing)
            {
                pending?.Complete(new AckResult(false, "InvalidArgument"));
                return;
            }

            this.pacingStopped = true;
            this.tickPermitQueue.Close();
            this.state = WorldSlotHostState.Stopping;
            this.observability.Trace.Ack("Stopped", null, this.epoch.Value, null);
            this.PublishEventUnsafe(new WorldSlotEvent.ReadyToStop(this.epoch));
            pending?.Complete(new AckResult(true, null));
        }
    }

    private void ProcessTickPermit(WorldSlotCommand command)
    {
        if (command is not WorldSlotCommand.TickPermit permit)
        {
            return;
        }

        lock (this.sync)
        {
            if (!this.CheckEpochUnsafe(permit.Epoch)
                || this.state != WorldSlotHostState.Running
                || this.pacingStopped
                || this.disposed)
            {
                return;
            }

        }

        try
        {
            var itemBudget = Math.Min(
                this.budget.MaxIngressItemsPerTick,
                WorldSlotProvisionalDefaults.IngressDrainItemsPerTick);
            var byteBudget = Math.Min(
                this.budget.MaxIngressBytesPerTick,
                WorldSlotProvisionalDefaults.IngressDrainBytesPerTick);
            ValidatedEnvelopeBytes[] received = new ValidatedEnvelopeBytes[itemBudget];
            var count = this.ingress.Drain(
                new TransportConnectionId(0),
                itemBudget,
                byteBudget,
                received.AsSpan());
            count = Math.Clamp(count, 0, received.Length);

            var frames = new WireFrame[count];
            for (var i = 0; i < count; i++)
            {
                frames[i] = new WireFrame(received[i].Bytes);
            }

            var request = new HostTickRequest(
                permit.Tick,
                frames,
                this.slotId.Value ^ permit.Tick.Value);
            var outcome = this.simulation.RunTick(in request);
            var adjudication = this.adjudicator.Classify(outcome.FaultClass);

            lock (this.sync)
            {
                if (!this.CheckEpochUnsafe(permit.Epoch)
                    || this.state != WorldSlotHostState.Running
                    || this.pacingStopped
                    || this.disposed)
                {
                    return;
                }

                if (adjudication.SlotMustFailStop)
                {
                    this.EnterFaultedUnsafe(adjudication);
                    return;
                }

                if (adjudication.SessionMustIsolate)
                {
                    if (!this.PublishEventUnsafe(new WorldSlotEvent.FaultAdjudicated(adjudication, this.epoch)))
                    {
                        this.EnterFaultedUnsafe(this.ClassifyUnproven());
                    }

                    return;
                }

                if (outcome.Status == HostTickStatus.Faulted)
                {
                    // A Faulted status without a slot/process witness is
                    // unproven by definition; never treat None as an implicit
                    // healthy attestation.
                    this.EnterFaultedUnsafe(this.ClassifyUnproven());
                    return;
                }

                if (outcome.Status != HostTickStatus.Completed)
                {
                    return;
                }

                if (outcome.AuthorityRevision < this.authorityRevision)
                {
                    this.EnterFaultedUnsafe(new FaultAdjudication(HostFaultClass.SlotStateUnproven, true, false));
                    return;
                }

                if (outcome.AuthorityRevision != this.authorityRevision)
                {
                    this.authorityRevision = outcome.AuthorityRevision;
                    this.observability.Trace.State(
                        null,
                        null,
                        this.authorityRevision,
                        this.epoch.Value,
                        null);
                }

                this.lastTick = outcome.Tick.Value;
                this.PublishEventUnsafe(new WorldSlotEvent.TickCompleted(
                    outcome.Tick,
                    outcome.AuthorityRevision,
                    this.epoch));
            }
        }
        catch
        {
            lock (this.sync)
            {
                this.EnterFaultedUnsafe(this.ClassifyUnproven());
            }
        }
    }

    private void EnterFaultedUnsafe(FaultAdjudication adjudication)
    {
        if (!adjudication.SlotMustFailStop
            || !WorldSlotStateMachine.CanFailStop(this.state))
        {
            return;
        }

        this.SetGateUnsafe(AdmissionGateState.Closed);
        this.pacingStopped = true;
        this.state = WorldSlotHostState.Faulted;
        this.reservations.Clear();
        this.reservationsByAttempt.Clear();
        this.reservedCommands.Clear();
        this.tickPermitQueue.Close();
        this.disposeSimulationRequested = true;
        var faultPublished = this.PublishEventUnsafe(
            new WorldSlotEvent.FaultAdjudicated(adjudication, this.epoch));
        var stopPublished = this.PublishEventUnsafe(
            new WorldSlotEvent.ReadyToStop(this.epoch));
        if (!faultPublished || !stopPublished)
        {
            this.observability.Diagnostics.Write(
                "Diagnostic",
                "Error",
                "world-slot terminal event reserve exhausted");
            throw new InvalidOperationException("World-slot terminal event reserve exhausted");
        }
    }

    private bool SetGateUnsafe(AdmissionGateState value)
    {
        if (this.state is WorldSlotHostState.Destroyed or WorldSlotHostState.Faulted)
        {
            return value == this.admissionGate;
        }

        if (value == AdmissionGateState.Open
            && (this.state is WorldSlotHostState.Quiescing
                or WorldSlotHostState.Stopping
                or WorldSlotHostState.Faulted
                or WorldSlotHostState.Destroyed))
        {
            return false;
        }

        if (this.admissionGate == value)
        {
            return true;
        }

        var previous = this.admissionGate;
        this.admissionGate = value;
        if (this.PublishEventUnsafe(new WorldSlotEvent.GateStateChanged(value, this.epoch)))
        {
            return true;
        }

        // Closing is fail-closed even when the event lane is saturated. An
        // attempted reopen, however, must not change state without its event.
        this.admissionGate = value == AdmissionGateState.Closed
            ? AdmissionGateState.Closed
            : previous;
        return false;
    }

    private bool TryPublishPrimaryUnsafe(in WorldSlotEvent evt)
    {
        try
        {
            return this.eventOutbox.TryPublish(in evt).Status == EnqueueStatus.Accepted;
        }
        catch
        {
            return false;
        }
    }

    private bool PublishEventUnsafe(WorldSlotEvent evt)
    {
        // Once one event spills into the reserve, every later event joins that
        // FIFO tail until it drains. The dequeue path drains the older primary
        // prefix before taking this queue. Non-terminal events cannot consume
        // the two slots reserved for the fail-stop pair.
        if (this.terminalEvents.Count > 0)
        {
            return this.TryEnqueueTerminalEventUnsafe(evt);
        }

        try
        {
            var result = this.eventOutbox.TryPublish(in evt);
            if (result.Status == EnqueueStatus.Accepted)
            {
                return true;
            }

            if (IsTerminalEvent(evt))
            {
                return this.TryEnqueueTerminalEventUnsafe(evt);
            }

            return false;
        }
        catch
        {
            if (IsTerminalEvent(evt))
            {
                return this.TryEnqueueTerminalEventUnsafe(evt);
            }

            return false;
        }
    }

    private bool TryEnqueueTerminalEventUnsafe(WorldSlotEvent evt)
    {
        var capacity = Math.Max(
            WorldSlotProvisionalDefaults.SlotEventOutboxMaxItems,
            this.eventInbox?.Budget.MaxItems ?? 0)
            + CriticalTerminalReserveSlots;
        var criticalCount = 0;
        foreach (var queued in this.terminalEvents)
        {
            if (IsCriticalTerminalEvent(queued))
            {
                criticalCount++;
            }
        }

        if (this.terminalEvents.Count >= capacity
            || (!IsCriticalTerminalEvent(evt)
                && this.terminalEvents.Count
                    >= capacity - Math.Max(0, CriticalTerminalReserveSlots - criticalCount)))
        {
            this.observability.Diagnostics.Write(
                "Diagnostic",
                "Warn",
                IsCriticalTerminalEvent(evt)
                    ? "world-slot terminal event reserve exhausted"
                    : "world-slot non-terminal event tail is saturated");
            return false;
        }

        this.terminalEvents.Enqueue(evt);
        return true;
    }

    private static bool IsTerminalEvent(WorldSlotEvent evt)
        => evt is WorldSlotEvent.ReadyToStop
            or WorldSlotEvent.FaultAdjudicated
            or WorldSlotEvent.AdmissionRejected;

    private static bool IsCriticalTerminalEvent(WorldSlotEvent evt)
        => evt is WorldSlotEvent.ReadyToStop
            || evt is WorldSlotEvent.FaultAdjudicated fault
                && fault.Adjudication.SlotMustFailStop;

    private SnapshotCutRef FixSnapshotCutUnsafe()
    {
        if (this.snapshotCut.Value == 0)
        {
            this.snapshotCut = new SnapshotCutRef(++this.nextSnapshotCut);
        }

        return this.snapshotCut;
    }

    private HostSessionInit DefaultSimulationInitUnsafe()
        => new(
            new HostSessionId($"worldslot-{this.slotId.Value}"),
            new HostWorldSlotId(this.slotId.Value),
            ReadOnlyMemory<byte>.Empty,
            this.slotId.Value);

    private int OccupiedSessionsUnsafe() => this.reservations.Count + this.boundSessions.Count;

    private void RemoveReservationAttemptUnsafe(SlotReservationId reservation)
    {
        ulong? matchingAttempt = null;
        foreach (var pair in this.reservationsByAttempt)
        {
            if (pair.Value == reservation)
            {
                matchingAttempt = pair.Key;
                break;
            }
        }

        if (matchingAttempt is not null)
        {
            this.reservationsByAttempt.Remove(matchingAttempt.Value);
        }
    }

    private bool ReservationsContainUnsafe(ServerSessionId session)
    {
        foreach (var value in this.reservations.Values)
        {
            if (value == session)
            {
                return true;
            }
        }

        return false;
    }

    private bool CheckEpochUnsafe(SlotEpoch expected) => expected == this.epoch;

    private bool CommandEpochIsCurrentUnsafe(WorldSlotCommand command) => command switch
    {
        WorldSlotCommand.CommitAdmission c => this.CheckEpochUnsafe(c.Epoch),
        WorldSlotCommand.AbortAdmission c => this.CheckEpochUnsafe(c.Epoch),
        WorldSlotCommand.Quiesce c => this.CheckEpochUnsafe(c.Epoch),
        WorldSlotCommand.Stop c => this.CheckEpochUnsafe(c.Epoch),
        WorldSlotCommand.TickPermit c => this.CheckEpochUnsafe(c.Epoch),
        _ => true,
    };

    private FaultAdjudication ClassifyUnproven()
    {
        try
        {
            return this.adjudicator.Classify(null);
        }
        catch
        {
            return new FaultAdjudication(HostFaultClass.SlotStateUnproven, true, false);
        }
    }

    private AllocateResult AllocateFailure(string error)
        => new(false, this.slotId, this.epoch, error);

    private AdmissionReservationResult ReservationFailure(string error)
    {
        lock (this.sync)
        {
            return new AdmissionReservationResult(false, default, this.epoch, this.slotId, error);
        }
    }

    private static string NormalizeStableErrorId(string? candidate)
    {
        if (string.Equals(
                candidate,
                nameof(AggregateQueueAdmission.AggregateBusy),
                StringComparison.Ordinal))
        {
            return "QueueFull";
        }

        return candidate is not null
            && Array.IndexOf(Catalog.StableErrorIds, candidate) >= 0
            ? candidate
            : "InvalidArgument";
    }

    private bool IsOwnerThread()
    {
        var observedOwner = Volatile.Read(ref this.ownerThreadId);
        return observedOwner != 0
            ? observedOwner == Environment.CurrentManagedThreadId
            : this.ownerStarted && Environment.CurrentManagedThreadId == this.ownerHandle.ManagedThreadId;
    }

    private void OnOwnerDisposeRequested()
    {
        lock (this.sync)
        {
            this.OnOwnerDisposeRequestedUnsafe();
        }
    }

    private void OnOwnerDisposeRequestedUnsafe()
    {
        if (this.simulationDisposed)
        {
            return;
        }

        try
        {
            this.simulation.Dispose();
        }
        catch
        {
            // The supervisor owns the process-level failure boundary. A disposal
            // exception cannot reopen a slot or create a new lifecycle path.
        }

        this.simulationDisposed = true;
    }

    private static bool IsMvpReachableEvent(string @event)
        => @event is "BeginBootstrap"
            or "NativeLoaded"
            or "ManagedLoaded"
            or "LoadSession"
            or "SessionLoaded"
            or "Quiesce"
            or "Stop"
            or "TeardownComplete";

    private sealed class OwnerThreadBody : IThreadBody
    {
        private readonly WorldSlotHost owner;
        private WaitHandle[]? waitHandles;

        internal OwnerThreadBody(WorldSlotHost owner) => this.owner = owner;

        public ThreadStepResult Step(CancellationToken ct)
        {
            Interlocked.CompareExchange(
                ref this.owner.ownerThreadId,
                Environment.CurrentManagedThreadId,
                comparand: 0);

            if (ct.IsCancellationRequested)
            {
                this.owner.OnOwnerDisposeRequested();
                return new ThreadStepResult(false, null);
            }

            if (this.owner.IsDisposed)
            {
                this.owner.OnOwnerDisposeRequested();
                return new ThreadStepResult(false, null);
            }

            try
            {
                this.owner.PumpOwnerQueues();
                if (this.owner.State is WorldSlotHostState.Faulted or WorldSlotHostState.Destroyed)
                {
                    this.owner.OnOwnerDisposeRequested();
                    return new ThreadStepResult(false, null);
                }

                if (!ct.IsCancellationRequested)
                {
                    this.waitHandles ??= [this.owner.ownerSignal, ct.WaitHandle];
                    _ = WaitHandle.WaitAny(this.waitHandles);
                }
            }
            catch
            {
                lock (this.owner.sync)
                {
                    this.owner.EnterFaultedUnsafe(this.owner.ClassifyUnproven());
                }

                return new ThreadStepResult(false, "PanicBoundary");
            }

            return new ThreadStepResult(!ct.IsCancellationRequested, null);
        }
    }

    private enum LifecycleRequestKind
    {
        Initialize,
        Ready,
        StartRunning,
    }

    private sealed class PendingLifecycle
    {
        private int state;

        internal PendingLifecycle(LifecycleRequestKind kind, in HostSessionInit init)
        {
            this.Kind = kind;
            this.Init = new HostSessionInit(
                init.Session,
                init.Slot,
                init.OpaqueConfig.ToArray(),
                init.DeterministicSeed);
        }

        internal LifecycleRequestKind Kind { get; }

        internal HostSessionInit Init { get; }

        internal ManualResetEventSlim Completed { get; } = new(false);

        internal HostLifecycleResult Result { get; private set; }

        internal bool TryClaim()
            => Interlocked.CompareExchange(
                ref this.state,
                (int)PendingRequestState.Claimed,
                (int)PendingRequestState.Pending) == (int)PendingRequestState.Pending;

        internal bool TryCancel(HostLifecycleResult result)
        {
            if (Interlocked.CompareExchange(
                    ref this.state,
                    (int)PendingRequestState.Canceled,
                    (int)PendingRequestState.Pending) != (int)PendingRequestState.Pending)
            {
                return false;
            }

            this.Result = result;
            this.Completed.Set();
            return true;
        }

        internal void Complete(HostLifecycleResult result)
        {
            var prior = Interlocked.Exchange(ref this.state, (int)PendingRequestState.Completed);
            if (prior is (int)PendingRequestState.Canceled or (int)PendingRequestState.Completed)
            {
                return;
            }

            this.Result = result;
            this.Completed.Set();
        }
    }

    private sealed class PendingCommand
    {
        private int state;

        internal ManualResetEventSlim Completed { get; } = new(false);

        internal AckResult Ack { get; private set; }

        internal ServerSessionId? ReleaseSession { get; init; }

        internal AllocateResult? Allocation { get; private set; }

        internal AdmissionReservationResult? ReservationResult { get; private set; }

        internal SlotReservationId Reservation { get; private set; }

        internal bool TryClaim()
            => Interlocked.CompareExchange(
                ref this.state,
                (int)PendingRequestState.Claimed,
                (int)PendingRequestState.Pending) == (int)PendingRequestState.Pending;

        internal bool TryCancel(AckResult ack)
        {
            if (Interlocked.CompareExchange(
                    ref this.state,
                    (int)PendingRequestState.Canceled,
                    (int)PendingRequestState.Pending) != (int)PendingRequestState.Pending)
            {
                return false;
            }

            this.Ack = ack;
            this.Completed.Set();
            return true;
        }

        internal void SetAllocation(AllocateResult allocation, SlotReservationId reservation)
        {
            this.Allocation = allocation;
            this.Reservation = reservation;
            this.ReservationResult = new AdmissionReservationResult(
                allocation.Allocated,
                reservation,
                allocation.Epoch,
                allocation.SlotId,
                allocation.StableErrorId);
        }

        internal void SetReservation(AdmissionReservationResult result)
        {
            this.ReservationResult = result;
        }

        internal void Complete(AckResult ack)
        {
            var prior = Interlocked.Exchange(ref this.state, (int)PendingRequestState.Completed);
            if (prior is (int)PendingRequestState.Canceled or (int)PendingRequestState.Completed)
            {
                return;
            }

            this.Ack = ack;
            this.Completed.Set();
        }
    }

    private enum PendingRequestState
    {
        Pending,
        Claimed,
        Completed,
        Canceled,
    }

    private sealed class CommandReferenceComparer : IEqualityComparer<WorldSlotCommand>
    {
        internal static CommandReferenceComparer Instance { get; } = new();

        public bool Equals(WorldSlotCommand? x, WorldSlotCommand? y) => ReferenceEquals(x, y);

        public int GetHashCode(WorldSlotCommand obj) => RuntimeHelpers.GetHashCode(obj);
    }
}

/// <summary>
/// Result of a serialized admission reservation. This aggregate-private
/// exchange type intentionally does not live in public HostContracts.
/// </summary>
internal readonly record struct AdmissionReservationResult(
    bool Reserved,
    SlotReservationId Reservation,
    SlotEpoch Epoch,
    WorldSlotId SlotId,
    string? StableErrorId);
