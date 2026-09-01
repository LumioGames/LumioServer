#if MVP_HOST_FULL_GRAPH
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.Account;
using Lumio.Server.MvpHost.Admission;
using Lumio.Server.MvpHost.Auth;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.Session;
using Lumio.Server.MvpHost.Simulation.Reference;
using Lumio.Server.MvpHost.Transport;
using Lumio.Server.MvpHost.Transport.WebSocket;
using Lumio.Server.MvpHost.Wire;
using Lumio.Server.MvpHost.WorldSlot;

namespace Lumio.Server.MvpHost.App;

/// <summary>
/// Explicit production graph used once all dependent wave projects are present.
/// Every queue and port is constructed here and crossed only through the frozen
/// HostContracts interfaces.
/// </summary>
internal sealed class FullGraphComposition : IAsyncDisposable
{
    private const int MaxMessageBytes = 65_536;
    private const int MaxFragmentBytes = 4_096;
    private const int AntiReplayWindow = 1_024;
    private const int MaxConnections = 128;
    private const int MaxSessions = 128;
    internal const string ProductionRoomId = "room-main";
    internal const string AdmissionPublicKeyEnv = "LUMIO_ACCOUNT_ADMISSION_PUBLIC_KEY_HEX";
    internal const string AdmissionKeyIdEnv = "LUMIO_ACCOUNT_ADMISSION_KEY_ID";
    // The carrier facade waits only on the wrapper task.  It never cancels the
    // underlying WebSocket receive, because cancellation aborts Kestrel sockets.
    private static readonly TimeSpan CarrierPollInterval = TimeSpan.FromMilliseconds(25);

    private readonly string productId;
    private readonly string gameReleaseId;
    private readonly WebSocketByteCarrier carrier;
    private readonly TransportService transport;
    private readonly WorldSlotHost worldSlot;
    private readonly ReferenceWorldSimulation simulation;
    private readonly SessionRegistry sessions;
    private readonly MvpPacingController pacing;
    private readonly IBoundedInbox<SessionEvent> sessionEvents;
    private readonly IBoundedInbox<WorldSlotEvent> worldEvents;
    private readonly MultiplexedIngress worldIngress;
    private readonly INamedThreadSupervisor threads;
    private readonly IHostTraceSink trace;
    private readonly RoomAdmissionRegistry admission;
    private readonly AdmissionCredentialCapture capture;
    private readonly Dictionary<ulong, ActiveConnection> connections = new();
    private readonly object sessionsGate = new();
    private readonly TaskCompletionSource<object?> faultSignal = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> completionSignal = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private ulong observedRevision;
    private volatile bool fatalFault;
    private bool disposed;

    private FullGraphComposition(
        string productId,
        string gameReleaseId,
        WebSocketByteCarrier carrier,
        TransportService transport,
        WorldSlotHost worldSlot,
        ReferenceWorldSimulation simulation,
        SessionRegistry sessions,
        MvpPacingController pacing,
        IBoundedInbox<SessionEvent> sessionEvents,
        IBoundedInbox<WorldSlotEvent> worldEvents,
        MultiplexedIngress worldIngress,
        INamedThreadSupervisor threads,
        IHostTraceSink trace,
        RoomAdmissionRegistry admission,
        AdmissionCredentialCapture capture)
    {
        this.productId = productId;
        this.gameReleaseId = gameReleaseId;
        this.carrier = carrier;
        this.transport = transport;
        this.worldSlot = worldSlot;
        this.simulation = simulation;
        this.sessions = sessions;
        this.pacing = pacing;
        this.sessionEvents = sessionEvents;
        this.worldEvents = worldEvents;
        this.worldIngress = worldIngress;
        this.threads = threads;
        this.trace = trace;
        this.admission = admission;
        this.capture = capture;
    }

    internal string BoundUri => carrier.BoundUri;

    internal ISessionAdminPort? Admin => sessions.Admin;

    internal static FullGraphComposition Create(
        HostCommandLineOptions options,
        IAuthorizationService authorization,
        ICredentialVerifier verifier,
        IAntiReplayWindow antiReplay,
        IMonotonicClock clock,
        ITimerService timers,
        INamedThreadSupervisor threads,
        ObservabilityServices observability,
        PassThroughFaultPolicy faultPolicy,
        IHostTraceSink trace)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(antiReplay);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(threads);
        ArgumentNullException.ThrowIfNull(observability);
        ArgumentNullException.ThrowIfNull(faultPolicy);
        ArgumentNullException.ThrowIfNull(trace);

        var admissionClock = new SystemAdmissionClock();
        var (admissionKeyId, admissionPublicKey) = ResolveAdmissionSigningKey();
        var admission = HostComposition.CreateRoomAdmissionRegistry(
            admissionKeyId,
            admissionPublicKey,
            admissionClock,
            clock,
            timers,
            Math.Max(1, options.ReconnectWindowSeconds));
        var capture = new AdmissionCredentialCapture();
        var channelVerifier = new ChannelAdmissionVerifier(
            verifier,
            new AccountAdmissionVerifier(admissionKeyId, admissionPublicKey, admissionClock),
            capture);

        var carrierOptions = new WebSocketCarrierOptions(
            options.ListenUri,
            options.ListenUri.StartsWith("wss://", StringComparison.OrdinalIgnoreCase),
            options.AllowInsecureLoopback,
            options.HostProfile,
            MaxMessageBytes,
            MaxConnections,
            15,
            options.ProductId,
            options.GameReleaseId,
            HostCommandLineOptions.DefaultReleasePoolId);
        var carrier = WebSocketByteCarrier.Create(
            in carrierOptions,
            channelVerifier,
            antiReplay,
            clock,
            timers,
            observability.Audit);
        if (string.IsNullOrWhiteSpace(carrier.BoundUri))
        {
            carrier.Dispose();
            throw new InvalidOperationException("WebSocket listener failed to bind");
        }

        // TransportService is single-writer. This facade gives its owner loop a
        // bounded poll so one idle connection cannot starve other connections.
        var pollingCarrier = new PollingByteCarrier(carrier, CarrierPollInterval);

        var endpointOptions = new TransportEndpointOptions(
            options.ListenUri,
            carrierOptions.RequireTls,
            MaxMessageBytes,
            MaxConnections,
            options.ProductId,
            options.GameReleaseId);
        var transport = TransportService.Create(
            pollingCarrier,
            faultPolicy,
            clock,
            timers,
            observability,
            in endpointOptions);

        var simulation = ReferenceWorldSimulation.Create(0x4c554d494fUL);
        var worldInbox = PlatformModule.CreateInbox<WorldSlotCommand>(
            new QueueBudget(
                WorldSlotProvisionalDefaults.AggregateInboxMaxItems
                + WorldSlotProvisionalDefaults.AggregateInboxReservedSlots,
                256 * 1024));
        var worldEvents = PlatformModule.CreateInbox<WorldSlotEvent>(
            new QueueBudget(WorldSlotProvisionalDefaults.SlotEventOutboxMaxItems, 256 * 1024));
        var worldIngress = new MultiplexedIngress(
            new QueueBudget(
                WorldSlotProvisionalDefaults.IngressDrainItemsPerTick * 4,
                WorldSlotProvisionalDefaults.IngressDrainBytesPerTick * 4L));
        var worldSlot = WorldSlotHost.Create(
            simulation,
            clock,
            timers,
            threads,
            worldInbox,
            PlatformModule.CreateOutbox(worldEvents),
            worldIngress,
            new MvpFaultAdjudicator(),
            observability);
        var allocation = worldSlot.Allocate(new SlotBudget(
            MaxSessions,
            WorldSlotProvisionalDefaults.IngressDrainItemsPerTick,
            WorldSlotProvisionalDefaults.IngressDrainBytesPerTick));
        if (!allocation.Allocated)
        {
            worldSlot.Dispose();
            carrier.Dispose();
            throw new InvalidOperationException("WorldSlot allocation failed");
        }

        var sessionInbox = PlatformModule.CreateInbox<SessionCommand>(
            new QueueBudget(SessionProvisionalDefaults.ControlInboxMaxItems, 256 * 1024));
        var sessionEvents = PlatformModule.CreateInbox<SessionEvent>(
            new QueueBudget(SessionProvisionalDefaults.EventOutboxMaxItems, 256 * 1024));
        var sessionConfig = new SessionHostConfiguration(
            options.ProductId,
            options.GameReleaseId,
            HostCommandLineOptions.DefaultReleasePoolId,
            options.ReconnectWindowSeconds,
            SessionProvisionalDefaults.AdmissionAttemptBudget,
            options.EnableTestControl);
        var sessionWorldSlot = new SessionWorldSlotPort(worldSlot);
        var sessions = SessionRegistry.Create(
            sessionWorldSlot,
            authorization,
            transport,
            transport,
            options.EnableTestControl ? (IWorldMutationSink)simulation : null,
            clock,
            timers,
            sessionInbox,
            PlatformModule.CreateOutbox(sessionEvents),
            observability,
            in sessionConfig);
        sessions.AttachEventInbox(sessionEvents);
        worldSlot.AttachEventInbox(worldEvents);
        var pacing = new MvpPacingController(worldSlot, clock, timers);

        return new FullGraphComposition(
            options.ProductId,
            options.GameReleaseId,
            carrier,
            transport,
            worldSlot,
            simulation,
            sessions,
            pacing,
            sessionEvents,
            worldEvents,
            worldIngress,
            threads,
            trace,
            admission,
            capture);
    }

    internal static bool TryAdmitLiveWebsocketClient(
        RoomAdmissionRegistry registry,
        string roomId,
        string connectionId,
        string admissionCredential,
        out ConnectionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(roomId);
        ArgumentNullException.ThrowIfNull(connectionId);
        ArgumentNullException.ThrowIfNull(admissionCredential);

        binding = default;
        if (registry.Admit(roomId, connectionId, admissionCredential) is not RoomAdmitOutcome.Accepted)
        {
            return false;
        }

        return registry.TryGetBindingByConnection(roomId, connectionId, out binding);
    }

    private static (byte KeyId, byte[] PublicKey) ResolveAdmissionSigningKey()
    {
        byte keyId = 1;
        var keyIdText = Environment.GetEnvironmentVariable(AdmissionKeyIdEnv);
        if (!string.IsNullOrEmpty(keyIdText)
            && byte.TryParse(keyIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            keyId = parsed;
        }

        var hex = Environment.GetEnvironmentVariable(AdmissionPublicKeyEnv);
        if (!string.IsNullOrWhiteSpace(hex))
        {
            hex = hex.Trim();
            if ((hex.Length & 1) == 0)
            {
                try
                {
                    var publicKey = Convert.FromHexString(hex);
                    if (publicKey.Length == Ed25519Keys.PublicKeyLength)
                    {
                        return (keyId, publicKey);
                    }
                }
                catch (FormatException)
                {
                }
            }
        }

        return (keyId, Ed25519Keys.Generate().PublicKey);
    }

    private sealed class ChannelAdmissionVerifier(
        ICredentialVerifier inner,
        IAdmissionCredentialVerifier admission,
        AdmissionCredentialCapture capture) : ICredentialVerifier
    {
        private static readonly UTF8Encoding StrictUtf8 = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        public CredentialVerification Verify(OpaqueCredentialInput credential, in VerificationContext context)
        {
            ArgumentNullException.ThrowIfNull(credential);
            var channel = inner.Verify(credential, in context);
            if (channel.Verdict == CredentialVerdict.Accepted)
            {
                return channel;
            }

            if (!TryDecodeUtf8(credential.Span, out var text))
            {
                return channel;
            }

            if (admission.Verify(text) is not AdmissionCredentialOutcome.Accepted accepted)
            {
                return channel;
            }

            capture.Remember(accepted.AccountId, text);
            return new CredentialVerification(
                CredentialVerdict.Accepted,
                new PrincipalId(accepted.AccountId),
                null);
        }

        private static bool TryDecodeUtf8(ReadOnlySpan<byte> bytes, out string text)
        {
            try
            {
                text = StrictUtf8.GetString(bytes);
                return text.Length > 0;
            }
            catch (DecoderFallbackException)
            {
                text = string.Empty;
                return false;
            }
        }
    }

    private sealed class AdmissionCredentialCapture
    {
        private readonly object gate = new();
        private readonly Dictionary<string, string> byAccount = new(StringComparer.Ordinal);

        internal void Remember(string accountId, string credential)
        {
            lock (gate)
            {
                byAccount[accountId] = credential;
            }
        }

        internal bool TryTake(string accountId, out string credential)
        {
            lock (gate)
            {
                return byAccount.Remove(accountId, out credential!);
            }
        }
    }

    internal void Start()
    {
        var started = worldSlot.StartRunning();
        if (!started.Accepted)
        {
            MarkFatalFault();
            throw new InvalidOperationException(
                $"WorldSlot failed to start: {started.StableErrorId ?? "InternalInvariant"}");
        }

        pacing.Start();
        // The accept and transport calls are owned by one named supervisor
        // thread; no untracked Task.Run/background thread is introduced by App.
        _ = threads.Start("mvp-host-pump", new PumpBody(this));
    }

    internal bool HasFatalFault => fatalFault;

    internal Task FatalTask => faultSignal.Task;

    internal Task CompletionTask => completionSignal.Task;

    internal AckResult BeginShutdown(MonotonicInstant deadline)
    {
        lock (sessionsGate)
        {
            return sessions.BeginDrain(deadline);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        pacing.Dispose();
        sessions.Dispose();
        // Closing the carrier first releases the blocking AcceptAsync call in
        // the named pump thread; the remaining owners can then join promptly.
        await carrier.DisposeAsync().ConfigureAwait(false);
        worldSlot.Dispose();
        transport.Dispose();
        _ = trace;
    }

    private sealed class PumpBody(FullGraphComposition owner) : IThreadBody
    {
        public ThreadStepResult Step(CancellationToken ct)
        {
            if (ct.IsCancellationRequested || owner.disposed)
            {
                return new ThreadStepResult(false, null);
            }

            try
            {
                owner.transport.PumpCommandsOnce();
                lock (owner.sessionsGate)
                {
                    owner.sessions.PumpOnce();
                }
                owner.ProcessSessionEvents();

                // PollingByteCarrier bounds each wait, so every connection gets
                // a receive/send turn on this single TransportService writer.
                _ = owner.transport.TryAcceptOne();
                owner.ProcessEvents();

                foreach (var connection in owner.SnapshotConnections())
                {
                    if (owner.transport.StateOf(connection.Id) == TransportConnectionState.Closed)
                    {
                        owner.RemoveConnectionIfCurrent(connection);
                        continue;
                    }

                    // A newly accepted connection has a server-first handshake.
                    // Drain egress before starting the receive task so the peer
                    // can make progress without a first-frame deadlock.
                    _ = owner.transport.PumpSendOnce(connection.Id);
                    _ = owner.transport.PumpReceiveOnce(connection.Id);
                    owner.ProcessEvents();
                    _ = owner.transport.PumpSendOnce(connection.Id);
                }

                owner.ProcessEvents();
                owner.ProcessSessionEvents();
                owner.ProcessWorldEvents();
                if (owner.completionSignal.Task.IsCompleted)
                {
                    return new ThreadStepResult(false, null);
                }

                var revision = owner.simulation.AuthorityRevision;
                if (revision != owner.observedRevision)
                {
                    AckResult revisionResult;
                    lock (owner.sessionsGate)
                    {
                        revisionResult = owner.sessions.NotifyAuthorityRevision(revision);
                        owner.sessions.PumpOnce();
                    }

                    if (!revisionResult.Accepted)
                    {
                        // A revision conflict means the session replication
                        // cursor no longer agrees with the authoritative owner.
                        // Continuing would silently lose a state transition, so
                        // fail-stop and leave the conflict observable to the host.
                        owner.MarkFatalFault();
                        return new ThreadStepResult(
                            false,
                            revisionResult.StableErrorId ?? "RevisionConflict");
                    }

                    owner.observedRevision = revision;
                }

                if (owner.worldSlot.State == WorldSlotHostState.Faulted || owner.fatalFault)
                {
                    owner.MarkFatalFault();
                    return new ThreadStepResult(false, "PanicBoundary");
                }

                return new ThreadStepResult(true, null);
            }
            catch (OperationCanceledException)
            {
                return new ThreadStepResult(false, null);
            }
            catch (Exception)
            {
                owner.MarkFatalFault();
                return new ThreadStepResult(false, "PanicBoundary");
            }
        }
    }

    private ActiveConnection[] SnapshotConnections()
    {
        var result = new ActiveConnection[connections.Count];
        connections.Values.CopyTo(result, 0);
        return result;
    }

    private void MarkFatalFault()
    {
        fatalFault = true;
        faultSignal.TrySetResult(null);
    }

    private void ProcessEvents()
    {
        while (transport.TryReceive(out var connectionEvent))
        {
            switch (connectionEvent)
            {
                case ConnectionEvent.Accepted accepted:
                    var acceptedConnection = new ActiveConnection(accepted.Id, accepted.Epoch);
                    connections[accepted.Id.Value] = acceptedConnection;
                    var handshake = MvpEnvelopeWriter.WriteServerHandshake(new EnvelopeWriteContext(
                        "smoke-session-001",
                        productId,
                        gameReleaseId,
                        acceptedConnection.NextSequence(),
                        "trace-host-handshake",
                        MvpWireConstants.Reliability,
                        MaxMessageBytes,
                        MaxFragmentBytes,
                        AntiReplayWindow,
                        MvpWireConstants.AuthBinding,
                        MvpWireConstants.TransportErrorClass));
                    _ = transport.TryEnqueue(accepted.Id, accepted.Epoch, new OutboundEnvelopeBytes(handshake));
                    break;
                case ConnectionEvent.HandshakeEnvelope handshakeEvent:
                    lock (sessionsGate)
                    {
                        if (!transport.TryTakeAuthenticationMetadata(
                                handshakeEvent.Id,
                                handshakeEvent.Epoch,
                                out var principal,
                                out var authenticatedProductId,
                                out var authenticatedGameReleaseId))
                        {
                            _ = transport.TrySend(new ConnectionCommand.Close(
                                handshakeEvent.Id,
                                handshakeEvent.Epoch,
                                ConnectionCloseReason.PolicyReject));
                        }
                        else
                        {
                            var connectionId = handshakeEvent.Id.Value.ToString(CultureInfo.InvariantCulture);
                            if (capture.TryTake(principal.Value, out var admissionCredential)
                                && !TryAdmitLiveWebsocketClient(
                                    admission,
                                    ProductionRoomId,
                                    connectionId,
                                    admissionCredential,
                                    out _))
                            {
                                _ = transport.TrySend(new ConnectionCommand.Close(
                                    handshakeEvent.Id,
                                    handshakeEvent.Epoch,
                                    ConnectionCloseReason.PolicyReject));
                            }
                            else
                            {
                                _ = sessions.HandleAuthenticatedConnectionEvent(
                                    in handshakeEvent,
                                    principal,
                                    authenticatedProductId,
                                    authenticatedGameReleaseId);
                                sessions.PumpOnce();
                            }
                        }
                    }
                    UpdateEpoch(handshakeEvent.Id);
                    break;
                case ConnectionEvent.IngressReady ingress:
                    if (!connections.TryGetValue(ingress.Id.Value, out var activeConnection)
                        || !IsCurrentIngressGeneration(
                            activeConnection.Epoch,
                            transport.EpochOf(ingress.Id),
                            ingress.Epoch))
                    {
                        // A late event from a prior bind/unbind generation must
                        // not drain frames belonging to the current connection.
                        break;
                    }

                    var buffer = new ValidatedEnvelopeBytes[WorldSlotProvisionalDefaults.IngressDrainItemsPerTick];
                    var count = transport.Drain(
                        ingress.Id,
                        buffer.Length,
                        WorldSlotProvisionalDefaults.IngressDrainBytesPerTick,
                        buffer.AsSpan());
                    for (var index = 0; index < count; index++)
                    {
                        AckResult sessionResult;
                        lock (sessionsGate)
                        {
                            sessionResult = sessions.HandleInbound(
                                ingress.Id,
                                ingress.Epoch,
                                in buffer[index]);
                            sessions.PumpOnce();
                        }

                        var routed = RouteIngress(sessionResult, worldIngress, buffer[index]);
                        if (sessionResult.Accepted && routed.Status != EnqueueStatus.Accepted)
                        {
                            var closed = transport.TrySend(new ConnectionCommand.Close(
                                ingress.Id,
                                ingress.Epoch,
                                ConnectionCloseReason.Fault));
                            if (closed.Status != EnqueueStatus.Accepted
                                && transport.StateOf(ingress.Id) != TransportConnectionState.Closed)
                            {
                                MarkFatalFault();
                            }

                            break;
                        }
                    }

                    break;
                case ConnectionEvent.Closed closedEvent:
                    RemoveConnectionIfCurrent(closedEvent.Id, closedEvent.Epoch);
                    _ = admission.Disconnect(
                        ProductionRoomId,
                        closedEvent.Id.Value.ToString(CultureInfo.InvariantCulture));
                    lock (sessionsGate)
                    {
                        _ = sessions.HandleConnectionEvent(in connectionEvent);
                        sessions.PumpOnce();
                    }
                    break;
                case ConnectionEvent.Faulted faultedEvent:
                    RemoveConnectionIfCurrent(faultedEvent.Id, faultedEvent.Epoch);
                    _ = admission.Disconnect(
                        ProductionRoomId,
                        faultedEvent.Id.Value.ToString(CultureInfo.InvariantCulture));
                    lock (sessionsGate)
                    {
                        _ = sessions.HandleConnectionEvent(in connectionEvent);
                        sessions.PumpOnce();
                    }
                    break;
            }
        }
    }

    private void ProcessSessionEvents()
    {
        while (sessions.TryDequeueEvent(out _))
        {
        }
    }

    private static EnqueueResult RouteIngress(
        AckResult sessionResult,
        MultiplexedIngress ingress,
        ValidatedEnvelopeBytes envelope)
    {
        if (!sessionResult.Accepted)
        {
            return new EnqueueResult(EnqueueStatus.Closed, sessionResult.StableErrorId);
        }

        return ingress.TryEnqueue(in envelope);
    }

    internal static bool IsCurrentConnectionGeneration(
        ConnectionEpoch current,
        ConnectionEpoch observed)
        => current == observed;

    internal static bool IsCurrentIngressGeneration(
        ConnectionEpoch activeConnection,
        ConnectionEpoch transportGeneration,
        ConnectionEpoch observedEvent)
        => activeConnection == transportGeneration
            && activeConnection == observedEvent;

    private bool RemoveConnectionIfCurrent(ActiveConnection expected)
    {
        if (!connections.TryGetValue(expected.Id.Value, out var current)
            || !ReferenceEquals(current, expected)
            || !IsCurrentConnectionGeneration(current.Epoch, expected.Epoch))
        {
            return false;
        }

        return connections.Remove(expected.Id.Value);
    }

    private bool RemoveConnectionIfCurrent(
        TransportConnectionId id,
        ConnectionEpoch observedEpoch)
    {
        if (!connections.TryGetValue(id.Value, out var current)
            || !IsCurrentConnectionGeneration(current.Epoch, observedEpoch))
        {
            return false;
        }

        return connections.Remove(id.Value);
    }

    private void UpdateEpoch(TransportConnectionId id)
    {
        if (connections.TryGetValue(id.Value, out var connection))
        {
            connection.Epoch = transport.EpochOf(id);
        }
    }

    private void ProcessWorldEvents()
    {
        // WorldSlot owns simulation state. Consume its typed event lane only to
        // surface a fail-stop event to the process boundary.
        while (worldSlot.TryDequeueEvent(out var evt))
        {
            if (evt is WorldSlotEvent.TickCompleted)
            {
                continue;
            }
            else if (evt is WorldSlotEvent.FaultAdjudicated fault)
            {
                if (ShouldEscalateFault(fault.Adjudication))
                {
                    MarkFatalFault();
                }
                else
                {
                    lock (sessionsGate)
                    {
                        sessions.RecordUnroutableSessionFault(
                            fault.Epoch,
                            fault.Adjudication.FaultClass);
                    }
                }
            }
            else if (evt is WorldSlotEvent.ReadyToStop)
            {
                completionSignal.TrySetResult(null);
            }
        }
    }

    internal static bool ShouldEscalateFault(in FaultAdjudication adjudication)
        => adjudication.SlotMustFailStop;

    private sealed class MultiplexedIngress : IIngressReader
    {
        private readonly object gate = new();
        private readonly Queue<ValidatedEnvelopeBytes> pending = new();
        private readonly QueueBudget budget;
        private long pendingBytes;

        internal MultiplexedIngress(QueueBudget budget) => this.budget = budget;

        internal EnqueueResult TryEnqueue(in ValidatedEnvelopeBytes envelope)
        {
            lock (gate)
            {
                if (pending.Count >= budget.MaxItems
                    || pendingBytes + envelope.Bytes.Length > budget.MaxBytes)
                {
                    return new EnqueueResult(EnqueueStatus.Full, "QueueFull");
                }

                pending.Enqueue(envelope);
                pendingBytes += envelope.Bytes.Length;
                return new EnqueueResult(EnqueueStatus.Accepted, null);
            }
        }

        public int Drain(
            TransportConnectionId connection,
            int maxItems,
            long maxBytes,
            Span<ValidatedEnvelopeBytes> destination)
        {
            _ = connection;
            lock (gate)
            {
                var taken = 0;
                long bytes = 0;
                while (taken < maxItems && taken < destination.Length && pending.Count > 0)
                {
                    var item = pending.Peek();
                    if (bytes + item.Bytes.Length > maxBytes)
                    {
                        break;
                    }

                    pending.Dequeue();
                    pendingBytes -= item.Bytes.Length;
                    destination[taken++] = item;
                    bytes += item.Bytes.Length;
                }

                return taken;
            }
        }
    }

    /// <summary>
    /// Non-blocking facade around the async carrier.  Transport owns a synchronous
    /// single-writer pump, so each call starts at most one receive/accept operation
    /// and waits only on a wrapper timeout.  The underlying operation always uses
    /// CancellationToken.None; cancelling a Kestrel WebSocket ReceiveAsync aborts
    /// the socket and turns an idle connection into a false transport close.
    /// </summary>
    private sealed class PollingByteCarrier : IByteCarrier, ITransportAuthenticationMetadataSource
    {
        private static readonly CarrierAccept NoAccept =
            new(false, default, ImmutableArray<string>.Empty);
        private static readonly CarrierReceive NoReceive =
            new(false, 0, false, false);

        private readonly object gate = new();
        private readonly IByteCarrier inner;
        private readonly TimeSpan pollInterval;
        private readonly Dictionary<ulong, PendingReceive> pendingReceives = new();
        private Task<CarrierAccept>? pendingAccept;

        internal PollingByteCarrier(IByteCarrier inner, TimeSpan pollInterval)
        {
            this.inner = inner;
            this.pollInterval = pollInterval;
        }

        public async ValueTask<CarrierAccept> AcceptAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<CarrierAccept> operation;
            lock (this.gate)
            {
                if (this.pendingAccept is null)
                {
                    this.pendingAccept = this.inner.AcceptAsync(CancellationToken.None).AsTask();
                }

                operation = this.pendingAccept;
            }

            try
            {
                var result = await operation.WaitAsync(this.pollInterval, cancellationToken)
                    .ConfigureAwait(false);
                this.ClearAccept(operation);
                return result;
            }
            catch (TimeoutException)
            {
                return NoAccept;
            }
            catch
            {
                this.ClearAccept(operation);
                throw;
            }
        }

        bool ITransportAuthenticationMetadataSource.TryTakeAuthenticationMetadata(
            TransportConnectionId connectionId,
            ConnectionEpoch connectionEpoch,
            out PrincipalId principalId,
            out string productId,
            out string gameReleaseId)
        {
            if (this.inner is WebSocketByteCarrier webSocket)
            {
                return webSocket.TryTakeAuthenticationMetadata(
                    connectionId,
                    connectionEpoch,
                    out principalId,
                    out productId,
                    out gameReleaseId);
            }

            if (this.inner is ITransportAuthenticationMetadataSource source)
            {
                return source.TryTakeAuthenticationMetadata(
                    connectionId,
                    connectionEpoch,
                    out principalId,
                    out productId,
                    out gameReleaseId);
            }

            principalId = default;
            productId = string.Empty;
            gameReleaseId = string.Empty;
            return false;
        }

        public async ValueTask<CarrierReceive> ReceiveAsync(
            TransportConnectionId connection,
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PendingReceive pending;
            lock (this.gate)
            {
                if (!this.pendingReceives.TryGetValue(connection.Value, out pending!))
                {
                    // Own the storage for the operation.  Transport's buffer is
                    // a per-call local and may be reclaimed before the task ends.
                    var source = new byte[Math.Max(1, buffer.Length)];
                    var operation = this.inner.ReceiveAsync(
                        connection,
                        source,
                        CancellationToken.None).AsTask();
                    pending = new PendingReceive(source, operation);
                    this.pendingReceives.Add(connection.Value, pending);
                }
            }

            try
            {
                var result = await pending.Operation.WaitAsync(this.pollInterval, cancellationToken)
                    .ConfigureAwait(false);
                this.RemoveReceive(connection, pending);

                if (!result.Received)
                {
                    return result;
                }

                if (result.ByteCount < 0
                    || result.ByteCount > pending.Buffer.Length
                    || result.ByteCount > buffer.Length)
                {
                    // Never truncate a framed message.  The transport will turn
                    // this terminal carrier result into a connection close.
                    _ = this.inner.Close(connection, ConnectionCloseReason.Fault);
                    return new CarrierReceive(false, 0, true, true);
                }

                pending.Buffer.AsMemory(0, result.ByteCount).CopyTo(buffer);
                return result;
            }
            catch (TimeoutException)
            {
                return NoReceive;
            }
            catch
            {
                this.RemoveReceive(connection, pending);
                throw;
            }
        }

        public bool TrySend(TransportConnectionId connection, ReadOnlyMemory<byte> bytes)
            => inner.TrySend(connection, bytes);

        public bool Close(TransportConnectionId connection, ConnectionCloseReason reason)
        {
            PendingReceive? pending;
            lock (this.gate)
            {
                this.pendingReceives.Remove(connection.Value, out pending);
            }

            if (pending is not null)
            {
                // The operation cannot be cancelled safely.  Observe a late
                // fault after the transport has closed the connection.
                _ = Observe(pending.Operation);
            }

            return this.inner.Close(connection, reason);
        }

        private void ClearAccept(Task<CarrierAccept> operation)
        {
            lock (this.gate)
            {
                if (ReferenceEquals(this.pendingAccept, operation))
                {
                    this.pendingAccept = null;
                }
            }
        }

        private void RemoveReceive(TransportConnectionId connection, PendingReceive pending)
        {
            lock (this.gate)
            {
                if (this.pendingReceives.TryGetValue(connection.Value, out var current)
                    && ReferenceEquals(current, pending))
                {
                    this.pendingReceives.Remove(connection.Value);
                }
            }
        }

        private static async Task Observe(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Closing a carrier is best effort; the transport already
                // published its terminal event to the owning session.
            }
        }

        private sealed record PendingReceive(byte[] Buffer, Task<CarrierReceive> Operation);
    }

    private sealed class ActiveConnection
    {
        private ulong sequence;

        internal ActiveConnection(TransportConnectionId id, ConnectionEpoch epoch)
        {
            Id = id;
            Epoch = epoch;
        }

        internal TransportConnectionId Id { get; }

        internal ConnectionEpoch Epoch { get; set; }

        internal ulong NextSequence() => ++sequence;
    }

    private sealed class SessionWorldSlotPort(WorldSlotHost owner)
        : IWorldSlotHost, ISessionWorldSlotPort
    {
        public AdmissionGateState Gate => owner.Gate;

        public QuotaView Capacity => owner.Capacity;

        public AllocateResult Allocate(in SlotBudget budget) => owner.Allocate(in budget);

        public SessionReservationResult ReserveAdmission(
            AdmissionAttemptId attempt,
            ServerSessionId session)
        {
            var result = owner.ReserveAdmission(attempt, session);
            return new SessionReservationResult(
                result.Reserved,
                result.Reservation,
                result.Epoch,
                result.SlotId,
                result.StableErrorId);
        }

        public AckResult BindSession(
            SlotReservationId reservation,
            ServerSessionId session,
            SlotEpoch epoch)
            => owner.BindSession(reservation, session, epoch);

        public AckResult AbortAdmission(SlotReservationId reservation, SlotEpoch epoch)
            => owner.AbortAdmission(reservation, epoch);

        public AckResult ReleaseCommittedReservation(
            SlotReservationId reservation,
            ServerSessionId session,
            SlotEpoch epoch)
            => owner.ReleaseCommittedReservation(reservation, session, epoch);

        public AckResult Quiesce(string reason, SlotEpoch epoch)
            => owner.Quiesce(reason, epoch.Value == 0 ? owner.Epoch : epoch);

        public SnapshotCutRef FixSnapshotCut(SlotEpoch epoch)
            => owner.FixSnapshotCut(epoch);

        public AckResult Destroy(SlotEpoch epoch) => owner.Destroy(epoch);

        public AckResult ReportFault(
            string registeredErrorCode,
            HostFaultClass faultClass,
            SlotEpoch epoch)
            => owner.ReportFault(registeredErrorCode, faultClass, epoch);
    }
}
#endif
