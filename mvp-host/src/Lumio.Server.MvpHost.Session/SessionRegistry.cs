using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.Wire;

namespace Lumio.Server.MvpHost.Session;

/// <summary>
/// Single-writer session coordinator. Cross-module work is expressed through
/// HostContracts ports; this assembly does not own transport or slot registries.
/// </summary>
public sealed class SessionRegistry : IDisposable
{
    private const int ReservationReleaseRetryLimit = 3;
    private const int UnbindRetryLimit = 3;
    private readonly IWorldSlotHost slot;
    private readonly ISessionWorldSlotPort? admissionPort;
    private readonly IAuthorizationService auth;
    private readonly ITransportControlPort transportControl;
    private readonly IEgressWriter egress;
    private readonly IWorldMutationSink? worldMutations;
    private readonly IMonotonicClock clock;
    private readonly ITimerService timers;
    private readonly IBoundedInbox<SessionCommand> controlInbox;
    private readonly IBoundedOutbox<SessionEvent> eventOutbox;
    private IBoundedInbox<SessionEvent>? eventInbox;
    private readonly ObservabilityServices observability;
    private readonly SessionHostConfiguration config;
    // All public ingress paths share this gate. The owner pump remains the only
    // path that applies queued commands, while transport/admin callers cannot
    // mutate session maps concurrently with it.
    private readonly object ownerGate = new();
    private readonly Dictionary<string, ServerConnectionSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, string> connectionSessions = new();
    private readonly Dictionary<string, int> attemptsByConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, AdmissionAttemptState> admissions = new();
    private readonly Dictionary<string, AuthenticatedConnection> authenticatedConnections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeferredReconnect> deferredReconnects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SlotReservationId> committedReservationsBySession = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, PendingReservationRelease> pendingReservationReleases = new();
    private readonly Queue<PendingReservationRelease> deadLetterReservationReleases = new();
    private readonly HashSet<ulong> deadLetterReservationIds = new();
    private readonly Dictionary<(ulong ConnectionId, ulong Epoch), PendingUnbind> pendingUnbinds = new();
    private readonly Queue<PendingUnbind> deadLetterUnbinds = new();
    private readonly HashSet<ulong> compensatedAttempts = new();
    private readonly Queue<ulong> compensatedAttemptOrder = new();
    private readonly HashSet<ulong> rejectedAttempts = new();
    private readonly Queue<ulong> rejectedAttemptOrder = new();
    private readonly Queue<SessionEvent> terminalReserve = new();
    private readonly Queue<OwnerIngress> ownerIngress = new();
    private readonly Dictionary<(ulong ConnectionId, ulong Epoch), PendingTerminalClose> pendingTerminalCloses = new();
    private readonly HashSet<string> retainedTerminalSessions = new(StringComparer.Ordinal);
    private readonly Queue<string> terminalSessionOrder = new();
    private readonly ISessionAdminPort? admin;
    private readonly MvpAdmissionReducer reducer = new();

    private ulong nextAttempt;
    private ulong nextContext;
    private ulong outboundSequence;
    private long auditSequence;
    private ulong authorityRevision;
    private ulong nextReconnectTimerToken;
    private WorldSlotId worldSlotId;
    private SlotEpoch worldSlotEpoch;
    private bool worldSlotAllocated;
    private bool draining;
    private bool disposed;
    private int ownerThreadId;
    private int admissionSagaDepth;
    private int ownerPumpDepth;

    private SessionRegistry(
        IWorldSlotHost slot,
        IAuthorizationService auth,
        ITransportControlPort transportControl,
        IEgressWriter egress,
        IWorldMutationSink? worldMutations,
        IMonotonicClock clock,
        ITimerService timers,
        IBoundedInbox<SessionCommand> controlInbox,
        IBoundedOutbox<SessionEvent> eventOutbox,
        ObservabilityServices observability,
        in SessionHostConfiguration config)
    {
        this.slot = slot;
        this.admissionPort = slot as ISessionWorldSlotPort;
        this.auth = auth;
        this.transportControl = transportControl;
        this.egress = egress;
        this.worldMutations = worldMutations;
        this.clock = clock;
        this.timers = timers;
        this.controlInbox = controlInbox;
        this.eventOutbox = eventOutbox;
        this.observability = observability;
        this.config = config.Normalize();

        if (this.config.TestControlEnabled && worldMutations is not null)
        {
            this.admin = new SessionAdminPort(this);
        }
    }

    /// <summary>Explicit composition-root factory; no service locator or hidden defaults.</summary>
    public static SessionRegistry Create(
        IWorldSlotHost slot,
        IAuthorizationService auth,
        ITransportControlPort transportControl,
        IEgressWriter egress,
        IWorldMutationSink? worldMutations,
        IMonotonicClock clock,
        ITimerService timers,
        IBoundedInbox<SessionCommand> controlInbox,
        IBoundedOutbox<SessionEvent> eventOutbox,
        ObservabilityServices observability,
        in SessionHostConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(transportControl);
        ArgumentNullException.ThrowIfNull(egress);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(controlInbox);
        ArgumentNullException.ThrowIfNull(eventOutbox);
        ArgumentNullException.ThrowIfNull(observability);

        return new SessionRegistry(
            slot,
            auth,
            transportControl,
            egress,
            worldMutations,
            clock,
            timers,
            controlInbox,
            eventOutbox,
            observability,
            in config);
    }

    /// <summary>Returns the optional test-only administrative port.</summary>
    public ISessionAdminPort? Admin => admin;

    /// <summary>Read-only session lookup used by composition and acceptance harnesses.</summary>
    public bool TryGet(ServerSessionId id, out ServerConnectionSession session)
    {
        lock (ownerGate)
        {
            return sessions.TryGetValue(id.Value, out session!);
        }
    }

    /// <summary>
    /// Processes the currently queued commands in FIFO order. A bounded pass
    /// prevents a command producer from starving the owner loop by self-enqueueing.
    /// </summary>
    public void PumpOnce()
    {
        BindOwnerThread();
        lock (ownerGate)
        {
            ownerPumpDepth++;
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);

                var budget = Math.Max(1, controlInbox.Budget.MaxItems);
                RetryPendingUnbinds(budget);
                RetryPendingReservationReleases(budget);
                RetryPendingTerminalCloses(budget);

                var processed = 0;
                while (processed++ < budget && ownerIngress.Count > 0)
                {
                    ProcessOwnerIngress(ownerIngress.Dequeue());
                }

                processed = 0;
                while (processed++ < budget && controlInbox.TryDequeue(out var command))
                {
                    Process(command);
                }

                // Dependency callbacks can re-enter while an admission command
                // is being reduced. Drain them only after that command reaches
                // its safe boundary, never from inside the callback itself.
                processed = 0;
                while (processed++ < budget && ownerIngress.Count > 0)
                {
                    ProcessOwnerIngress(ownerIngress.Dequeue());
                }
            }
            finally
            {
                ownerPumpDepth--;
            }
        }
    }

    /// <summary>
    /// Transport adapters hand events to the session owner through this method;
    /// the method only enqueues typed commands and never mutates transport state.
    /// </summary>
    public AckResult HandleConnectionEvent(in ConnectionEvent connectionEvent)
    {
        lock (ownerGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            switch (connectionEvent)
            {
                case ConnectionEvent.HandshakeEnvelope handshake:
                    var ownedHandshake = CopyHandshake(handshake);
                    var admission = Enqueue(new SessionCommand.ConnectionCandidate(
                        ownedHandshake.Id,
                        ownedHandshake.Epoch,
                        ownedHandshake.Envelope));
                    if (!admission.Accepted && admission.StableErrorId == "QueueFull")
                    {
                        _ = transportControl.TrySend(new ConnectionCommand.Close(
                            handshake.Id,
                            handshake.Epoch,
                            ConnectionCloseReason.PolicyReject));
                    }

                    return admission;
                case ConnectionEvent.Closed closed:
                    if (!IsCurrentConnectionEventGeneration(closed.Id, closed.Epoch))
                    {
                        return new AckResult(false, "StaleConnectionGeneration");
                    }

                    return EnqueueOwnerIngress(new OwnerIngress.ConnectionClosed(closed));
                case ConnectionEvent.Faulted faulted:
                    if (!IsCurrentConnectionEventGeneration(faulted.Id, faulted.Epoch))
                    {
                        return new AckResult(false, "StaleConnectionGeneration");
                    }

                    return EnqueueOwnerIngress(new OwnerIngress.ConnectionFaulted(faulted));
                default:
                    return new AckResult(true, null);
            }
        }
    }

    internal AckResult HandleAuthenticatedConnectionEvent(
        in ConnectionEvent.HandshakeEnvelope handshake,
        PrincipalId principal,
        string productId,
        string gameReleaseId)
    {
        lock (ownerGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var ownedHandshake = CopyHandshake(handshake);
            return EnqueueOwnerIngress(new OwnerIngress.AuthenticatedHandshake(
                ownedHandshake,
                principal,
                productId,
                gameReleaseId));
        }
    }

    private AckResult HandleAuthenticatedConnectionEventOnOwner(
        in ConnectionEvent.HandshakeEnvelope handshake,
        PrincipalId principal,
        string productId,
        string gameReleaseId)
    {
        lock (ownerGate)
        {
            var key = AuthenticatedConnectionKey(handshake.Id, handshake.Epoch);
            if (authenticatedConnections.ContainsKey(key))
            {
                return new AckResult(false, "SessionAntiReplay");
            }

            authenticatedConnections.Add(
                key,
                new AuthenticatedConnection(principal, productId, gameReleaseId));
            var ownedHandshake = CopyHandshake(handshake);
            var result = Enqueue(new SessionCommand.ConnectionCandidate(
                ownedHandshake.Id,
                ownedHandshake.Epoch,
                ownedHandshake.Envelope));
            if (!result.Accepted)
            {
                authenticatedConnections.Remove(key);
            }

            return result;
        }
    }

    /// <summary>Queues an already validated ingress frame for replication handling.</summary>
    public AckResult HandleInbound(in ValidatedEnvelopeBytes envelope)
    {
        lock (ownerGate)
        {
            if (!IsOwnerThread() || ownerPumpDepth > 0 || admissionSagaDepth > 0)
            {
                var copy = CopyEnvelope(in envelope);
                return EnqueueOwnerIngress(new OwnerIngress.InboundEnvelope(
                    null,
                    null,
                    copy));
            }

            return HandleInboundCore(null, null, in envelope);
        }
    }

    /// <summary>
    /// Handles an ingress frame when the transport adapter can provide its
    /// connection identity.  The overload keeps generation checking explicit;
    /// the legacy envelope-only entry point remains for adapters whose validated
    /// envelope already crossed the transport generation boundary.
    /// </summary>
    public AckResult HandleInbound(
        TransportConnectionId connectionId,
        ConnectionEpoch connectionEpoch,
        in ValidatedEnvelopeBytes envelope)
    {
        lock (ownerGate)
        {
            if (!IsOwnerThread() || ownerPumpDepth > 0 || admissionSagaDepth > 0)
            {
                var copy = CopyEnvelope(in envelope);
                return EnqueueOwnerIngress(new OwnerIngress.InboundEnvelope(
                    connectionId,
                    connectionEpoch,
                    copy));
            }

            return HandleInboundCore(connectionId, connectionEpoch, in envelope);
        }
    }

    private AckResult HandleInboundCore(
        TransportConnectionId? connectionId,
        ConnectionEpoch? connectionEpoch,
        in ValidatedEnvelopeBytes envelope)
    {
        EnsureOwnerThread();
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!sessions.TryGetValue(envelope.Header.SessionId, out var session))
        {
            return new AckResult(false, "SessionMismatch");
        }

        if (session.Binding is not { } binding)
        {
            return new AckResult(false, "StaleConnectionGeneration");
        }

        if (!connectionSessions.TryGetValue(binding.ConnectionId.Value, out var mappedSession)
            || !string.Equals(mappedSession, session.SessionId.Value, StringComparison.Ordinal))
        {
            return new AckResult(false, "StaleConnectionGeneration");
        }

        if ((connectionId is { } suppliedConnection && suppliedConnection != binding.ConnectionId)
            || (connectionEpoch is { } suppliedEpoch && suppliedEpoch != binding.ConnectionEpoch))
        {
            return new AckResult(false, "StaleConnectionGeneration");
        }

        if (HasPendingTerminalClose(binding.ConnectionId, binding.ConnectionEpoch))
        {
            return new AckResult(false, "ContextClosing");
        }

        var validation = MvpEnvelopeReader.Validate(envelope.Bytes.Span);
        if (validation.Status != EnvelopeParseStatus.Ok)
        {
            return new AckResult(false, validation.StableErrorId ?? "ManifestMalformed");
        }

        if (MvpEnvelopeReader.TryReadHeader(envelope.Bytes.Span, out var parsedHeader).Status != EnvelopeParseStatus.Ok
            || !HeaderMatches(envelope.Header, parsedHeader))
        {
            return new AckResult(false, "ManifestMalformed");
        }

        var permission = auth.EvaluateMessagePermission(new MvpPermissionGateRequest(
            SessionId: envelope.Header.SessionId,
            ProductId: envelope.Header.ProductId,
            GameReleaseId: envelope.Header.GameReleaseId,
            MessageId: envelope.Header.MessageType,
            Role: "Client",
            Claims: ImmutableArray<string>.Empty,
            ConnectionGeneration: binding.ConnectionEpoch.Value,
            AdmittedSessionId: session.SessionId.Value,
            AdmittedProductId: session.ProductId,
            AdmittedGameReleaseId: session.GameReleaseId,
            AdmittedRole: "Client",
            AdmittedClaims: ImmutableArray<string>.Empty,
            AdmittedConnectionGeneration: binding.ConnectionEpoch.Value));
        if (!permission.Accepted)
        {
            return new AckResult(false, NormalizeStableError(permission.StableErrorId, "MessagePermissionDenied"));
        }

        switch (envelope.Header.MessageType)
        {
            case "BaselineAck":
                var awaitingBaseline = session.State == ServerConnectionSessionState.Syncing
                    || (session.State == ServerConnectionSessionState.Active && !session.BaselineAcknowledged);
                if (!awaitingBaseline
                    || !TryReadBaselineAck(envelope.Bytes, out var snapshotId, out var confirmedRevision)
                    || confirmedRevision > authorityRevision
                    || !session.TryAcknowledgeBaseline(snapshotId, confirmedRevision))
                {
                    return new AckResult(false, "SnapshotBaseMismatch");
                }

                if (session.State == ServerConnectionSessionState.Syncing)
                {
                    SetState(session, ServerConnectionSessionState.Active);
                }

                return authorityRevision > confirmedRevision
                    ? SendDeltaOrIsolateOnQueueFull(session, binding, confirmedRevision)
                    : new AckResult(true, null);
            case "ResyncRequest":
                if (session.State != ServerConnectionSessionState.Active)
                {
                    return new AckResult(false, "SnapshotBaseMismatch");
                }

                return SendFullSnapshotOrIsolateOnQueueFull(session, binding);
            case "DeltaAck":
                if (session.State != ServerConnectionSessionState.Active)
                {
                    return new AckResult(false, "SnapshotBaseMismatch");
                }

                if (!TryReadDeltaAck(
                        envelope.Bytes,
                        out var confirmationSequence,
                        out var toRevision)
                    || toRevision > authorityRevision
                    || !session.TryAcknowledgeDelta(confirmationSequence, toRevision))
                {
                    return new AckResult(false, "SnapshotBaseMismatch");
                }

                return authorityRevision > toRevision
                    ? SendDeltaOrIsolateOnQueueFull(session, binding, toRevision)
                    : new AckResult(true, null);
            default:
                return new AckResult(false, "MessagePermissionDenied");
        }
    }

    /// <summary>
    /// Publishes an authority revision observed by the slot/runtime owner. The
    /// session layer treats it as opaque and only uses it for replication framing.
    /// </summary>
    public AckResult NotifyAuthorityRevision(ulong revision)
    {
        lock (ownerGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!IsOwnerThread() || ownerPumpDepth > 0 || admissionSagaDepth > 0)
            {
                return EnqueueOwnerIngress(new OwnerIngress.AuthorityRevision(revision));
            }

            return NotifyAuthorityRevisionOnOwner(revision);
        }
    }

    private AckResult NotifyAuthorityRevisionOnOwner(ulong revision)
    {
        lock (ownerGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (revision < authorityRevision)
            {
                return new AckResult(false, "RevisionConflict");
            }

            var changed = revision != authorityRevision;
            authorityRevision = revision;
            ResumeDeferredReconnects();
            foreach (var session in sessions.Values.ToArray())
            {
                if (changed)
                {
                    TraceState(session);
                }

                if (session.State == ServerConnectionSessionState.Active
                    && session.BaselineAcknowledged
                    && session.Binding is { } binding
                    && authorityRevision > session.LastSnapshotRevision)
                {
                    var delta = SendDelta(session, binding, session.LastSnapshotRevision);
                    if (!delta.Accepted)
                    {
                        if (delta.StableErrorId == "QueueFull")
                        {
                            var isolated = IsolateEgressBackpressure(session, binding);
                            if (!isolated.Accepted)
                            {
                                return isolated;
                            }

                            continue;
                        }

                        return delta;
                    }
                }
            }

            return new AckResult(true, null);
        }
    }

    /// <summary>Exposes the terminal reserve to an integration harness without a mutable registry view.</summary>
    internal bool TryDequeueTerminal(out SessionEvent sessionEvent)
    {
        if (terminalReserve.Count == 0)
        {
            sessionEvent = default!;
            return false;
        }

        sessionEvent = terminalReserve.Dequeue();
        return true;
    }

    /// <summary>Attaches the composition-root read lane for the unified FIFO view.</summary>
    internal void AttachEventInbox(IBoundedInbox<SessionEvent> inbox)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        eventInbox = inbox;
    }

    /// <summary>
    /// Dequeues the primary event prefix before the reserved tail. Once a terminal
    /// event spills, later events are routed to the same tail by <see cref="Publish"/>.
    /// </summary>
    internal bool TryDequeueEvent(out SessionEvent sessionEvent)
    {
        if (eventInbox is not null && eventInbox.TryDequeue(out sessionEvent!))
        {
            return true;
        }

        return TryDequeueTerminal(out sessionEvent!);
    }

    /// <summary>
    /// Once a terminal event spills into the reserve, later events must join the
    /// same FIFO tail until the reserve drains. The application uses this bit to
    /// drain the older primary lane before taking the reserved tail.
    /// </summary>
    internal bool HasPendingTerminalEvents => terminalReserve.Count > 0;

    internal int SessionCount => sessions.Count;

    internal ulong AuthorityRevision => authorityRevision;

    internal bool IsDraining => draining;

    internal IBoundedInbox<SessionCommand> ControlInboxForTest => controlInbox;

    internal AckResult Enqueue(in SessionCommand command)
    {
        var result = controlInbox.TryEnqueue(in command);
        if (result.Status == EnqueueStatus.Accepted)
        {
            return new AckResult(true, null);
        }

        if (result.Status == EnqueueStatus.Full)
        {
            return IsolateOnControlInboxFull(in command)
                ? new AckResult(true, null)
                : new AckResult(false, "QueueFull");
        }

        return new AckResult(false, "ContextClosing");
    }

    /// <summary>
    /// Records the local-fault contract gap without guessing an affected session.
    /// The frozen FaultAdjudicated event carries no session identity.
    /// </summary>
    internal void RecordUnroutableSessionFault(SlotEpoch epoch, HostFaultClass faultClass)
    {
        lock (ownerGate)
        {
            observability.Diagnostics.Write(
                "Diagnostic",
                "Error",
                $"session-local fault could not be isolated: affected-session identity is absent (fault={faultClass}, epoch={epoch.Value})");
        }
    }

    internal AckResult BeginDrain(MonotonicInstant graceDeadline)
    {
        lock (ownerGate)
        {
            SessionCommand command = new SessionCommand.BeginDrain(graceDeadline);
            var result = Enqueue(in command);
            return result;
        }
    }

    internal AckResult Kick(ServerSessionId sessionId, string reasonCode)
    {
        lock (ownerGate)
        {
            SessionCommand command = new SessionCommand.Kick(sessionId, reasonCode);
            var result = Enqueue(in command);
            return result;
        }
    }

    internal AckResult InjectWorldMutation(ServerSessionId onBehalfOf, ReadOnlyMemory<byte> opaqueCommand)
    {
        lock (ownerGate)
        {
            if (worldMutations is null)
            {
                return new AckResult(false, "ContextClosing");
            }

            var result = worldMutations.TryEnqueueOpaqueMutation(opaqueCommand);
            return result.Status switch
            {
                EnqueueStatus.Accepted => new AckResult(true, null),
                EnqueueStatus.Full => new AckResult(false, "QueueFull"),
                _ => new AckResult(false, "ContextClosing"),
            };
        }
    }

    public void Dispose()
    {
        lock (ownerGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            // Committed capacity belongs to this registry until the release is
            // accepted or represented by the bounded pending/dead-letter queues.
            foreach (var pair in committedReservationsBySession.ToArray())
            {
                var session = sessions.TryGetValue(pair.Key, out var active)
                    ? active
                    : null;
                QueueReservationRelease(
                    pair.Value,
                    new ServerSessionId(pair.Key),
                    session?.SlotEpoch ?? worldSlotEpoch,
                    committed: true);
            }

            RetryPendingReservationReleases(ReservationReleaseRetryLimit);
            foreach (var session in sessions.Values.ToArray())
            {
                if (session.Binding is { } binding)
                {
                    try
                    {
                        _ = transportControl.TrySend(new ConnectionCommand.Close(
                            binding.ConnectionId,
                            binding.ConnectionEpoch,
                            ConnectionCloseReason.OwnerRequest));
                    }
                    catch (Exception ex)
                    {
                        observability.Diagnostics.Write(
                            "Diagnostic",
                            "Error",
                            $"session disposal close failed: {ex.GetType().Name}");
                    }
                }
            }

            foreach (var deferred in deferredReconnects.Values)
            {
                try
                {
                    _ = transportControl.TrySend(new ConnectionCommand.Close(
                        deferred.Candidate.ConnectionId,
                        deferred.Candidate.ConnectionEpoch,
                        ConnectionCloseReason.OwnerRequest));
                }
                catch (Exception ex)
                {
                    observability.Diagnostics.Write(
                        "Diagnostic",
                        "Error",
                        $"deferred session disposal close failed: {ex.GetType().Name}");
                }
            }

            controlInbox.Close();
            authenticatedConnections.Clear();
            pendingTerminalCloses.Clear();
            ownerIngress.Clear();
        }
    }

    private void BindOwnerThread()
    {
        var current = Environment.CurrentManagedThreadId;
        var owner = Interlocked.CompareExchange(ref ownerThreadId, current, 0);
        if (owner != 0 && owner != current)
        {
            throw new InvalidOperationException("Session owner operations must run on the bound owner thread");
        }
    }

    private bool IsOwnerThread()
        => Volatile.Read(ref ownerThreadId) == Environment.CurrentManagedThreadId;

    private bool IsCurrentConnectionEventGeneration(
        TransportConnectionId connection,
        ConnectionEpoch epoch)
    {
        if (!connectionSessions.TryGetValue(connection.Value, out var sessionId)
            || !sessions.TryGetValue(sessionId, out var session)
            || session.Binding is not { } binding)
        {
            // An admission callback may arrive before the saga installs its
            // mapping; queue it and let the post-saga drain validate again.
            return true;
        }

        return binding.ConnectionEpoch == epoch;
    }

    private void EnsureOwnerThread()
    {
        if (!IsOwnerThread())
        {
            throw new InvalidOperationException("Session owner operations must run on the bound owner thread");
        }
    }

    // Cross-thread lane of MvpSessionControlInbox: same 256 budget and onFull
    // isolation (handshake closes; active session is isolated). Closed/Faulted
    // must be applied rather than dropped so a live binding cannot hang.
    private AckResult EnqueueOwnerIngress(OwnerIngress ingress)
    {
        if (ownerIngress.Count >= Math.Max(1, controlInbox.Budget.MaxItems))
        {
            return IsolateOnOwnerIngressFull(ingress);
        }

        ownerIngress.Enqueue(ingress);
        return new AckResult(true, null);
    }

    private bool IsolateOnControlInboxFull(in SessionCommand command)
    {
        switch (command)
        {
            case SessionCommand.Kick kick:
                ExecuteKick(kick);
                return true;
            case SessionCommand.TimerFired timer:
                ExecuteTimer(timer);
                return true;
            default:
                return false;
        }
    }

    private AckResult IsolateOnOwnerIngressFull(OwnerIngress ingress)
    {
        switch (ingress)
        {
            case OwnerIngress.ConnectionClosed closed:
                return HandleDisconnected(
                    closed.Event.Id,
                    closed.Event.Epoch,
                    closed.Event.Reason);
            case OwnerIngress.ConnectionFaulted faulted:
                return HandleDisconnected(
                    faulted.Event.Id,
                    faulted.Event.Epoch,
                    ConnectionCloseReason.Fault);
            case OwnerIngress.InboundEnvelope inbound:
                return IsolateInboundOverflow(inbound);
            case OwnerIngress.AuthenticatedHandshake handshake:
                _ = transportControl.TrySend(new ConnectionCommand.Close(
                    handshake.Event.Id,
                    handshake.Event.Epoch,
                    ConnectionCloseReason.PolicyReject));
                return new AckResult(false, "QueueFull");
            default:
                return new AckResult(false, "QueueFull");
        }
    }

    private AckResult IsolateInboundOverflow(OwnerIngress.InboundEnvelope inbound)
    {
        if (inbound.ConnectionId is { } connection
            && connectionSessions.TryGetValue(connection.Value, out var mapped)
            && sessions.TryGetValue(mapped, out var mappedSession)
            && mappedSession.Binding is { } mappedBinding)
        {
            return IsolateEgressBackpressure(mappedSession, mappedBinding);
        }

        if (sessions.TryGetValue(inbound.Envelope.Header.SessionId, out var session)
            && session.Binding is { } binding)
        {
            return IsolateEgressBackpressure(session, binding);
        }

        return new AckResult(false, "QueueFull");
    }

    private void ProcessOwnerIngress(OwnerIngress ingress)
    {
        switch (ingress)
        {
            case OwnerIngress.ConnectionClosed closed:
                _ = HandleDisconnected(
                    closed.Event.Id,
                    closed.Event.Epoch,
                    closed.Event.Reason);
                break;
            case OwnerIngress.ConnectionFaulted faulted:
                _ = HandleDisconnected(
                    faulted.Event.Id,
                    faulted.Event.Epoch,
                    ConnectionCloseReason.Fault);
                break;
            case OwnerIngress.AuthenticatedHandshake authenticated:
                var handshake = authenticated.Event;
                _ = HandleAuthenticatedConnectionEventOnOwner(
                    in handshake,
                    authenticated.Principal,
                    authenticated.ProductId,
                    authenticated.GameReleaseId);
                break;
            case OwnerIngress.InboundEnvelope inbound:
                var envelope = inbound.Envelope;
                _ = inbound.ConnectionId is { } connectionId
                    && inbound.ConnectionEpoch is { } connectionEpoch
                    ? HandleInboundCore(connectionId, connectionEpoch, in envelope)
                    : HandleInboundCore(null, null, in envelope);
                break;
            case OwnerIngress.AuthorityRevision revision:
                var revisionResult = NotifyAuthorityRevisionOnOwner(revision.Revision);
                if (!revisionResult.Accepted)
                {
                    var errorId = revisionResult.StableErrorId ?? "RevisionConflict";
                    observability.Diagnostics.Write(
                        "Diagnostic",
                        "Error",
                        $"authority revision synchronization rejected: {errorId}");
                    throw new InvalidOperationException(
                        $"Authority revision synchronization rejected: {errorId}");
                }

                break;
        }
    }

    private void Process(SessionCommand command)
    {
        var admission = command is SessionCommand.ConnectionCandidate;
        if (admission)
        {
            admissionSagaDepth++;
        }

        try
        {
            switch (command)
            {
                case SessionCommand.ConnectionCandidate candidate:
                    Admit(candidate);
                    break;
                case SessionCommand.DependencyResult dependency:
                    ProcessDependencyResult(dependency);
                    break;
                case SessionCommand.BeginDrain drain:
                    ExecuteBeginDrain(drain);
                    break;
                case SessionCommand.Kick kick:
                    ExecuteKick(kick);
                    break;
                case SessionCommand.TimerFired timer:
                    ExecuteTimer(timer);
                    break;
                case SessionCommand.SlotFaulted fault:
                    ExecuteSlotFault(fault);
                    break;
            }
        }
        finally
        {
            if (admission)
            {
                admissionSagaDepth--;
            }
        }
    }

    private void Admit(SessionCommand.ConnectionCandidate candidate)
    {
        var attempt = new AdmissionAttemptId(++nextAttempt);
        admissions[attempt.Value] = new AdmissionAttemptState(attempt, candidate);
        var header = candidate.Handshake.Header;
        var sessionIdText = string.IsNullOrWhiteSpace(header.SessionId)
            ? $"session-{candidate.ConnectionId.Value}"
            : header.SessionId;
        var sessionId = new ServerSessionId(sessionIdText);
        admissions[attempt.Value].SessionId = sessionId;

        if (!RecordAttempt(candidate.ConnectionId, attempt))
        {
            Reject(attempt, candidate, "QueueFull", close: true, traceCompensation: false);
            return;
        }

        SessionCommand input = candidate;
        var state = ServerConnectionSessionState.Admitted;
        var busyTries = 0;

        while (true)
        {
            var step = reducer.Advance(in state, in input);
            if (step.Effect is AdmissionEffectKind.Reject)
            {
                Reject(
                    attempt,
                    candidate,
                    step.StableErrorId ?? "InvalidArgument",
                    close: true,
                    traceCompensation: false);
                return;
            }

            if (step.Effect is AdmissionEffectKind.Compensate)
            {
                var reason = NormalizeStableError(step.StableErrorId, "QueueFull");
                var closeEpoch = Compensate(attempt, candidate, session: null, boundEpoch: null);
                Reject(
                    attempt,
                    candidate,
                    reason,
                    close: reason != "ReleaseMismatch",
                    traceCompensation: false,
                    closeEpoch: closeEpoch);
                return;
            }

            if (step.Effect is AdmissionEffectKind.None)
            {
                CompleteSuccessfulAdmission(attempt);
                return;
            }

            var tracked = admissions[attempt.Value];
            var slotEpoch = tracked.Reservation.Value == 0 ? (SlotEpoch?)null : tracked.SlotEpoch;
            var connectionEpoch = step.Effect == AdmissionEffectKind.StartReplication && tracked.TransportBound
                ? tracked.BoundEpoch
                : candidate.ConnectionEpoch;
            TraceAck(step.Effect, attempt, slotEpoch, connectionEpoch);

            var io = ExecuteAdmissionEffect(step.Effect, attempt, candidate, sessionId);
            if (io.Diverted)
            {
                return;
            }

            if (io.Busy)
            {
                busyTries++;
                if (busyTries < config.AdmissionAttemptBudget)
                {
                    continue;
                }

                io = EffectIo.Fail("QueueFull");
            }

            input = new SessionCommand.DependencyResult(
                attempt,
                step.Effect,
                io.Accepted,
                io.StableErrorId);
        }
    }

    private EffectIo ExecuteAdmissionEffect(
        AdmissionEffectKind effect,
        AdmissionAttemptId attempt,
        SessionCommand.ConnectionCandidate candidate,
        ServerSessionId sessionId)
    {
        var tracked = admissions[attempt.Value];
        switch (effect)
        {
            case AdmissionEffectKind.ReadGate:
                if (draining || slot.Gate != AdmissionGateState.Open || auth.AdmissionMustStop)
                {
                    return EffectIo.Fail("ContextClosing");
                }

                if (TryGet(sessionId, out var existing))
                {
                    if (existing.State == ServerConnectionSessionState.ReconnectWindow)
                    {
                        Reconnect(existing, candidate, attempt);
                        return EffectIo.Stop();
                    }

                    if (existing.State != ServerConnectionSessionState.Kicked)
                    {
                        SendErrorAndClose(candidate, "SessionMismatch");
                        Reject(attempt, candidate, "SessionMismatch", close: false, traceCompensation: false);
                        return EffectIo.Stop();
                    }
                }

                return EffectIo.Ok();
            case AdmissionEffectKind.Authenticate:
                if (candidate.Handshake.Header.MessageType != "Handshake"
                    || !IsHandshakeClient(candidate.Handshake))
                {
                    return EffectIo.Fail("RoleMismatch");
                }

                var authentication = Authenticate(candidate, sessionId);
                if (IsTransientBusy(authentication.ReasonCode))
                {
                    return EffectIo.Retry();
                }

                if (!authentication.Accepted)
                {
                    return EffectIo.Fail(NormalizeStableError(authentication.ReasonCode, "RoleMismatch"));
                }

                tracked.Principal = authentication.Principal;
                return EffectIo.Ok();
            case AdmissionEffectKind.MatchExactRelease:
                if (!ExactRelease(candidate.Handshake.Header))
                {
                    SendErrorAndClose(candidate, "ReleaseMismatch");
                    return EffectIo.Fail("ReleaseMismatch");
                }

                return EffectIo.Ok();
            case AdmissionEffectKind.ReserveSlot:
                var allocation = ReserveSlot();
                if (!allocation.Allocated)
                {
                    return IsTransientBusy(allocation.StableErrorId)
                        ? EffectIo.Retry()
                        : EffectIo.Fail(NormalizeStableError(allocation.StableErrorId, "ContextClosing"));
                }

                var quota = slot.Capacity;
                if (quota.MaxSessions > 0 && quota.BoundSessions >= quota.MaxSessions)
                {
                    return EffectIo.Fail("CapacityExceeded");
                }

                worldSlotId = allocation.SlotId;
                worldSlotEpoch = allocation.Epoch;
                worldSlotAllocated = true;
                var reservationResult = ReserveAdmission(attempt, sessionId, allocation);
                if (!reservationResult.Reserved || reservationResult.Reservation.Value == 0)
                {
                    return IsTransientBusy(reservationResult.StableErrorId)
                        ? EffectIo.Retry()
                        : EffectIo.Fail(NormalizeStableError(reservationResult.StableErrorId, "InvalidArgument"));
                }

                if (reservationResult.Epoch != allocation.Epoch)
                {
                    return EffectIo.Fail("StaleEpoch");
                }

                tracked.Reservation = reservationResult.Reservation;
                tracked.SlotEpoch = reservationResult.Epoch;
                return EffectIo.Ok();
            case AdmissionEffectKind.CommitSlot:
                var commit = this.admissionPort is { } admission
                    ? admission.BindSession(tracked.Reservation, sessionId, tracked.SlotEpoch)
                    : new AckResult(false, "InvalidArgument");
                if (!commit.Accepted)
                {
                    return EffectIo.Fail(NormalizeStableError(commit.StableErrorId, "CapacityExceeded"));
                }

                tracked.SlotCommitted = true;
                tracked.ReleaseCommittedOnCompensation = true;
                return EffectIo.Ok();
            case AdmissionEffectKind.CreateSession:
                if (sessions.ContainsKey(sessionId.Value))
                {
                    return EffectIo.Fail("SessionMismatch");
                }

                var session = new ServerConnectionSession(
                    sessionId,
                    new SessionEpoch(0),
                    config.ProductId,
                    config.GameReleaseId);
                var grant = auth.Authorize(
                    tracked.Principal,
                    new SessionScope(sessionId, config.ProductId, config.GameReleaseId, "Client"));
                session.SetPrincipal(tracked.Principal);
                session.Associate(worldSlotId, tracked.SlotEpoch);
                tracked.ReplicationContext = new ReplicationContextHandle(++nextContext);
                var grantRef = GrantReference(grant, attempt);
                sessions.Add(sessionId.Value, session);
                committedReservationsBySession[sessionId.Value] = tracked.Reservation;
                tracked.Session = session;
                tracked.Grant = grantRef;
                return EffectIo.Ok();
            case AdmissionEffectKind.BindConnection:
                var bindResult = transportControl.TrySend(new ConnectionCommand.Bind(
                    candidate.ConnectionId,
                    candidate.ConnectionEpoch,
                    tracked.Grant,
                    sessionId));
                if (bindResult.Status != EnqueueStatus.Accepted)
                {
                    return EffectIo.Fail(NormalizeStableError(bindResult.StableErrorId, "StaleConnectionGeneration"));
                }

                tracked.TransportBound = true;
                tracked.BoundEpoch = new ConnectionEpoch(candidate.ConnectionEpoch.Value + 1);
                var activeBinding = new SessionBinding(
                    candidate.ConnectionId,
                    tracked.BoundEpoch,
                    tracked.Grant,
                    worldSlotId,
                    tracked.SlotEpoch);
                tracked.Session!.Bind(activeBinding, tracked.ReplicationContext);
                connectionSessions[candidate.ConnectionId.Value] = sessionId.Value;
                return EffectIo.Ok();
            case AdmissionEffectKind.StartReplication:
                var snapshot = SendFullSnapshot(tracked.Session!, tracked.Session!.Binding!.Value);
                return snapshot.Accepted
                    ? EffectIo.Ok()
                    : EffectIo.Fail(NormalizeStableError(snapshot.StableErrorId, "QueueFull"));
            default:
                return EffectIo.Fail("InvalidArgument");
        }
    }

    private void CompleteSuccessfulAdmission(AdmissionAttemptId attempt)
    {
        if (!admissions.TryGetValue(attempt.Value, out var tracked) || tracked.Session is not { } session)
        {
            return;
        }

        SetState(session, ServerConnectionSessionState.Syncing);
        Publish(new SessionEvent.Admitted(session.SessionId, session.SessionEpoch, session.Binding!.Value));
        admissions.Remove(attempt.Value);
    }

    private void Reconnect(ServerConnectionSession session, SessionCommand.ConnectionCandidate candidate, AdmissionAttemptId attempt)
    {
        if (deferredReconnects.ContainsKey(session.SessionId.Value))
        {
            Reject(attempt, candidate, "SessionMismatch", close: true, traceCompensation: false);
            return;
        }

        if (candidate.Handshake.Header.MessageType != "Handshake"
            || !IsHandshakeClient(candidate.Handshake))
        {
            Reject(attempt, candidate, "RoleMismatch", close: true, traceCompensation: false);
            return;
        }

        if (session.Binding is not null)
        {
            Reject(attempt, candidate, "SessionMismatch", close: true, traceCompensation: false);
            return;
        }

        TraceAck(AdmissionEffectKind.Authenticate, attempt, session.SlotEpoch, candidate.ConnectionEpoch);
        var authentication = Authenticate(candidate, session);
        if (!authentication.Accepted)
        {
            Reject(attempt, candidate, NormalizeStableError(authentication.ReasonCode, "RoleMismatch"), close: true, traceCompensation: false);
            return;
        }

        if (session.Principal is not { } expectedPrincipal
            || authentication.Principal != expectedPrincipal)
        {
            Reject(attempt, candidate, "SessionMismatch", close: true, traceCompensation: false);
            return;
        }

        TraceAck(AdmissionEffectKind.MatchExactRelease, attempt, session.SlotEpoch, candidate.ConnectionEpoch);
        if (!ExactRelease(candidate.Handshake.Header))
        {
            SendErrorAndClose(candidate, "ReleaseMismatch");
            Reject(attempt, candidate, "ReleaseMismatch", close: false, traceCompensation: false);
            return;
        }

        if (session.LastSentDeltaRevision is { } lastDeltaRevision
            && authorityRevision <= lastDeltaRevision)
        {
            admissions.Remove(attempt.Value);
            deferredReconnects.Add(
                session.SessionId.Value,
                new DeferredReconnect(attempt, candidate));
            return;
        }

        var grant = auth.Authorize(
            authentication.Principal,
            new SessionScope(session.SessionId, config.ProductId, config.GameReleaseId, "Client"));
        CompleteReconnect(session, candidate, attempt, grant);
    }

    private void CompleteReconnect(
        ServerConnectionSession session,
        SessionCommand.ConnectionCandidate candidate,
        AdmissionAttemptId attempt,
        PermissionGrant grant)
    {

        TraceAck(AdmissionEffectKind.ReserveSlot, attempt, session.SlotEpoch, candidate.ConnectionEpoch);
        TraceAck(AdmissionEffectKind.CommitSlot, attempt, session.SlotEpoch, candidate.ConnectionEpoch);
        TraceAck(AdmissionEffectKind.CreateSession, attempt, session.SlotEpoch, candidate.ConnectionEpoch);
        TraceAck(AdmissionEffectKind.BindConnection, attempt, session.SlotEpoch, candidate.ConnectionEpoch);

        var grantRef = GrantReference(grant, attempt);
        var bind = transportControl.TrySend(new ConnectionCommand.Bind(
            candidate.ConnectionId,
            candidate.ConnectionEpoch,
            grantRef,
            session.SessionId));
        if (bind.Status != EnqueueStatus.Accepted)
        {
            Reject(attempt, candidate, NormalizeStableError(bind.StableErrorId, "StaleConnectionGeneration"), close: true, traceCompensation: true);
            return;
        }

        admissions[attempt.Value] = new AdmissionAttemptState(attempt, candidate)
        {
            SessionId = session.SessionId,
            Session = session,
            SlotEpoch = session.SlotEpoch,
            Grant = grantRef,
            SlotCommitted = true,
            ReleaseCommittedOnCompensation = false,
            TransportBound = true,
            BoundEpoch = new ConnectionEpoch(candidate.ConnectionEpoch.Value + 1),
        };

        if (session.PendingTimer is { } timer)
        {
            _ = timers.Cancel(timer);
            session.PendingTimer = null;
            session.PendingTimerToken = null;
        }

        session.AdvanceSessionEpoch();
        var activeBinding = new SessionBinding(
            candidate.ConnectionId,
            new ConnectionEpoch(candidate.ConnectionEpoch.Value + 1),
            grantRef,
            session.Slot,
            session.SlotEpoch);
        session.Bind(
            activeBinding,
            session.ReplicationContext ?? new ReplicationContextHandle(++nextContext));
        admissions[attempt.Value].ReleaseCommittedOnCompensation = true;
        connectionSessions[candidate.ConnectionId.Value] = session.SessionId.Value;
        TraceAck(AdmissionEffectKind.StartReplication, attempt, session.SlotEpoch, activeBinding.ConnectionEpoch);
        SetState(session, ServerConnectionSessionState.Syncing);
        var snapshot = SendFullSnapshot(session, activeBinding);
        if (!snapshot.Accepted)
        {
            var closeEpoch = Compensate(attempt, candidate, session, activeBinding.ConnectionEpoch);
            session.ClearConnectionBinding();
            SetState(session, ServerConnectionSessionState.Closed);
            Reject(
                attempt,
                candidate,
                NormalizeStableError(snapshot.StableErrorId, "QueueFull"),
                close: true,
                traceCompensation: false,
                closeEpoch: closeEpoch);
            return;
        }

        Publish(new SessionEvent.Reconnected(session.SessionId, session.SessionEpoch, activeBinding));
        admissions.Remove(attempt.Value);
    }

    private AuthenticateResult Authenticate(SessionCommand.ConnectionCandidate candidate, ServerSessionId sessionId)
    {
        var authenticatedKey = AuthenticatedConnectionKey(
            candidate.ConnectionId,
            candidate.ConnectionEpoch);
        if (authenticatedConnections.Remove(authenticatedKey, out var authenticated))
        {
            if (string.IsNullOrWhiteSpace(authenticated.Principal.Value))
            {
                return new AuthenticateResult(false, "RoleMismatch", default);
            }

            if (!string.Equals(
                    authenticated.ProductId,
                    config.ProductId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    authenticated.GameReleaseId,
                    config.GameReleaseId,
                    StringComparison.Ordinal))
            {
                return new AuthenticateResult(false, "ReleaseMismatch", default);
            }

            return new AuthenticateResult(true, null, authenticated.Principal);
        }

        using var credential = new OpaqueCredentialInput(
            candidate.Handshake.Bytes.IsEmpty ? Array.Empty<byte>() : candidate.Handshake.Bytes.ToArray());
        var context = new VerificationContext(
            ProductId: candidate.Handshake.Header.ProductId,
            GameReleaseId: candidate.Handshake.Header.GameReleaseId,
            Nonce: candidate.Handshake.Header.TraceId,
            ReceivedAt: clock.Now);
        var outcome = auth.Authenticate(new AuthenticateCommand(
            new AuthRequestId(++nextAttempt),
            candidate.ConnectionId,
            candidate.ConnectionEpoch,
            credential,
            context));

        if (IsTransientBusy(outcome.StableErrorId))
        {
            return new AuthenticateResult(false, outcome.StableErrorId, default);
        }

        if (outcome.Verdict != CredentialVerdict.Accepted || outcome.AntiReplay != AntiReplayVerdict.Ok)
        {
            var reason = outcome.AntiReplay != AntiReplayVerdict.Ok
                ? "SessionAntiReplay"
                : outcome.StableErrorId;
            return new AuthenticateResult(false, reason, default);
        }

        return new AuthenticateResult(true, null, outcome.Principal);
    }

    private AuthenticateResult Authenticate(SessionCommand.ConnectionCandidate candidate, ServerConnectionSession session)
        => Authenticate(candidate, session.SessionId);

    private void ProcessDependencyResult(SessionCommand.DependencyResult dependency)
    {
        if (!dependency.Accepted)
        {
            if (admissions.TryGetValue(dependency.Attempt.Value, out var admission))
            {
                // A dependency can report the same failure more than once.  The
                // compensation guard makes the first terminal result authoritative.
                if (!compensatedAttempts.Contains(dependency.Attempt.Value))
                {
                    var closeEpoch = Compensate(
                        dependency.Attempt,
                        candidate: null,
                        session: null,
                        boundEpoch: null);
                    Reject(
                        dependency.Attempt,
                        admission.Candidate,
                        NormalizeStableError(dependency.StableErrorId, "QueueFull"),
                        close: true,
                        traceCompensation: false,
                        closeEpoch: closeEpoch);
                }
            }

            return;
        }

        var step = reducer.Advance(
            ServerConnectionSessionState.Admitted,
            dependency);
        TraceAck(step.Effect, dependency.Attempt, null, null);
    }

    private void ExecuteBeginDrain(SessionCommand.BeginDrain command)
    {
        draining = true;
        var epoch = worldSlotAllocated
            ? worldSlotEpoch
            : sessions.Values.Select(s => s.SlotEpoch).FirstOrDefault();
        var quiesce = slot.Quiesce("MaintenanceDrain", epoch);
        if (!quiesce.Accepted)
        {
            observability.Diagnostics.Write(
                "Diagnostic",
                "Error",
                $"world-slot quiesce rejected: {NormalizeStableError(quiesce.StableErrorId, "InternalInvariant")}");
            throw new InvalidOperationException("World-slot quiesce was rejected");
        }

        foreach (var session in sessions.Values.ToArray())
        {
            if (session.State is ServerConnectionSessionState.Closed
                or ServerConnectionSessionState.Kicked
                or ServerConnectionSessionState.Expired)
            {
                continue;
            }

            if (session.PendingTimer is { } timer)
            {
                _ = timers.Cancel(timer);
                session.PendingTimer = null;
                session.PendingTimerToken = null;
            }

            CancelDeferredReconnect(session, "ContextClosing");

            if (session.Binding is { } binding)
            {
                if (!HasPendingTerminalClose(binding.ConnectionId, binding.ConnectionEpoch))
                {
                    _ = transportControl.TrySend(new ConnectionCommand.SetDrain(
                        binding.ConnectionId,
                        binding.ConnectionEpoch,
                        true));
                    _ = transportControl.TrySend(new ConnectionCommand.Close(
                        binding.ConnectionId,
                        binding.ConnectionEpoch,
                        ConnectionCloseReason.OwnerRequest));
                }

                connectionSessions.Remove(binding.ConnectionId.Value);
                session.ClearConnectionBinding();
            }

            ReleaseCommittedReservation(session);

            SetState(session, ServerConnectionSessionState.Closed);
            Publish(new SessionEvent.Drained(session.SessionId, session.SessionEpoch));
        }
    }

    private void ExecuteKick(SessionCommand.Kick command)
    {
        if (!sessions.TryGetValue(command.SessionId.Value, out var session))
        {
            return;
        }

        if (session.State is ServerConnectionSessionState.Kicked
            or ServerConnectionSessionState.Closed
            or ServerConnectionSessionState.Expired)
        {
            return;
        }

        if (session.Binding is { } binding)
        {
            var envelope = BuildMaintenanceKick(session, command.RegisteredReasonCode);
            EnqueueTerminalEnvelopeThenClose(
                binding.ConnectionId,
                binding.ConnectionEpoch,
                new OutboundEnvelopeBytes(envelope),
                ConnectionCloseReason.MaintenanceKick);
        }

        FinalizeKick(session, command.RegisteredReasonCode);
    }

    private void FinalizeKick(ServerConnectionSession session, string reason)
    {
        if (session.State is ServerConnectionSessionState.Kicked
            or ServerConnectionSessionState.Closed
            or ServerConnectionSessionState.Expired)
        {
            DetachLiveConnection(session);
            return;
        }

        if (session.PendingTimer is { } pendingTimer)
        {
            _ = timers.Cancel(pendingTimer);
            session.PendingTimer = null;
            session.PendingTimerToken = null;
        }

        CancelDeferredReconnect(session, "SessionMismatch");
        ReleaseCommittedReservation(session);
        DetachLiveConnection(session);
        SetState(session, ServerConnectionSessionState.Kicked);
        Publish(new SessionEvent.Kicked(session.SessionId, session.SessionEpoch, reason));
    }

    private void DetachLiveConnection(ServerConnectionSession session)
    {
        if (session.Binding is not { } binding)
        {
            return;
        }

        connectionSessions.Remove(binding.ConnectionId.Value);
        session.ClearConnectionBinding();
    }

    private void ExecuteTimer(SessionCommand.TimerFired command)
    {
        if (!sessions.TryGetValue(command.SessionId.Value, out var session)
            || session.State != ServerConnectionSessionState.ReconnectWindow
            || session.PendingTimer is null
            || session.PendingTimerToken != command.Timer)
        {
            return;
        }

        session.PendingTimer = null;
        session.PendingTimerToken = null;
        CancelDeferredReconnect(session, "SessionMismatch");
        SetState(session, ServerConnectionSessionState.Expired);
        session.ClearReplicationContext();
        ReleaseCommittedReservation(session);
        foreach (var connection in connectionSessions
            .Where(pair => pair.Value == session.SessionId.Value)
            .Select(pair => pair.Key)
            .ToArray())
        {
            connectionSessions.Remove(connection);
        }
    }

    private void ExecuteSlotFault(SessionCommand.SlotFaulted command)
    {
        // The published FaultAdjudicated event carries slot/epoch only, not an
        // affected session identity. Never infer one (or take unrelated sessions
        // down); leave a stable diagnostic for the upstream contract blocker.
        RecordUnroutableSessionFault(command.Epoch, command.FaultClass);
    }

    private AckResult HandleDisconnected(TransportConnectionId connection, ConnectionEpoch epoch, ConnectionCloseReason reason)
    {
        authenticatedConnections.Remove(AuthenticatedConnectionKey(connection, epoch));
        attemptsByConnection.Remove(ConnectionKey(connection));
        if (!connectionSessions.TryGetValue(connection.Value, out var id))
        {
            var deferred = deferredReconnects.FirstOrDefault(pair =>
                pair.Value.Candidate.ConnectionId == connection
                && pair.Value.Candidate.ConnectionEpoch == epoch);
            if (deferred.Value is not null)
            {
                deferredReconnects.Remove(deferred.Key);
                admissions.Remove(deferred.Value.Attempt.Value);
            }

            // A transport close for a connection that never reached admission
            // is already terminal and therefore idempotently acknowledged.
            return new AckResult(true, null);
        }

        if (!sessions.TryGetValue(id, out var session)
            || session.Binding is not { } binding)
        {
            if (!HasPendingUnbind(connection, epoch))
            {
                connectionSessions.Remove(connection.Value);
            }
            return new AckResult(true, null);
        }

        if (binding.ConnectionEpoch != epoch)
        {
            return new AckResult(false, "StaleConnectionGeneration");
        }

        if (reason == ConnectionCloseReason.MaintenanceKick
            || session.State == ServerConnectionSessionState.Kicked)
        {
            FinalizeKick(session, "MaintenanceKick");
            return new AckResult(true, null);
        }

        if (session.State is ServerConnectionSessionState.Closed
            or ServerConnectionSessionState.Expired)
        {
            DetachLiveConnection(session);
            return new AckResult(true, null);
        }

        session.ClearConnectionBinding();
        connectionSessions.Remove(connection.Value);
        SetState(session, ServerConnectionSessionState.ReconnectWindow);
        var due = new MonotonicInstant(
            clock.Now.Ticks + TimeSpan.FromSeconds(config.ReconnectWindowSeconds).Ticks);
        var timerToken = new TimerId(++nextReconnectTimerToken);
        var timerCommand = new SessionCommand.TimerFired(timerToken, session.SessionId);
        var timer = timers.Schedule(due, controlInbox, timerCommand);
        session.PendingTimer = timer;
        session.PendingTimerToken = timerToken;
        Publish(new SessionEvent.Disconnected(session.SessionId, session.SessionEpoch));
        return new AckResult(true, null);
    }

    private void ResumeDeferredReconnects()
    {
        foreach (var pair in deferredReconnects.ToArray())
        {
            if (!sessions.TryGetValue(pair.Key, out var session)
                || session.State != ServerConnectionSessionState.ReconnectWindow)
            {
                deferredReconnects.Remove(pair.Key);
                admissions.Remove(pair.Value.Attempt.Value);
                continue;
            }

            if (session.LastSentDeltaRevision is { } lastDeltaRevision
                && authorityRevision <= lastDeltaRevision)
            {
                continue;
            }

            deferredReconnects.Remove(pair.Key);
            if (session.Principal is not { } principal)
            {
                Reject(
                    pair.Value.Attempt,
                    pair.Value.Candidate,
                    "SessionMismatch",
                    close: true,
                    traceCompensation: false);
                continue;
            }

            var grant = auth.Authorize(
                principal,
                new SessionScope(session.SessionId, config.ProductId, config.GameReleaseId, "Client"));
            CompleteReconnect(session, pair.Value.Candidate, pair.Value.Attempt, grant);
        }
    }

    private void CancelDeferredReconnect(ServerConnectionSession session, string reason)
    {
        if (!deferredReconnects.Remove(session.SessionId.Value, out var deferred))
        {
            return;
        }

        SendErrorAndClose(deferred.Candidate, reason);
        admissions.Remove(deferred.Attempt.Value);
    }

    private bool RecordAttempt(TransportConnectionId connection, AdmissionAttemptId attempt)
    {
        var key = ConnectionKey(connection);
        attemptsByConnection.TryGetValue(key, out var count);
        count++;
        attemptsByConnection[key] = count;
        return count <= config.AdmissionAttemptBudget;
    }

    private static string ConnectionKey(TransportConnectionId connection)
        => connection.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string AuthenticatedConnectionKey(
        TransportConnectionId connection,
        ConnectionEpoch epoch)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{connection.Value}:{epoch.Value}");

    private AllocateResult ReserveSlot()
    {
        if (worldSlotAllocated)
        {
            return new AllocateResult(true, worldSlotId, worldSlotEpoch, null);
        }

        var capacity = slot.Capacity;
        var budget = new SlotBudget(
            MaxSessions: Math.Max(1, capacity.MaxSessions),
            MaxIngressItemsPerTick: SessionProvisionalDefaults.ControlInboxMaxItems,
            MaxIngressBytesPerTick: 256L * 1024L);
        var allocation = slot.Allocate(in budget);
        if (allocation.Allocated)
        {
            return allocation;
        }

        return allocation;
    }

    private SessionReservationResult ReserveAdmission(
        AdmissionAttemptId attempt,
        ServerSessionId session,
        in AllocateResult allocation)
    {
        if (this.admissionPort is { } admission)
        {
            return admission.ReserveAdmission(attempt, session);
        }

        // A reservation must come from the serialized WorldSlot admission
        // operation.  Never derive one from the attempt id or another local
        // value when an adapter does not expose that capability.
        return new SessionReservationResult(false, default, allocation.Epoch, allocation.SlotId, "InvalidArgument");
    }

    private bool ExactRelease(in EnvelopeHeaderView header)
        => string.Equals(header.ProductId, config.ProductId, StringComparison.Ordinal)
            && string.Equals(header.GameReleaseId, config.GameReleaseId, StringComparison.Ordinal);

    private static bool IsHandshakeClient(in ValidatedEnvelopeBytes envelope)
    {
        if (envelope.Bytes.IsEmpty)
        {
            return true;
        }

        try
        {
            var node = JsonNode.Parse(envelope.Bytes.ToArray());
            var role = (node?["body"] as JsonObject)?["role"]?.GetValue<string>();
            return string.Equals(role, "Client", StringComparison.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool HeaderMatches(in EnvelopeHeaderView expected, in EnvelopeHeaderView actual)
        => expected.ProtocolVersion == actual.ProtocolVersion
            && expected.Sequence == actual.Sequence
            && string.Equals(expected.SessionId, actual.SessionId, StringComparison.Ordinal)
            && string.Equals(expected.ProductId, actual.ProductId, StringComparison.Ordinal)
            && string.Equals(expected.GameReleaseId, actual.GameReleaseId, StringComparison.Ordinal)
            && string.Equals(expected.MessageType, actual.MessageType, StringComparison.Ordinal)
            && string.Equals(expected.Reliability, actual.Reliability, StringComparison.Ordinal)
            && string.Equals(expected.TraceId, actual.TraceId, StringComparison.Ordinal)
            && expected.WireByteLength == actual.WireByteLength;

    private static bool TryReadBaselineAck(
        ReadOnlyMemory<byte> bytes,
        out string snapshotId,
        out ulong confirmedRevision)
    {
        snapshotId = string.Empty;
        confirmedRevision = 0;

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var body = document.RootElement.GetProperty("body");
            snapshotId = body.GetProperty("snapshotId").GetString() ?? string.Empty;
            confirmedRevision = body.GetProperty("confirmedRevision").GetUInt64();
            return snapshotId.Length != 0;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    private static bool TryReadDeltaAck(
        ReadOnlyMemory<byte> bytes,
        out ulong confirmationSequence,
        out ulong toRevision)
    {
        confirmationSequence = 0;
        toRevision = 0;

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var body = document.RootElement.GetProperty("body");
            confirmationSequence = body.GetProperty("confirmationSequence").GetUInt64();
            toRevision = body.GetProperty("toRevision").GetUInt64();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    private static bool IsTransientBusy(string? error)
        => error is "AuthBusy" or "AggregateBusy";

    private static string NormalizeStableError(string? error, string fallback)
        => error is "AuthBusy" or "AggregateBusy"
            ? "QueueFull"
            : string.IsNullOrWhiteSpace(error) ? fallback : error;

    private static ValidatedEnvelopeBytes CopyEnvelope(in ValidatedEnvelopeBytes envelope)
    {
        var bytes = envelope.Bytes.ToArray();
        var header = envelope.Header;
        if (MvpEnvelopeReader.TryReadHeader(bytes, out var parsed).Status == EnvelopeParseStatus.Ok)
        {
            header = parsed;
        }

        return new ValidatedEnvelopeBytes(bytes, header);
    }

    private static ConnectionEvent.HandshakeEnvelope CopyHandshake(
        in ConnectionEvent.HandshakeEnvelope handshake)
    {
        var source = handshake.Envelope;
        var envelope = CopyEnvelope(in source);
        return new ConnectionEvent.HandshakeEnvelope(handshake.Id, handshake.Epoch, envelope);
    }

    private static bool RememberAttempt(HashSet<ulong> retained, Queue<ulong> order, ulong attempt)
    {
        if (!retained.Add(attempt))
        {
            return false;
        }

        order.Enqueue(attempt);
        while (order.Count > SessionProvisionalDefaults.ControlInboxMaxItems)
        {
            retained.Remove(order.Dequeue());
        }

        return true;
    }

    private static PermissionGrantRef GrantReference(PermissionGrant grant, AdmissionAttemptId attempt)
        => new(grant.Epoch.Value == 0 ? attempt.Value : grant.Epoch.Value);

    private void Reject(
        AdmissionAttemptId attempt,
        SessionCommand.ConnectionCandidate candidate,
        string reason,
        bool close,
        bool traceCompensation,
        ConnectionEpoch? closeEpoch = null)
    {
        authenticatedConnections.Remove(AuthenticatedConnectionKey(
            candidate.ConnectionId,
            candidate.ConnectionEpoch));
        if (!RememberAttempt(rejectedAttempts, rejectedAttemptOrder, attempt.Value))
        {
            return;
        }

        reason = NormalizeStableError(reason, "ContextClosing");

        if (traceCompensation)
        {
            closeEpoch = Compensate(attempt, candidate, session: null, boundEpoch: null)
                ?? closeEpoch;
        }

        Publish(new SessionEvent.Rejected(attempt, candidate.ConnectionId, reason));
        if (close)
        {
            _ = transportControl.TrySend(new ConnectionCommand.Close(
                candidate.ConnectionId,
                closeEpoch ?? candidate.ConnectionEpoch,
                ConnectionCloseReason.PolicyReject));
        }

        attemptsByConnection.Remove(ConnectionKey(candidate.ConnectionId));
        admissions.Remove(attempt.Value);
    }

    private void SendErrorAndClose(SessionCommand.ConnectionCandidate candidate, string reason)
    {
        var context = new EnvelopeWriteContext(
            string.IsNullOrWhiteSpace(candidate.Handshake.Header.SessionId)
                ? $"session-{candidate.ConnectionId.Value}"
                : candidate.Handshake.Header.SessionId,
            config.ProductId,
            config.GameReleaseId,
            ++outboundSequence,
            $"trace-session-error-{outboundSequence}",
            MvpWireConstants.Reliability,
            MvpWireConstants.MaxMessageBytes,
            MvpWireConstants.MaxFragmentBytes,
            MvpWireConstants.AntiReplayWindow,
            MvpWireConstants.AuthBinding,
            MvpWireConstants.TransportErrorClass);
        var bytes = MvpEnvelopeWriter.WriteError(context, "Rejectable", reason);
        EnqueueTerminalEnvelopeThenClose(
            candidate.ConnectionId,
            candidate.ConnectionEpoch,
            new OutboundEnvelopeBytes(bytes),
            ConnectionCloseReason.PolicyReject);
    }

    private void EnqueueTerminalEnvelopeThenClose(
        TransportConnectionId connection,
        ConnectionEpoch epoch,
        OutboundEnvelopeBytes envelope,
        ConnectionCloseReason reason)
    {
        if (HasPendingTerminalClose(connection, epoch))
        {
            return;
        }

        var pending = new PendingTerminalClose(connection, epoch, envelope, reason);
        var result = egress.TryEnqueue(connection, epoch, in envelope);
        if (result.Status == EnqueueStatus.Accepted)
        {
            pending.EnvelopeQueued = true;
            if (!TryFinishTerminalClose(pending))
            {
                RetainPendingTerminalClose(pending);
            }

            return;
        }

        if (result.Status != EnqueueStatus.Full)
        {
            observability.Diagnostics.Write(
                "Diagnostic",
                "Warn",
                "terminal envelope converged after the connection became unavailable");
            _ = HandleDisconnected(connection, epoch, reason);
            return;
        }

        RetainPendingTerminalClose(pending);
    }

    private void RetryPendingTerminalCloses(int budget)
    {
        foreach (var pending in pendingTerminalCloses.Values.ToArray())
        {
            if (budget-- <= 0)
            {
                break;
            }

            if (!pending.EnvelopeQueued)
            {
                var envelope = pending.Envelope;
                var result = egress.TryEnqueue(pending.Connection, pending.Epoch, in envelope);
                if (result.Status == EnqueueStatus.Full)
                {
                    continue;
                }

                if (result.Status != EnqueueStatus.Accepted)
                {
                    observability.Diagnostics.Write(
                        "Diagnostic",
                        "Warn",
                        "terminal envelope retry converged after the connection closed");
                    RemovePendingTerminalClose(pending);
                    continue;
                }

                pending.EnvelopeQueued = true;
            }

            _ = TryFinishTerminalClose(pending);
        }
    }

    private void RetainPendingTerminalClose(PendingTerminalClose pending)
    {
        var key = TerminalCloseKey(pending.Connection, pending.Epoch);
        if (pendingTerminalCloses.ContainsKey(key))
        {
            return;
        }

        if (pendingTerminalCloses.Count >= SessionProvisionalDefaults.EventOutboxMaxItems)
        {
            observability.Diagnostics.Write("Diagnostic", "Error", "pending terminal close reserve exhausted");
            throw new InvalidOperationException("Pending terminal close reserve exhausted");
        }

        pendingTerminalCloses[key] = pending;
    }

    private bool TryFinishTerminalClose(PendingTerminalClose pending)
    {
        var result = transportControl.TrySend(new ConnectionCommand.Close(
            pending.Connection,
            pending.Epoch,
            pending.Reason));
        if (result.Status == EnqueueStatus.Accepted
            || result.Status == EnqueueStatus.Closed
            || result.StableErrorId == "StaleConnectionGeneration")
        {
            if (result.Status != EnqueueStatus.Accepted)
            {
                observability.Diagnostics.Write(
                    "Diagnostic",
                    "Warn",
                    "terminal close converged after the connection became unavailable");
            }

            RemovePendingTerminalClose(pending);
            return true;
        }

        return false;
    }

    private bool HasPendingTerminalClose(TransportConnectionId connection, ConnectionEpoch epoch)
        => pendingTerminalCloses.ContainsKey(TerminalCloseKey(connection, epoch));

    private void RemovePendingTerminalClose(PendingTerminalClose pending)
    {
        var key = TerminalCloseKey(pending.Connection, pending.Epoch);
        if (pendingTerminalCloses.TryGetValue(key, out var retained)
            && ReferenceEquals(retained, pending))
        {
            pendingTerminalCloses.Remove(key);
        }
    }

    private static (ulong ConnectionId, ulong Epoch) TerminalCloseKey(
        TransportConnectionId connection,
        ConnectionEpoch epoch)
        => (connection.Value, epoch.Value);

    private AckResult SendFullSnapshot(ServerConnectionSession session, in SessionBinding binding)
    {
        var context = new EnvelopeWriteContext(
            session.SessionId.Value,
            session.ProductId,
            session.GameReleaseId,
            ++outboundSequence,
            $"trace-session-{session.SessionId.Value}-{outboundSequence}",
            MvpWireConstants.Reliability,
            MvpWireConstants.MaxMessageBytes,
            MvpWireConstants.MaxFragmentBytes,
            MvpWireConstants.AntiReplayWindow,
            MvpWireConstants.AuthBinding,
            MvpWireConstants.TransportErrorClass);
        // The outbound sequence makes each full snapshot identity unique even
        // when a resync is requested without an authority revision change.
        var snapshotId = $"snapshot-{session.SessionEpoch.Value}-{outboundSequence}";
        var bytes = MvpEnvelopeWriter.WriteFullSnapshot(
            context,
            snapshotId,
            session.SessionEpoch.Value,
            authorityRevision);
        var result = egress.TryEnqueue(binding.ConnectionId, binding.ConnectionEpoch, new OutboundEnvelopeBytes(bytes));
        if (result.Status == EnqueueStatus.Accepted)
        {
            session.RecordSnapshot(snapshotId, authorityRevision);
        }

        return result.Status switch
        {
            EnqueueStatus.Accepted => new AckResult(true, null),
            EnqueueStatus.Full => new AckResult(false, "QueueFull"),
            _ => new AckResult(false, NormalizeStableError(result.StableErrorId, "StaleConnectionGeneration")),
        };
    }

    private AckResult SendFullSnapshotOrIsolateOnQueueFull(
        ServerConnectionSession session,
        in SessionBinding binding)
    {
        var snapshot = SendFullSnapshot(session, binding);
        return !snapshot.Accepted && snapshot.StableErrorId == "QueueFull"
            ? IsolateEgressBackpressure(session, binding)
            : snapshot;
    }

    private AckResult SendDelta(ServerConnectionSession session, in SessionBinding binding, ulong fromRevision)
    {
        var context = new EnvelopeWriteContext(
            session.SessionId.Value,
            session.ProductId,
            session.GameReleaseId,
            ++outboundSequence,
            $"trace-session-{session.SessionId.Value}-{outboundSequence}",
            MvpWireConstants.Reliability,
            MvpWireConstants.MaxMessageBytes,
            MvpWireConstants.MaxFragmentBytes,
            MvpWireConstants.AntiReplayWindow,
            MvpWireConstants.AuthBinding,
            MvpWireConstants.TransportErrorClass);
        var baseSnapshotId = session.LastSnapshotId;
        if (!session.BaselineAcknowledged || string.IsNullOrEmpty(baseSnapshotId))
        {
            return new AckResult(false, "SnapshotBaseMismatch");
        }

        if (session.PendingDeltaConfirmationSequence is not null)
        {
            return new AckResult(true, null);
        }

        if (authorityRevision <= fromRevision)
        {
            return new AckResult(true, null);
        }

        var bytes = MvpEnvelopeWriter.WriteDelta(
            context,
            baseSnapshotId,
            fromRevision,
            authorityRevision,
            outboundSequence);
        var result = egress.TryEnqueue(binding.ConnectionId, binding.ConnectionEpoch, new OutboundEnvelopeBytes(bytes));
        if (result.Status == EnqueueStatus.Accepted)
        {
            session.RecordDelta(
                outboundSequence,
                fromRevision,
                authorityRevision,
                baseSnapshotId);
        }

        return result.Status switch
        {
            EnqueueStatus.Accepted => new AckResult(true, null),
            EnqueueStatus.Full => new AckResult(false, "QueueFull"),
            _ => new AckResult(false, NormalizeStableError(result.StableErrorId, "StaleConnectionGeneration")),
        };
    }

    private AckResult SendDeltaOrIsolateOnQueueFull(
        ServerConnectionSession session,
        in SessionBinding binding,
        ulong fromRevision)
    {
        var delta = SendDelta(session, binding, fromRevision);
        return !delta.Accepted && delta.StableErrorId == "QueueFull"
            ? IsolateEgressBackpressure(session, binding)
            : delta;
    }

    private AckResult IsolateEgressBackpressure(
        ServerConnectionSession session,
        in SessionBinding binding)
    {
        var close = transportControl.TrySend(new ConnectionCommand.Close(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.Fault));
        if (close.Status == EnqueueStatus.Full
            && !HasPendingTerminalClose(binding.ConnectionId, binding.ConnectionEpoch))
        {
            var pending = new PendingTerminalClose(
                binding.ConnectionId,
                binding.ConnectionEpoch,
                default,
                ConnectionCloseReason.Fault)
            {
                EnvelopeQueued = true,
            };
            RetainPendingTerminalClose(pending);
        }
        else if (close.Status != EnqueueStatus.Accepted
            && close.StableErrorId != "StaleConnectionGeneration")
        {
            observability.Diagnostics.Write(
                "Diagnostic",
                "Warn",
                $"session {session.SessionId.Value} detached after transport close became unavailable");
        }

        observability.Diagnostics.Write(
            "Diagnostic",
            "Warn",
            $"session {session.SessionId.Value} isolated after reliable egress backpressure");
        return HandleDisconnected(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.Fault);
    }

    private ReadOnlyMemory<byte> BuildMaintenanceKick(ServerConnectionSession session, string reasonCode)
    {
        var context = new EnvelopeWriteContext(
            session.SessionId.Value,
            session.ProductId,
            session.GameReleaseId,
            ++outboundSequence,
            $"trace-session-kick-{outboundSequence}",
            MvpWireConstants.Reliability,
            MvpWireConstants.MaxMessageBytes,
            MvpWireConstants.MaxFragmentBytes,
            MvpWireConstants.AntiReplayWindow,
            MvpWireConstants.AuthBinding,
            MvpWireConstants.TransportErrorClass);
        return MvpEnvelopeWriter.WriteMaintenanceKick(context, reasonCode);
    }

    private ConnectionEpoch? Compensate(
        AdmissionAttemptId attempt,
        SessionCommand.ConnectionCandidate? candidate,
        ServerConnectionSession? session,
        ConnectionEpoch? boundEpoch)
    {
        if (!RememberAttempt(compensatedAttempts, compensatedAttemptOrder, attempt.Value))
        {
            return null;
        }

        var tracked = admissions.TryGetValue(attempt.Value, out var admission)
            ? admission
            : null;
        var trackedSession = session ?? tracked?.Session;
        var connection = candidate?.ConnectionId ?? tracked?.Candidate.ConnectionId;
        var epoch = boundEpoch
            ?? (tracked?.TransportBound is true ? tracked.BoundEpoch : null);
        var slotEpoch = trackedSession?.SlotEpoch
            ?? tracked?.SlotEpoch;
        var unbindConverged = false;

        TraceAck(AdmissionEffectKind.Compensate, attempt, slotEpoch, epoch);
        if (tracked?.TransportBound is true && connection is { } boundConnection && epoch is { } boundConnectionEpoch)
        {
            var unbind = EnqueueUnbindIntent(
                boundConnection,
                boundConnectionEpoch,
                trackedSession?.SessionId ?? tracked?.SessionId ?? default);
            if (unbind.Accepted)
            {
                epoch = new ConnectionEpoch(boundConnectionEpoch.Value + 1);
                unbindConverged = true;
            }
            else if (unbind.StableErrorId == "StaleConnectionGeneration")
            {
                unbindConverged = true;
            }
        }

        if (trackedSession is not null)
        {
            sessions.Remove(trackedSession.SessionId.Value);
            if (unbindConverged && connection is { } sessionConnection)
            {
                connectionSessions.Remove(sessionConnection.Value);
            }
        }

        if (tracked is not null && tracked.Reservation.Value != 0)
        {
            if (tracked.SlotCommitted)
            {
                if (tracked.ReleaseCommittedOnCompensation
                    && this.admissionPort is not null)
                {
                    QueueReservationRelease(
                        tracked.Reservation,
                        trackedSession?.SessionId ?? tracked.SessionId,
                        tracked.SlotEpoch,
                        committed: true);
                }
            }
            else if (this.admissionPort is not null)
            {
                QueueReservationRelease(
                    tracked.Reservation,
                    trackedSession?.SessionId ?? tracked.SessionId,
                    tracked.SlotEpoch,
                    committed: false);
            }
        }

        admissions.Remove(attempt.Value);
        return epoch ?? candidate?.ConnectionEpoch;
    }

    private void ReleaseCommittedReservation(ServerConnectionSession session)
    {
        if (!committedReservationsBySession.TryGetValue(session.SessionId.Value, out var reservation)
            || this.admissionPort is null)
        {
            return;
        }

        QueueReservationRelease(
            reservation,
            session.SessionId,
            session.SlotEpoch,
            committed: true);
    }

    private void QueueReservationRelease(
        SlotReservationId reservation,
        ServerSessionId session,
        SlotEpoch epoch,
        bool committed)
    {
        if (pendingReservationReleases.ContainsKey(reservation.Value)
            || deadLetterReservationIds.Contains(reservation.Value))
        {
            return;
        }

        var pending = new PendingReservationRelease(reservation, session, epoch, committed);
        pendingReservationReleases.Add(reservation.Value, pending);
        TryReleaseReservation(pending, reportRetry: true);
    }

    private void RetryPendingReservationReleases(int budget)
    {
        foreach (var pending in pendingReservationReleases.Values.Take(budget).ToArray())
        {
            TryReleaseReservation(pending, reportRetry: false);
        }
    }

    private AckResult EnqueueUnbindIntent(
        TransportConnectionId connection,
        ConnectionEpoch epoch,
        ServerSessionId session)
    {
        var key = (connection.Value, epoch.Value);
        if (pendingUnbinds.TryGetValue(key, out var existing))
        {
            return existing.LastResult;
        }

        var pending = new PendingUnbind(connection, epoch, session);
        pendingUnbinds.Add(key, pending);
        return TryUnbind(pending);
    }

    private void RetryPendingUnbinds(int budget)
    {
        foreach (var pending in pendingUnbinds.Values.ToArray())
        {
            if (budget-- <= 0)
            {
                break;
            }

            TryUnbind(pending);
        }
    }

    private AckResult TryUnbind(PendingUnbind pending)
    {
        if (!pendingUnbinds.ContainsKey((pending.Connection.Value, pending.Epoch.Value)))
        {
            return pending.LastResult;
        }

        pending.Attempts++;
        EnqueueResult result;
        try
        {
            result = transportControl.TrySend(new ConnectionCommand.Unbind(
                pending.Connection,
                pending.Epoch));
        }
        catch (Exception ex)
        {
            result = new EnqueueResult(EnqueueStatus.Full, ex.GetType().Name);
        }

        if (result.Status == EnqueueStatus.Accepted)
        {
            pending.LastResult = new AckResult(true, null);
            pendingUnbinds.Remove((pending.Connection.Value, pending.Epoch.Value));
            RemoveConnectionSessionIfCurrent(pending);
            return pending.LastResult;
        }

        if (result.StableErrorId == "StaleConnectionGeneration")
        {
            pending.LastResult = new AckResult(false, "StaleConnectionGeneration");
            pendingUnbinds.Remove((pending.Connection.Value, pending.Epoch.Value));
            RemoveConnectionSessionIfCurrent(pending);
            return pending.LastResult;
        }

        pending.LastResult = new AckResult(false, result.StableErrorId ?? "QueueFull");
        if (pending.Attempts >= UnbindRetryLimit)
        {
            pendingUnbinds.Remove((pending.Connection.Value, pending.Epoch.Value));
            if (deadLetterUnbinds.Count >= SessionProvisionalDefaults.EventOutboxMaxItems)
            {
                observability.Diagnostics.Write(
                    "Diagnostic",
                    "Error",
                    "unbind dead-letter capacity exhausted");
                throw new InvalidOperationException("Unbind dead-letter capacity exhausted");
            }

            pending.LastError = pending.LastResult.StableErrorId;
            deadLetterUnbinds.Enqueue(pending);
            observability.Diagnostics.Write(
                "Diagnostic",
                "Error",
                "unbind retry exhausted; ownership retained in dead-letter");
        }

        return pending.LastResult;
    }

    private void RemoveConnectionSessionIfCurrent(PendingUnbind pending)
    {
        if (!connectionSessions.TryGetValue(pending.Connection.Value, out var sessionId))
        {
            return;
        }

        if (sessions.TryGetValue(sessionId, out var session)
            && session.Binding is { } binding
            && binding.ConnectionEpoch != pending.Epoch)
        {
            // A replacement binding reused the connection id while the old
            // Unbind was pending; never erase the newer generation's mapping.
            return;
        }

        connectionSessions.Remove(pending.Connection.Value);
    }

    private bool HasPendingUnbind(TransportConnectionId connection, ConnectionEpoch epoch)
        => pendingUnbinds.ContainsKey((connection.Value, epoch.Value));

    private void TryReleaseReservation(PendingReservationRelease pending, bool reportRetry)
    {
        if (pending.DeadLettered)
        {
            return;
        }

        pending.Attempts++;
        AckResult result;
        try
        {
            result = pending.Committed
                ? this.admissionPort is { } releasePort
                    ? releasePort.ReleaseCommittedReservation(
                        pending.Reservation,
                        pending.Session,
                        pending.Epoch)
                    : new AckResult(false, "InternalInvariant")
                : this.admissionPort is { } admission
                    ? admission.AbortAdmission(pending.Reservation, pending.Epoch)
                    : new AckResult(false, "InternalInvariant");
        }
        catch (Exception ex)
        {
            pending.LastError = ex.GetType().Name;
            result = new AckResult(false, "InternalInvariant");
            observability.Diagnostics.Write(
                "Diagnostic",
                "Error",
                $"reservation release threw; retaining bounded ownership evidence: {ex.GetType().Name}");
        }
        if (result.Accepted)
        {
            pendingReservationReleases.Remove(pending.Reservation.Value);
            if (committedReservationsBySession.TryGetValue(pending.Session.Value, out var committed)
                && committed == pending.Reservation)
            {
                committedReservationsBySession.Remove(pending.Session.Value);
            }

            return;
        }

        if (result.StableErrorId is "QueueFull" or "TimedOut"
            && pending.Attempts < ReservationReleaseRetryLimit)
        {
            if (reportRetry)
            {
                observability.Diagnostics.Write(
                    "Diagnostic",
                    "Warn",
                    "reservation release deferred for owner-lane retry");
            }

            return;
        }

        observability.Diagnostics.Write(
            "Diagnostic",
            "Error",
            $"reservation release moved to dead-letter: {result.StableErrorId ?? "InternalInvariant"}");
        pendingReservationReleases.Remove(pending.Reservation.Value);
        if (deadLetterReservationReleases.Count >= SessionProvisionalDefaults.EventOutboxMaxItems)
        {
            observability.Diagnostics.Write(
                "Diagnostic",
                "Error",
                "reservation release dead-letter capacity exhausted");
            throw new InvalidOperationException("Reservation release dead-letter capacity exhausted");
        }

        pending.DeadLettered = true;
        pending.LastError = result.StableErrorId ?? "InternalInvariant";
        deadLetterReservationIds.Add(pending.Reservation.Value);
        deadLetterReservationReleases.Enqueue(pending);
    }

    // Faulted is retained as a modeled terminal bit but MVP never transitions
    // here (absences.json ABS-SESSION-FAULTED-UNREACHABLE).
    private void SetState(ServerConnectionSession session, ServerConnectionSessionState state)
    {
        if (session.TryTransition(state))
        {
            TraceState(session);
            if (state is ServerConnectionSessionState.Expired
                or ServerConnectionSessionState.Closed
                or ServerConnectionSessionState.Kicked
                or ServerConnectionSessionState.Faulted)
            {
                RetainTerminalSession(session);
            }
        }
    }

    private void RetainTerminalSession(ServerConnectionSession session)
    {
        if (!sessions.ContainsKey(session.SessionId.Value)
            || !retainedTerminalSessions.Add(session.SessionId.Value))
        {
            return;
        }

        terminalSessionOrder.Enqueue(session.SessionId.Value);
        while (terminalSessionOrder.Count > SessionProvisionalDefaults.EventOutboxMaxItems)
        {
            var expired = terminalSessionOrder.Dequeue();
            retainedTerminalSessions.Remove(expired);
            if (sessions.TryGetValue(expired, out var retained)
                && retained.State is ServerConnectionSessionState.Expired
                    or ServerConnectionSessionState.Closed
                    or ServerConnectionSessionState.Kicked
                    or ServerConnectionSessionState.Faulted)
            {
                sessions.Remove(expired);
            }
        }
    }

    private void TraceState(ServerConnectionSession session)
        => observability.Trace.State(
            session.SessionId.Value,
            session.State.ToString(),
            authorityRevision,
            session.SlotEpoch.Value,
            session.Binding is { } binding ? binding.Grant.Value : null);

    private void TraceAck(
        AdmissionEffectKind effect,
        AdmissionAttemptId attempt,
        SlotEpoch? slotEpoch,
        ConnectionEpoch? connectionEpoch)
        => observability.Trace.Ack(
            effect.ToString(),
            attempt.Value == 0 ? null : attempt.Value,
            slotEpoch?.Value,
            connectionEpoch?.Value);

    private void Publish(in SessionEvent sessionEvent)
    {
        // A reserve item is older than every event published after it. Keep all
        // later events in that bounded tail so a newly freed primary slot cannot
        // let them bypass the reserved event.
        if (terminalReserve.Count > 0)
        {
            EnqueueTerminalReserve(in sessionEvent);
            return;
        }

        var result = eventOutbox.TryPublish(in sessionEvent);
        if (result.Status == EnqueueStatus.Accepted)
        {
            return;
        }

        if (sessionEvent is SessionEvent.Rejected
            or SessionEvent.Disconnected
            or SessionEvent.Reconnected
            or SessionEvent.Drained
            or SessionEvent.Kicked
            or SessionEvent.Faulted)
        {
            EnqueueTerminalReserve(in sessionEvent);
            return;
        }

        observability.Diagnostics.Write("Diagnostic", "Warn", "session event outbox full");
    }

    private void EnqueueTerminalReserve(in SessionEvent sessionEvent)
    {
        if (terminalReserve.Count >= SessionProvisionalDefaults.EventOutboxMaxItems)
        {
            observability.Diagnostics.Write("Diagnostic", "Error", "session terminal reserve exhausted");
            throw new InvalidOperationException("Session terminal reserve exhausted");
        }

        terminalReserve.Enqueue(sessionEvent);
    }

    private abstract record OwnerIngress
    {
        private OwnerIngress()
        {
        }

        internal sealed record ConnectionClosed(ConnectionEvent.Closed Event) : OwnerIngress;

        internal sealed record ConnectionFaulted(ConnectionEvent.Faulted Event) : OwnerIngress;

        internal sealed record AuthenticatedHandshake(
            ConnectionEvent.HandshakeEnvelope Event,
            PrincipalId Principal,
            string ProductId,
            string GameReleaseId) : OwnerIngress;

        internal sealed record InboundEnvelope(
            TransportConnectionId? ConnectionId,
            ConnectionEpoch? ConnectionEpoch,
            ValidatedEnvelopeBytes Envelope) : OwnerIngress;

        internal sealed record AuthorityRevision(ulong Revision) : OwnerIngress;
    }

    private sealed record AuthenticateResult(bool Accepted, string? ReasonCode, PrincipalId Principal);

    private readonly record struct EffectIo(bool Accepted, bool Busy, bool Diverted, string? StableErrorId)
    {
        internal static EffectIo Ok() => new(true, false, false, null);

        internal static EffectIo Fail(string reason) => new(false, false, false, reason);

        internal static EffectIo Retry() => new(false, true, false, null);

        internal static EffectIo Stop() => new(false, false, true, null);
    }

    private sealed class AdmissionAttemptState
    {
        internal AdmissionAttemptState(
            AdmissionAttemptId attempt,
            SessionCommand.ConnectionCandidate candidate)
        {
            Attempt = attempt;
            Candidate = candidate;
        }

        internal AdmissionAttemptId Attempt { get; }

        internal SessionCommand.ConnectionCandidate Candidate { get; }

        internal ServerSessionId SessionId { get; set; }

        internal ServerConnectionSession? Session { get; set; }

        internal SlotReservationId Reservation { get; set; }

        internal SlotEpoch SlotEpoch { get; set; }

        internal PermissionGrantRef Grant { get; set; }

        internal PrincipalId Principal { get; set; }

        internal ReplicationContextHandle ReplicationContext { get; set; }

        internal ConnectionEpoch BoundEpoch { get; set; }

        internal bool SlotCommitted { get; set; }

        internal bool ReleaseCommittedOnCompensation { get; set; }

        internal bool TransportBound { get; set; }
    }

    private sealed class PendingReservationRelease(
        SlotReservationId reservation,
        ServerSessionId session,
        SlotEpoch epoch,
        bool committed)
    {
        internal SlotReservationId Reservation { get; } = reservation;

        internal ServerSessionId Session { get; } = session;

        internal SlotEpoch Epoch { get; } = epoch;

        internal bool Committed { get; } = committed;

        internal int Attempts { get; set; }

        internal string? LastError { get; set; }

        internal bool DeadLettered { get; set; }
    }

    private sealed class PendingUnbind(
        TransportConnectionId connection,
        ConnectionEpoch epoch,
        ServerSessionId session)
    {
        internal TransportConnectionId Connection { get; } = connection;

        internal ConnectionEpoch Epoch { get; } = epoch;

        internal ServerSessionId Session { get; } = session;

        internal int Attempts { get; set; }

        internal string? LastError { get; set; }

        internal AckResult LastResult { get; set; } = new(false, "QueueFull");
    }

    private sealed record DeferredReconnect(
        AdmissionAttemptId Attempt,
        SessionCommand.ConnectionCandidate Candidate);

    private readonly record struct AuthenticatedConnection(
        PrincipalId Principal,
        string ProductId,
        string GameReleaseId);

    private sealed class PendingTerminalClose(
        TransportConnectionId connection,
        ConnectionEpoch epoch,
        OutboundEnvelopeBytes envelope,
        ConnectionCloseReason reason)
    {
        internal TransportConnectionId Connection { get; } = connection;

        internal ConnectionEpoch Epoch { get; } = epoch;

        internal OutboundEnvelopeBytes Envelope { get; } = envelope;

        internal ConnectionCloseReason Reason { get; } = reason;

        internal bool EnvelopeQueued { get; set; }
    }

    private sealed class SessionAdminPort : ISessionAdminPort
    {
        private readonly SessionRegistry owner;

        internal SessionAdminPort(SessionRegistry owner) => this.owner = owner;

        public AckResult BeginDrain(MonotonicInstant graceDeadline)
        {
            lock (owner.ownerGate)
            {
                owner.WriteAdminAudit("BeginDrain");
                return owner.BeginDrain(graceDeadline);
            }
        }

        public AckResult Kick(ServerSessionId sessionId, string registeredReasonCode)
        {
            lock (owner.ownerGate)
            {
                owner.WriteAdminAudit($"Kick:{registeredReasonCode}");
                return owner.Kick(sessionId, registeredReasonCode);
            }
        }

        public AckResult InjectWorldMutation(ServerSessionId onBehalfOf, ReadOnlyMemory<byte> opaqueCommand)
        {
            lock (owner.ownerGate)
            {
                owner.WriteAdminAudit("InjectWorldMutation");
                return owner.InjectWorldMutation(onBehalfOf, opaqueCommand);
            }
        }
    }

    private void WriteAdminAudit(string message)
    {
        var sequence = unchecked((ulong)Interlocked.Increment(ref auditSequence) - 1UL);
        var id = new ServerSessionId("admin-session");
        _ = observability.Audit.WriteSessionScoped(
            id,
            config.ProductId,
            config.GameReleaseId,
            $"trace-session-admin-{sequence}",
            "mvp-session",
            sequence,
            message);
    }

}
