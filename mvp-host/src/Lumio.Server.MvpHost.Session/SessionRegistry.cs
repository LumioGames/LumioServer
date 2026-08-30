using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly IWorldSlotHost slot;
    private readonly IAuthorizationService auth;
    private readonly ITransportControlPort transportControl;
    private readonly IEgressWriter egress;
    private readonly IWorldMutationSink? worldMutations;
    private readonly IMonotonicClock clock;
    private readonly ITimerService timers;
    private readonly IBoundedInbox<SessionCommand> controlInbox;
    private readonly IBoundedOutbox<SessionEvent> eventOutbox;
    private readonly ObservabilityServices observability;
    private readonly SessionHostConfiguration config;
    private readonly Dictionary<string, ServerConnectionSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, string> connectionSessions = new();
    private readonly Dictionary<string, int> attemptsByConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, AdmissionAttemptState> admissions = new();
    private readonly HashSet<ulong> compensatedAttempts = new();
    private readonly HashSet<ulong> rejectedAttempts = new();
    private readonly Queue<SessionEvent> terminalReserve = new();
    private readonly ISessionAdminPort? admin;

    private ulong nextAttempt;
    private ulong nextContext;
    private ulong outboundSequence;
    private ulong auditSequence;
    private ulong authorityRevision;
    private WorldSlotId worldSlotId;
    private SlotEpoch worldSlotEpoch;
    private bool worldSlotAllocated;
    private bool draining;
    private bool disposed;

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
        => sessions.TryGetValue(id.Value, out session!);

    /// <summary>
    /// Processes the currently queued commands in FIFO order. A bounded pass
    /// prevents a command producer from starving the owner loop by self-enqueueing.
    /// </summary>
    public void PumpOnce()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var budget = Math.Max(1, controlInbox.Budget.MaxItems);
        var processed = 0;
        while (processed++ < budget && controlInbox.TryDequeue(out var command))
        {
            Process(command);
        }
    }

    /// <summary>
    /// Transport adapters hand events to the session owner through this method;
    /// the method only enqueues typed commands and never mutates transport state.
    /// </summary>
    public AckResult HandleConnectionEvent(in ConnectionEvent connectionEvent)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        switch (connectionEvent)
        {
            case ConnectionEvent.HandshakeEnvelope handshake:
                var admission = Enqueue(new SessionCommand.ConnectionCandidate(
                    handshake.Id,
                    handshake.Epoch,
                    handshake.Envelope)
                {
                    AuthenticationEvidence = handshake.AuthenticationEvidence,
                });
                if (!admission.Accepted && admission.StableErrorId == "QueueFull")
                {
                    _ = transportControl.TrySend(new ConnectionCommand.Close(
                        handshake.Id,
                        handshake.Epoch,
                        ConnectionCloseReason.PolicyReject));
                }

                return admission;
            case ConnectionEvent.Closed closed:
                return HandleDisconnected(closed.Id, closed.Epoch, closed.Reason);
            case ConnectionEvent.Faulted faulted:
                return HandleDisconnected(faulted.Id, faulted.Epoch, ConnectionCloseReason.Fault);
            default:
                return new AckResult(true, null);
        }
    }

    /// <summary>Queues an already validated ingress frame for replication handling.</summary>
    public AckResult HandleInbound(in ValidatedEnvelopeBytes envelope)
        => HandleInboundCore(null, null, in envelope);

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
        => HandleInboundCore(connectionId, connectionEpoch, in envelope);

    private AckResult HandleInboundCore(
        TransportConnectionId? connectionId,
        ConnectionEpoch? connectionEpoch,
        in ValidatedEnvelopeBytes envelope)
    {
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
                    ? SendDelta(session, binding, confirmedRevision)
                    : new AckResult(true, null);
            case "ResyncRequest":
                if (session.State != ServerConnectionSessionState.Active)
                {
                    return new AckResult(false, "SnapshotBaseMismatch");
                }

                return SendFullSnapshot(session, binding);
            case "DeltaAck":
                if (session.State != ServerConnectionSessionState.Active)
                {
                    return new AckResult(false, "SnapshotBaseMismatch");
                }

                if (!TryReadDeltaAck(envelope.Bytes, out var toRevision)
                    || toRevision > authorityRevision
                    || !session.TryAcknowledgeDelta(toRevision))
                {
                    return new AckResult(false, "SnapshotBaseMismatch");
                }

                return new AckResult(true, null);
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
        ObjectDisposedException.ThrowIf(disposed, this);

        if (revision < authorityRevision)
        {
            return new AckResult(false, "RevisionConflict");
        }

        var previous = authorityRevision;
        if (revision == previous)
        {
            return new AckResult(true, null);
        }

        authorityRevision = revision;
        foreach (var session in sessions.Values.ToArray())
        {
            TraceState(session);
            if (session.State == ServerConnectionSessionState.Active
                && session.BaselineAcknowledged
                && session.Binding is { } binding
                && authorityRevision > session.LastSnapshotRevision)
            {
                _ = SendDelta(session, binding, session.LastSnapshotRevision);
            }
        }

        return new AckResult(true, null);
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

    internal int SessionCount => sessions.Count;

    internal ulong AuthorityRevision => authorityRevision;

    internal bool IsDraining => draining;

    internal IBoundedInbox<SessionCommand> ControlInboxForTest => controlInbox;

    internal AckResult Enqueue(in SessionCommand command)
    {
        var result = controlInbox.TryEnqueue(in command);
        return result.Status switch
        {
            EnqueueStatus.Accepted => new AckResult(true, null),
            EnqueueStatus.Full => new AckResult(false, "QueueFull"),
            _ => new AckResult(false, "ContextClosing"),
        };
    }

    internal AckResult BeginDrain(MonotonicInstant graceDeadline)
    {
        SessionCommand command = new SessionCommand.BeginDrain(graceDeadline);
        var result = Enqueue(in command);
        if (result.Accepted)
        {
            PumpOnce();
        }

        return result;
    }

    internal AckResult Kick(ServerSessionId sessionId, string reasonCode)
    {
        SessionCommand command = new SessionCommand.Kick(sessionId, reasonCode);
        var result = Enqueue(in command);
        if (result.Accepted)
        {
            PumpOnce();
        }

        return result;
    }

    internal AckResult InjectWorldMutation(ServerSessionId onBehalfOf, ReadOnlyMemory<byte> opaqueCommand)
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

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var session in sessions.Values.ToArray())
        {
            if (session.Binding is { } binding)
            {
                _ = transportControl.TrySend(new ConnectionCommand.Close(
                    binding.ConnectionId,
                    binding.ConnectionEpoch,
                    ConnectionCloseReason.OwnerRequest));
            }
        }

        controlInbox.Close();
    }

    private void Process(SessionCommand command)
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

    private void Admit(SessionCommand.ConnectionCandidate candidate)
    {
        var attempt = new AdmissionAttemptId(++nextAttempt);
        admissions[attempt.Value] = new AdmissionAttemptState(attempt, candidate);
        var header = candidate.Handshake.Header;
        var sessionIdText = string.IsNullOrWhiteSpace(header.SessionId)
            ? $"session-{candidate.ConnectionId.Value}"
            : header.SessionId;
        var sessionId = new ServerSessionId(sessionIdText);

        if (!RecordAttempt(candidate.ConnectionId, attempt))
        {
            Reject(attempt, candidate, "QueueFull", close: true, traceCompensation: false);
            return;
        }

        TraceAck(AdmissionEffectKind.ReadGate, attempt, null, candidate.ConnectionEpoch);
        if (draining || slot.Gate != AdmissionGateState.Open || auth.AdmissionMustStop)
        {
            Reject(attempt, candidate, "ContextClosing", close: true, traceCompensation: false);
            return;
        }

        if (TryGet(sessionId, out var existing))
        {
            if (existing.State == ServerConnectionSessionState.ReconnectWindow)
            {
                Reconnect(existing, candidate, attempt);
            }
            else
            {
                Reject(attempt, candidate, "SessionMismatch", close: true, traceCompensation: false);
            }

            return;
        }

        if (candidate.Handshake.Header.MessageType != "Handshake"
            || !IsHandshakeClient(candidate.Handshake))
        {
            TraceAck(AdmissionEffectKind.Authenticate, attempt, null, candidate.ConnectionEpoch);
            Reject(attempt, candidate, "RoleMismatch", close: true, traceCompensation: false);
            return;
        }

        TraceAck(AdmissionEffectKind.Authenticate, attempt, null, candidate.ConnectionEpoch);
        var authentication = Authenticate(candidate, sessionId);
        if (!authentication.Accepted)
        {
            var reason = NormalizeStableError(authentication.ReasonCode, "RoleMismatch");
            Reject(attempt, candidate, reason, close: true, traceCompensation: false);
            return;
        }

        TraceAck(AdmissionEffectKind.MatchExactRelease, attempt, null, candidate.ConnectionEpoch);
        if (!ExactRelease(candidate.Handshake.Header))
        {
            SendErrorAndClose(candidate, "ReleaseMismatch");
            Reject(attempt, candidate, "ReleaseMismatch", close: false, traceCompensation: false);
            return;
        }

        TraceAck(AdmissionEffectKind.ReserveSlot, attempt, null, candidate.ConnectionEpoch);
        var allocation = ReserveSlot();
        if (!allocation.Allocated)
        {
            Reject(
                attempt,
                candidate,
                NormalizeStableError(allocation.StableErrorId, "ContextClosing"),
                close: true,
                traceCompensation: true);
            return;
        }

        var quota = slot.Capacity;
        if (quota.MaxSessions > 0 && quota.BoundSessions >= quota.MaxSessions)
        {
            Reject(attempt, candidate, "CapacityExceeded", close: true, traceCompensation: true);
            return;
        }

        worldSlotId = allocation.SlotId;
        worldSlotEpoch = allocation.Epoch;
        worldSlotAllocated = true;
        var reservationResult = ReserveAdmission(attempt, sessionId, allocation);
        if (!reservationResult.Reserved || reservationResult.Reservation.Value == 0)
        {
            Reject(
                attempt,
                candidate,
                NormalizeStableError(reservationResult.StableErrorId, "InvalidArgument"),
                close: true,
                traceCompensation: true);
            return;
        }

        if (reservationResult.Epoch != allocation.Epoch)
        {
            Reject(attempt, candidate, "StaleEpoch", close: true, traceCompensation: true);
            return;
        }

        var reservation = reservationResult.Reservation;
        var slotEpoch = reservationResult.Epoch;
        admissions[attempt.Value].Reservation = reservation;
        admissions[attempt.Value].SlotEpoch = slotEpoch;
        TraceAck(AdmissionEffectKind.CommitSlot, attempt, slotEpoch, candidate.ConnectionEpoch);
        var commit = slot is IWorldSlotAdmissionPort admissionPort
            ? admissionPort.BindSession(reservation, sessionId, slotEpoch)
            : new AckResult(false, "InvalidArgument");
        if (!commit.Accepted)
        {
            Reject(attempt, candidate, NormalizeStableError(commit.StableErrorId, "CapacityExceeded"), close: true, traceCompensation: true);
            return;
        }

        admissions[attempt.Value].SlotCommitted = true;

        TraceAck(AdmissionEffectKind.CreateSession, attempt, slotEpoch, candidate.ConnectionEpoch);
        if (sessions.ContainsKey(sessionId.Value))
        {
            Reject(attempt, candidate, "SessionMismatch", close: true, traceCompensation: true);
            return;
        }

        var session = new ServerConnectionSession(
            sessionId,
            new SessionEpoch(0),
            config.ProductId,
            config.GameReleaseId);
        session.Associate(worldSlotId, slotEpoch);
        var context = new ReplicationContextHandle(++nextContext);
        var grantRef = GrantReference(authentication.Grant!, attempt);
        sessions.Add(sessionId.Value, session);
        admissions[attempt.Value].Session = session;
        admissions[attempt.Value].Reservation = reservation;
        admissions[attempt.Value].SlotEpoch = slotEpoch;
        admissions[attempt.Value].Grant = grantRef;

        TraceAck(AdmissionEffectKind.BindConnection, attempt, slotEpoch, candidate.ConnectionEpoch);
        var bindResult = transportControl.TrySend(new ConnectionCommand.Bind(
            candidate.ConnectionId,
            candidate.ConnectionEpoch,
            grantRef,
            sessionId));
        if (bindResult.Status != EnqueueStatus.Accepted)
        {
            Reject(attempt, candidate, NormalizeStableError(bindResult.StableErrorId, "StaleConnectionGeneration"), close: true, traceCompensation: true);
            return;
        }

        admissions[attempt.Value].TransportBound = true;
        admissions[attempt.Value].BoundEpoch = new ConnectionEpoch(candidate.ConnectionEpoch.Value + 1);

        var boundEpoch = new ConnectionEpoch(candidate.ConnectionEpoch.Value + 1);
        var activeBinding = new SessionBinding(
            candidate.ConnectionId,
            boundEpoch,
            grantRef,
            worldSlotId,
            slotEpoch);
        session.Bind(activeBinding, context);
        connectionSessions[candidate.ConnectionId.Value] = sessionId.Value;

        TraceAck(AdmissionEffectKind.StartReplication, attempt, slotEpoch, boundEpoch);
        SetState(session, ServerConnectionSessionState.Syncing);
        var snapshot = SendFullSnapshot(session, activeBinding);
        if (!snapshot.Accepted)
        {
            Compensate(attempt, candidate, session, boundEpoch);
            Reject(attempt, candidate, NormalizeStableError(snapshot.StableErrorId, "QueueFull"), close: true, traceCompensation: false);
            return;
        }

        // The admission saga is complete once the first snapshot is queued,
        // but replication is not active until the client confirms that exact
        // snapshot with BaselineAck.
        Publish(new SessionEvent.Admitted(session.SessionId, session.SessionEpoch, activeBinding));
        admissions.Remove(attempt.Value);
    }

    private void Reconnect(ServerConnectionSession session, SessionCommand.ConnectionCandidate candidate, AdmissionAttemptId attempt)
    {
        if (candidate.Handshake.Header.MessageType != "Handshake"
            || !IsHandshakeClient(candidate.Handshake))
        {
            Reject(attempt, candidate, "RoleMismatch", close: true, traceCompensation: false);
            return;
        }

        TraceAck(AdmissionEffectKind.Authenticate, attempt, session.SlotEpoch, candidate.ConnectionEpoch);
        var authentication = Authenticate(candidate, session);
        if (!authentication.Accepted)
        {
            Reject(attempt, candidate, NormalizeStableError(authentication.ReasonCode, "RoleMismatch"), close: true, traceCompensation: false);
            return;
        }

        TraceAck(AdmissionEffectKind.MatchExactRelease, attempt, session.SlotEpoch, candidate.ConnectionEpoch);
        if (!ExactRelease(candidate.Handshake.Header))
        {
            SendErrorAndClose(candidate, "ReleaseMismatch");
            Reject(attempt, candidate, "ReleaseMismatch", close: false, traceCompensation: false);
            return;
        }

        if (session.Binding is not null)
        {
            Reject(attempt, candidate, "SessionMismatch", close: true, traceCompensation: false);
            return;
        }

        TraceAck(AdmissionEffectKind.ReserveSlot, attempt, session.SlotEpoch, candidate.ConnectionEpoch);
        TraceAck(AdmissionEffectKind.CommitSlot, attempt, session.SlotEpoch, candidate.ConnectionEpoch);
        TraceAck(AdmissionEffectKind.CreateSession, attempt, session.SlotEpoch, candidate.ConnectionEpoch);
        TraceAck(AdmissionEffectKind.BindConnection, attempt, session.SlotEpoch, candidate.ConnectionEpoch);

        var grantRef = GrantReference(authentication.Grant!, attempt);
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
            Session = session,
            SlotEpoch = session.SlotEpoch,
            Grant = grantRef,
            SlotCommitted = true,
            TransportBound = true,
            BoundEpoch = new ConnectionEpoch(candidate.ConnectionEpoch.Value + 1),
        };

        if (session.PendingTimer is { } timer)
        {
            _ = timers.Cancel(timer);
            session.PendingTimer = null;
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
        connectionSessions[candidate.ConnectionId.Value] = session.SessionId.Value;
        TraceAck(AdmissionEffectKind.StartReplication, attempt, session.SlotEpoch, activeBinding.ConnectionEpoch);
        SetState(session, ServerConnectionSessionState.Syncing);
        var snapshot = SendFullSnapshot(session, activeBinding);
        if (!snapshot.Accepted)
        {
            Compensate(attempt, candidate, session, activeBinding.ConnectionEpoch);
            session.ClearConnectionBinding();
            SetState(session, ServerConnectionSessionState.Closed);
            Reject(attempt, candidate, NormalizeStableError(snapshot.StableErrorId, "QueueFull"), close: true, traceCompensation: false);
            return;
        }

        Publish(new SessionEvent.Reconnected(session.SessionId, session.SessionEpoch, activeBinding));
        admissions.Remove(attempt.Value);
    }

    private AuthenticateResult Authenticate(SessionCommand.ConnectionCandidate candidate, ServerSessionId sessionId)
    {
        if (candidate.AuthenticationEvidence is { } evidence)
        {
            if (evidence.TransportConnectionId != candidate.ConnectionId
                || evidence.ConnectionEpoch != candidate.ConnectionEpoch)
            {
                return new AuthenticateResult(false, "StaleConnectionGeneration", null);
            }

            if (string.IsNullOrWhiteSpace(evidence.PrincipalId.Value)
                || !string.Equals(
                    evidence.ProductId,
                    candidate.Handshake.Header.ProductId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    evidence.GameReleaseId,
                    candidate.Handshake.Header.GameReleaseId,
                    StringComparison.Ordinal)
                || !ExactRelease(candidate.Handshake.Header))
            {
                return new AuthenticateResult(false, "ReleaseMismatch", null);
            }

            var evidenceGrant = auth.Authorize(
                evidence.PrincipalId,
                new SessionScope(sessionId, config.ProductId, config.GameReleaseId, "Client"));
            return new AuthenticateResult(true, null, evidenceGrant);
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

        if (outcome.Verdict != CredentialVerdict.Accepted || outcome.AntiReplay != AntiReplayVerdict.Ok)
        {
            var reason = outcome.AntiReplay != AntiReplayVerdict.Ok
                ? "SessionAntiReplay"
                : outcome.StableErrorId;
            return new AuthenticateResult(false, reason, null);
        }

        var grant = auth.Authorize(
            outcome.Principal,
            new SessionScope(sessionId, config.ProductId, config.GameReleaseId, "Client"));
        return new AuthenticateResult(true, null, grant);
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
                    Compensate(dependency.Attempt, candidate: null, session: null, boundEpoch: null);
                    Reject(
                        dependency.Attempt,
                        admission.Candidate,
                        NormalizeStableError(dependency.StableErrorId, "QueueFull"),
                        close: true,
                        traceCompensation: false);
                }
            }

            return;
        }

        var step = new MvpAdmissionReducer().Advance(
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
        _ = slot.Quiesce("MaintenanceDrain", epoch);

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
            }

            if (session.Binding is { } binding)
            {
                _ = transportControl.TrySend(new ConnectionCommand.SetDrain(binding.ConnectionId, binding.ConnectionEpoch, true));
                _ = transportControl.TrySend(new ConnectionCommand.Close(
                    binding.ConnectionId,
                    binding.ConnectionEpoch,
                    ConnectionCloseReason.OwnerRequest));
                connectionSessions.Remove(binding.ConnectionId.Value);
                session.ClearConnectionBinding();
            }

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
            _ = egress.TryEnqueue(binding.ConnectionId, binding.ConnectionEpoch, new OutboundEnvelopeBytes(envelope));
            _ = transportControl.TrySend(new ConnectionCommand.Close(
                binding.ConnectionId,
                binding.ConnectionEpoch,
                ConnectionCloseReason.MaintenanceKick));
            connectionSessions.Remove(binding.ConnectionId.Value);
            session.ClearConnectionBinding();
        }

        SetState(session, ServerConnectionSessionState.Kicked);
        Publish(new SessionEvent.Kicked(session.SessionId, session.SessionEpoch, command.RegisteredReasonCode));
    }

    private void ExecuteTimer(SessionCommand.TimerFired command)
    {
        if (!sessions.TryGetValue(command.SessionId.Value, out var session)
            || session.State != ServerConnectionSessionState.ReconnectWindow
            || session.PendingTimer is not { } pending
            || (command.Timer.Value != 0 && pending != command.Timer))
        {
            return;
        }

        session.PendingTimer = null;
        SetState(session, ServerConnectionSessionState.Expired);
        session.ClearReplicationContext();
        foreach (var connection in connectionSessions
            .Where(pair => pair.Value == session.SessionId.Value)
            .Select(pair => pair.Key)
            .ToArray())
        {
            connectionSessions.Remove(connection);
        }
    }

    private static void ExecuteSlotFault(SessionCommand.SlotFaulted command)
    {
        // Faulted is intentionally modeled in the shared state enum but is
        // unreachable in the MVP session track (ABS-SESSION-FAULTED-UNREACHABLE).
        // Fault adjudication belongs to WorldSlot; a session never infers a
        // fault domain or mutates itself from this notification.
    }

    private AckResult HandleDisconnected(TransportConnectionId connection, ConnectionEpoch epoch, ConnectionCloseReason reason)
    {
        if (!connectionSessions.TryGetValue(connection.Value, out var id))
        {
            // A transport close for a connection that never reached admission
            // is already terminal and therefore idempotently acknowledged.
            return new AckResult(true, null);
        }

        if (!sessions.TryGetValue(id, out var session)
            || session.Binding is not { } binding)
        {
            connectionSessions.Remove(connection.Value);
            return new AckResult(true, null);
        }

        if (binding.ConnectionEpoch != epoch)
        {
            return new AckResult(false, "StaleConnectionGeneration");
        }

        if (session.State is ServerConnectionSessionState.Kicked
            or ServerConnectionSessionState.Closed
            or ServerConnectionSessionState.Expired)
        {
            return new AckResult(true, null);
        }

        session.ClearConnectionBinding();
        connectionSessions.Remove(connection.Value);
        SetState(session, ServerConnectionSessionState.ReconnectWindow);
        var due = new MonotonicInstant(
            clock.Now.Ticks + TimeSpan.FromSeconds(config.ReconnectWindowSeconds).Ticks);
        var timerCommand = new SessionCommand.TimerFired(default, session.SessionId);
        var timer = timers.Schedule(due, controlInbox, timerCommand);
        session.PendingTimer = timer;
        Publish(new SessionEvent.Disconnected(session.SessionId, session.SessionEpoch));
        return new AckResult(true, null);
    }

    private bool RecordAttempt(TransportConnectionId connection, AdmissionAttemptId attempt)
    {
        var key = connection.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        attemptsByConnection.TryGetValue(key, out var count);
        count++;
        attemptsByConnection[key] = count;
        return count <= config.AdmissionAttemptBudget;
    }

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

    private AdmissionReservationResult ReserveAdmission(
        AdmissionAttemptId attempt,
        ServerSessionId session,
        in AllocateResult allocation)
    {
        if (slot is IWorldSlotAdmissionPort admissionPort)
        {
            return admissionPort.ReserveAdmission(attempt, session);
        }

        // A reservation must come from the serialized WorldSlot admission
        // operation.  Never derive one from the attempt id or another local
        // value when an adapter does not expose that capability.
        return new AdmissionReservationResult(false, default, allocation.Epoch, allocation.SlotId, "InvalidArgument");
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
        out ulong toRevision)
    {
        toRevision = 0;

        try
        {
            using var document = JsonDocument.Parse(bytes);
            toRevision = document.RootElement
                .GetProperty("body")
                .GetProperty("toRevision")
                .GetUInt64();
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

    private static string NormalizeStableError(string? error, string fallback)
        => error is "AuthBusy" or "AggregateBusy"
            ? "QueueFull"
            : string.IsNullOrWhiteSpace(error) ? fallback : error;

    private static PermissionGrantRef GrantReference(PermissionGrant grant, AdmissionAttemptId attempt)
        => new(grant.Epoch.Value == 0 ? attempt.Value : grant.Epoch.Value);

    private void Reject(
        AdmissionAttemptId attempt,
        SessionCommand.ConnectionCandidate candidate,
        string reason,
        bool close,
        bool traceCompensation)
    {
        if (!rejectedAttempts.Add(attempt.Value))
        {
            return;
        }

        reason = NormalizeStableError(reason, "ContextClosing");

        if (traceCompensation)
        {
            Compensate(attempt, candidate, session: null, boundEpoch: null);
        }

        Publish(new SessionEvent.Rejected(attempt, candidate.ConnectionId, reason));
        if (close)
        {
            _ = transportControl.TrySend(new ConnectionCommand.Close(
                candidate.ConnectionId,
                candidate.ConnectionEpoch,
                ConnectionCloseReason.PolicyReject));
        }

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
        _ = egress.TryEnqueue(candidate.ConnectionId, candidate.ConnectionEpoch, new OutboundEnvelopeBytes(bytes));
        _ = transportControl.TrySend(new ConnectionCommand.Close(
            candidate.ConnectionId,
            candidate.ConnectionEpoch,
            ConnectionCloseReason.PolicyReject));
    }

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
        return result.Status switch
        {
            EnqueueStatus.Accepted => new AckResult(true, null),
            EnqueueStatus.Full => new AckResult(false, "QueueFull"),
            _ => new AckResult(false, NormalizeStableError(result.StableErrorId, "StaleConnectionGeneration")),
        };
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

    private void Compensate(
        AdmissionAttemptId attempt,
        SessionCommand.ConnectionCandidate? candidate,
        ServerConnectionSession? session,
        ConnectionEpoch? boundEpoch)
    {
        if (!compensatedAttempts.Add(attempt.Value))
        {
            return;
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

        TraceAck(AdmissionEffectKind.Compensate, attempt, slotEpoch, epoch);
        if (tracked?.TransportBound is true && connection is { } boundConnection && epoch is { } boundConnectionEpoch)
        {
            _ = transportControl.TrySend(new ConnectionCommand.Unbind(boundConnection, boundConnectionEpoch));
            connectionSessions.Remove(boundConnection.Value);
        }

        if (trackedSession is not null)
        {
            sessions.Remove(trackedSession.SessionId.Value);
            if (connection is { } sessionConnection)
            {
                connectionSessions.Remove(sessionConnection.Value);
            }
        }

        if (tracked is not null
            && !tracked.SlotCommitted
            && tracked.Reservation.Value != 0
            && slot is IWorldSlotAdmissionPort admissionPort)
        {
            _ = admissionPort.AbortAdmission(tracked.Reservation, tracked.SlotEpoch);
        }

        admissions.Remove(attempt.Value);
    }

    private void SetState(ServerConnectionSession session, ServerConnectionSessionState state)
    {
        if (session.TryTransition(state))
        {
            TraceState(session);
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
            terminalReserve.Enqueue(sessionEvent);
            return;
        }

        observability.Diagnostics.Write("Diagnostic", "Warn", "session event outbox full");
    }

    private sealed record AuthenticateResult(bool Accepted, string? ReasonCode, PermissionGrant? Grant);

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

        internal ServerConnectionSession? Session { get; set; }

        internal SlotReservationId Reservation { get; set; }

        internal SlotEpoch SlotEpoch { get; set; }

        internal PermissionGrantRef Grant { get; set; }

        internal ConnectionEpoch BoundEpoch { get; set; }

        internal bool SlotCommitted { get; set; }

        internal bool TransportBound { get; set; }
    }

    private sealed class SessionAdminPort : ISessionAdminPort
    {
        private readonly SessionRegistry owner;

        internal SessionAdminPort(SessionRegistry owner) => this.owner = owner;

        public AckResult BeginDrain(MonotonicInstant graceDeadline)
        {
            owner.WriteAdminAudit("BeginDrain");
            return owner.BeginDrain(graceDeadline);
        }

        public AckResult Kick(ServerSessionId sessionId, string registeredReasonCode)
        {
            owner.WriteAdminAudit($"Kick:{registeredReasonCode}");
            return owner.Kick(sessionId, registeredReasonCode);
        }

        public AckResult InjectWorldMutation(ServerSessionId onBehalfOf, ReadOnlyMemory<byte> opaqueCommand)
        {
            owner.WriteAdminAudit("InjectWorldMutation");
            return owner.InjectWorldMutation(onBehalfOf, opaqueCommand);
        }
    }

    private void WriteAdminAudit(string message)
    {
        var id = new ServerSessionId("admin-session");
        _ = observability.Audit.WriteSessionScoped(
            id,
            config.ProductId,
            config.GameReleaseId,
            $"trace-session-admin-{auditSequence}",
            "mvp-session",
            auditSequence++,
            message);
    }

    private sealed class ServerSessionTimerState
    {
        internal TimerId? PendingTimer { get; set; }
    }
}
