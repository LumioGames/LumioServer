using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.Session;
using Lumio.Server.MvpHost.TestKit;
using Lumio.Server.MvpHost.Wire;
using Xunit;

namespace Lumio.Server.MvpHost.Session.Tests;

public sealed class SessionBehaviorTests
{
    private static readonly AdmissionEffectKind[] AdmissionEffects =
    {
        AdmissionEffectKind.ReadGate,
        AdmissionEffectKind.Authenticate,
        AdmissionEffectKind.MatchExactRelease,
        AdmissionEffectKind.ReserveSlot,
        AdmissionEffectKind.CommitSlot,
        AdmissionEffectKind.CreateSession,
        AdmissionEffectKind.BindConnection,
        AdmissionEffectKind.StartReplication,
    };

    private static readonly string[] TracedAdmissionEffects =
    {
        "ReadGate", "Authenticate", "MatchExactRelease", "ReserveSlot", "CommitSlot",
        "CreateSession", "BindConnection", "StartReplication",
    };

    private static readonly string[] ExpectedStateChanges =
    {
        "Syncing", "Active", "ReconnectWindow", "Syncing", "Active",
    };

    [Fact]
    public void ReducerEmitsTheEightAdmissionEffectsInOrder()
    {
        var reducer = new MvpAdmissionReducer();
        var candidate = Candidate(1, "session-1");
        var state = ServerConnectionSessionState.Admitted;
        var effects = new List<AdmissionEffectKind>();
        SessionCommand candidateCommand = candidate;
        var step = reducer.Advance(in state, in candidateCommand);
        effects.Add(step.Effect);

        foreach (var effect in new[]
        {
            AdmissionEffectKind.ReadGate,
            AdmissionEffectKind.Authenticate,
            AdmissionEffectKind.MatchExactRelease,
            AdmissionEffectKind.ReserveSlot,
            AdmissionEffectKind.CommitSlot,
            AdmissionEffectKind.CreateSession,
            AdmissionEffectKind.BindConnection,
        })
        {
            SessionCommand result = new SessionCommand.DependencyResult(
                new AdmissionAttemptId(1), effect, true, null);
            step = reducer.Advance(in state, in result);
            if (step.Effect != AdmissionEffectKind.None)
            {
                effects.Add(step.Effect);
            }
        }

        Assert.Equal(
            AdmissionEffects,
            effects);
    }

    [Fact]
    public void ReducerRejectsOutOfOrderDependencyResults()
    {
        var reducer = new MvpAdmissionReducer();
        var state = ServerConnectionSessionState.Admitted;
        SessionCommand outOfOrder = new SessionCommand.DependencyResult(
            new AdmissionAttemptId(1),
            AdmissionEffectKind.None,
            true,
            null);

        var step = reducer.Advance(in state, in outOfOrder);

        Assert.Equal(AdmissionEffectKind.Reject, step.Effect);
        Assert.Equal("InvalidArgument", step.StableErrorId);
        Assert.Equal(ServerConnectionSessionState.Admitted, step.NextState);
    }

    [Fact]
    public void TestControlUsesTheTenSecondReconnectOverrideWhenUnset()
    {
        var normalized = new SessionHostConfiguration(
            "A", "A-1.1.0", "pool-a", 0, 0, true).Normalize();

        Assert.Equal(SessionProvisionalDefaults.TestReconnectWindowSeconds, normalized.ReconnectWindowSeconds);
        Assert.Equal(SessionProvisionalDefaults.AdmissionAttemptBudget, normalized.AdmissionAttemptBudget);
    }

    [Fact]
    public void AdmissionStopsAtSyncingUntilBaselineAck()
    {
        using var harness = new SessionHarness();
        var candidate = Candidate(1, "session-1");

        harness.Enqueue(candidate);

        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        Assert.Equal(ServerConnectionSessionState.Syncing, session.State);
        Assert.NotNull(session.Binding);
        Assert.NotNull(session.ReplicationContext);
        Assert.Single(harness.Egress.Envelopes);

        var snapshot = harness.Egress.Envelopes[0].Bytes;
        Assert.Equal(EnvelopeParseStatus.Ok, MvpEnvelopeReader.Validate(snapshot.Span).Status);
        Assert.True(MvpEnvelopeReader.TryReadHeader(snapshot.Span, out var header).Status == EnvelopeParseStatus.Ok);
        Assert.Equal("FullSnapshot", header.MessageType);
        Assert.Equal(
            TracedAdmissionEffects,
            harness.Trace.Acks.Select(a => a.Effect).ToArray());

        var baselineId = ReadSnapshotId(snapshot);
        Assert.True(harness.Registry.NotifyAuthorityRevision(1).Accepted);
        var baselineAck = Validated(MvpEnvelopeWriter.WriteBaselineAck(
            Context("session-1", 99), baselineId, 0));
        var result = harness.Registry.HandleInbound(in baselineAck);

        Assert.True(result.Accepted);
        Assert.Equal(ServerConnectionSessionState.Active, session.State);
        Assert.Equal("Delta", ReadType(harness.Egress.Envelopes[^1].Bytes));
    }

    [Fact]
    public void TransportEvidenceUsesVerifiedPrincipalWithoutCallingCredentialAuth()
    {
        using var harness = new SessionHarness();
        var candidate = CandidateWithEvidence(
            1,
            "session-1",
            new TransportAuthenticationEvidence(
                new PrincipalId("transport-principal"),
                new TransportConnectionId(1),
                new ConnectionEpoch(0),
                "A",
                "A-1.1.0"));

        harness.Enqueue(candidate);

        Assert.Equal(0, harness.Auth.AuthenticateCalls);
        Assert.Equal(new PrincipalId("transport-principal"), harness.Auth.LastAuthorizedPrincipal);
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        Assert.Equal(ServerConnectionSessionState.Syncing, session.State);
    }

    [Fact]
    public void InvalidTransportEvidenceGenerationIsRejectedBeforeAuthorization()
    {
        using var harness = new SessionHarness();
        var candidate = CandidateWithEvidence(
            1,
            "session-1",
            new TransportAuthenticationEvidence(
                new PrincipalId("transport-principal"),
                new TransportConnectionId(99),
                new ConnectionEpoch(0),
                "A",
                "A-1.1.0"));

        harness.Enqueue(candidate);

        Assert.Equal(0, harness.Auth.AuthenticateCalls);
        Assert.Null(harness.Auth.LastAuthorizedPrincipal);
        Assert.False(harness.Registry.TryGet(new ServerSessionId("session-1"), out _));
        Assert.Contains(ConnectionCloseReason.PolicyReject, harness.Transport.CloseReasons);
    }

    [Fact]
    public void AdmissionPassesTheWorldSlotReservationBackFromThePort()
    {
        using var harness = new SessionHarness();
        harness.Slot.Reservation = new SlotReservationId(42);

        harness.Enqueue(Candidate(1, "session-1"));

        Assert.Equal(new SlotReservationId(42), harness.Slot.LastReservation);
        Assert.NotEqual(new SlotReservationId(1), harness.Slot.LastReservation);
    }

    [Fact]
    public void AdmissionRejectsWhenWorldSlotDoesNotIssueAReservation()
    {
        using var harness = new SessionHarness();
        harness.Slot.Reservation = default;

        harness.Enqueue(Candidate(1, "session-1"));

        Assert.Equal(0, harness.Slot.BindCalls);
        Assert.False(harness.Registry.TryGet(new ServerSessionId("session-1"), out _));
        Assert.Contains(ConnectionCloseReason.PolicyReject, harness.Transport.CloseReasons);
    }

    [Fact]
    public void BaselineAckDoesNotEmitZeroWidthDelta()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var snapshotId = ReadSnapshotId(harness.Egress.Envelopes[0].Bytes);

        var ack = Validated(MvpEnvelopeWriter.WriteBaselineAck(
            Context("session-1", 2), snapshotId, session.LastSnapshotRevision));
        Assert.True(harness.Registry.HandleInbound(in ack).Accepted);

        Assert.Equal(ServerConnectionSessionState.Active, session.State);
        Assert.Single(harness.Egress.Envelopes);
    }

    [Fact]
    public void BaselineAckMustNameTheDeliveredSnapshot()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));

        var wrong = Validated(MvpEnvelopeWriter.WriteBaselineAck(
            Context("session-1", 2), "snapshot-not-delivered", 0));
        var result = harness.Registry.HandleInbound(in wrong);

        Assert.False(result.Accepted);
        Assert.Equal("SnapshotBaseMismatch", result.StableErrorId);
        Assert.Equal(ServerConnectionSessionState.Syncing, session.State);
        Assert.False(session.BaselineAcknowledged);
    }

    [Fact]
    public void StaleClosedEventIsRejectedWithoutChangingTheSession()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = session.Binding!.Value;

        var result = harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId,
            new ConnectionEpoch(binding.ConnectionEpoch.Value + 1),
            ConnectionCloseReason.Disconnect));

        Assert.False(result.Accepted);
        Assert.Equal("StaleConnectionGeneration", result.StableErrorId);
        Assert.Equal(ServerConnectionSessionState.Active, session.State);
        Assert.NotNull(session.Binding);
    }

    [Fact]
    public void DisconnectDuringInitialSyncStillOpensReconnectWindow()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = session.Binding!.Value;

        var result = harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId, binding.ConnectionEpoch, ConnectionCloseReason.Disconnect));

        Assert.True(result.Accepted);
        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, session.State);
        Assert.Null(session.Binding);
        Assert.NotNull(session.ReplicationContext);
    }

    [Fact]
    public void ExpiryDoesNotPublishFaultedEvent()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = session.Binding!.Value;
        harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId, binding.ConnectionEpoch, ConnectionCloseReason.Disconnect));
        var timer = harness.Timers.Scheduled[^1].Id;

        harness.Enqueue(new SessionCommand.TimerFired(timer, session.SessionId));

        Assert.Equal(ServerConnectionSessionState.Expired, session.State);
        Assert.DoesNotContain(harness.Trace.Acks, ack => ack.Effect == "Faulted");
        Assert.False(harness.Registry.TryDequeueTerminal(out var terminal)
            && terminal is SessionEvent.Faulted);
    }

    [Fact]
    public void DeltaUsesTheCurrentFullSnapshotIdentity()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var firstSnapshot = ReadSnapshotId(harness.Egress.Envelopes[0].Bytes);

        Assert.True(harness.Registry.NotifyAuthorityRevision(1).Accepted);
        var delta = harness.Egress.Envelopes[^1].Bytes;
        Assert.Equal("Delta", ReadType(delta));
        using (var document = System.Text.Json.JsonDocument.Parse(delta))
        {
            Assert.Equal(
                firstSnapshot,
                document.RootElement.GetProperty("body").GetProperty("baseSnapshotId").GetString());
        }

        var resync = Validated(MvpEnvelopeWriter.WriteResyncRequest(Context("session-1", 3), "gap"));
        Assert.True(harness.Registry.HandleInbound(in resync).Accepted);
        var secondSnapshot = ReadSnapshotId(harness.Egress.Envelopes[^1].Bytes);
        Assert.NotEqual(firstSnapshot, secondSnapshot);
    }

    [Fact]
    public void DisconnectClearsGrantAndReconnectRegeneratesIt()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var firstGrant = session.Binding!.Value.Grant;
        var firstContext = session.ReplicationContext;
        var boundEpoch = session.Binding.Value.ConnectionEpoch;

        harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            new TransportConnectionId(1), boundEpoch, ConnectionCloseReason.Disconnect));

        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, session.State);
        Assert.Null(session.Binding);
        Assert.Equal(firstContext, session.ReplicationContext);
        Assert.NotEmpty(harness.Timers.Scheduled);

        harness.Enqueue(Candidate(2, "session-1"));
        harness.AcknowledgeBaseline("session-1");

        Assert.Equal(ServerConnectionSessionState.Active, session.State);
        Assert.NotNull(session.Binding);
        Assert.NotEqual(firstGrant, session.Binding.Value.Grant);
        Assert.Equal(1UL, session.SessionEpoch.Value);
        Assert.Equal(
            ExpectedStateChanges,
            harness.Trace.States.Select(s => s.SessionState).Where(s => s is not null).ToArray());
    }

    [Fact]
    public void SameConnectionResyncDoesNotReauthenticate()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        var authCalls = harness.Auth.AuthenticateCalls;
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = session.Binding!.Value;
        var request = Validated(MvpEnvelopeWriter.WriteResyncRequest(Context("session-1", 99), "gap"));

        var result = harness.Registry.HandleInbound(in request);

        Assert.True(result.Accepted);
        Assert.Equal(authCalls, harness.Auth.AuthenticateCalls);
        Assert.Equal(2, harness.Egress.Envelopes.Count);
        Assert.Equal("FullSnapshot", ReadType(harness.Egress.Envelopes[1].Bytes));
        Assert.Equal(binding.ConnectionEpoch, session.Binding.Value.ConnectionEpoch);
        Assert.Equal(ServerConnectionSessionState.Active, session.State);

        var newSnapshotId = ReadSnapshotId(harness.Egress.Envelopes[1].Bytes);
        var baseline = Validated(MvpEnvelopeWriter.WriteBaselineAck(
            Context("session-1", 100), newSnapshotId, session.LastSnapshotRevision));
        Assert.True(harness.Registry.HandleInbound(in baseline).Accepted);
        Assert.Equal(ServerConnectionSessionState.Active, session.State);
    }

    [Fact]
    public void KickPublishesEnvelopeBeforeCloseAndTransitionsToKicked()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        Assert.NotNull(harness.Registry.Admin);

        var result = harness.Registry.Admin!.Kick(new ServerSessionId("session-1"), "MaintenanceKick");

        Assert.True(result.Accepted);
        Assert.Equal("MaintenanceKick", ReadType(harness.Egress.Envelopes[^1].Bytes));
        Assert.Equal(ConnectionCommandKind.Close, harness.Transport.Commands[^1]);
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        Assert.Equal(ServerConnectionSessionState.Kicked, session.State);
        Assert.Single(harness.ObservabilityAudit);
    }

    [Fact]
    public void AdminPortIsAbsentWithoutTestControlAndMutationIsOutOfBand()
    {
        using var harness = new SessionHarness(testControl: false);
        Assert.Null(harness.Registry.Admin);
    }

    [Fact]
    public void WindowTimerExpiresOnlyTheMatchingReconnectWindow()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var epoch = session.Binding!.Value.ConnectionEpoch;
        harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            new TransportConnectionId(1), epoch, ConnectionCloseReason.Disconnect));
        var timer = harness.Timers.Scheduled[^1].Id;

        var stale = new SessionCommand.TimerFired(new TimerId(timer.Value + 1), session.SessionId);
        harness.Enqueue(stale);
        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, session.State);

        var due = new SessionCommand.TimerFired(timer, session.SessionId);
        harness.Enqueue(due);
        Assert.Equal(ServerConnectionSessionState.Expired, session.State);
        Assert.Null(session.ReplicationContext);
    }

    private static SessionCommand.ConnectionCandidate Candidate(ulong connection, string session)
        => new(
            new TransportConnectionId(connection),
            new ConnectionEpoch(0),
            Validated(MvpEnvelopeWriter.WriteClientHandshake(Context(session, connection))));

    private static SessionCommand.ConnectionCandidate CandidateWithEvidence(
        ulong connection,
        string session,
        TransportAuthenticationEvidence evidence)
        => new(
            new TransportConnectionId(connection),
            new ConnectionEpoch(0),
            Validated(MvpEnvelopeWriter.WriteClientHandshake(Context(session, connection))))
        {
            AuthenticationEvidence = evidence,
        };

    private static ValidatedEnvelopeBytes Validated(ReadOnlyMemory<byte> bytes)
    {
        var parsed = MvpEnvelopeReader.TryReadHeader(bytes.Span, out var header);
        Assert.Equal(EnvelopeParseStatus.Ok, parsed.Status);
        return new ValidatedEnvelopeBytes(bytes, header);
    }

    private static string ReadType(ReadOnlyMemory<byte> bytes)
    {
        Assert.True(MvpEnvelopeReader.TryReadHeader(bytes.Span, out var header).Status == EnvelopeParseStatus.Ok);
        return header.MessageType;
    }

    private static string ReadSnapshotId(ReadOnlyMemory<byte> bytes)
    {
        using var document = System.Text.Json.JsonDocument.Parse(bytes);
        return document.RootElement.GetProperty("body").GetProperty("snapshotId").GetString()!;
    }

    private static EnvelopeWriteContext Context(string session, ulong sequence)
        => new(
            session,
            "A",
            "A-1.1.0",
            sequence,
            $"trace-{sequence}",
            MvpWireConstants.Reliability,
            65536,
            4096,
            1024,
            "SessionAdmission",
            "Rejectable");

    private sealed class SessionHarness : IDisposable
    {
        internal SessionHarness(bool testControl = true)
        {
            Clock = new FakeMonotonicClock();
            Timers = new FakeTimerService();
            Trace = new RecordingHostTraceSink();
            var audit = PlatformModule.CreateInbox<AuditRecord>(new QueueBudget(256, 65536));
            var diagnostic = PlatformModule.CreateInbox<DiagnosticRecord>(new QueueBudget(256, 65536));
            Observability = ObservabilityModule.Create(
                audit,
                diagnostic,
                new FakeWallClock("2026-08-27T00:10:00Z"),
                Trace,
                new HostIdentity("A", "A-1.1.0", "server-session"));
            ObservabilityAudit = Trace.Audits;
            Slot = new FakeSlot();
            Auth = new FakeAuth();
            Transport = new FakeTransport();
            Egress = new FakeEgress();
            Mutation = new FakeMutation();
            var controls = PlatformModule.CreateInbox<SessionCommand>(new QueueBudget(256, 65536));
            var events = PlatformModule.CreateInbox<SessionEvent>(new QueueBudget(256, 65536));
            Registry = SessionRegistry.Create(
                Slot,
                Auth,
                Transport,
                Egress,
                testControl ? Mutation : null,
                Clock,
                Timers,
                controls,
                PlatformModule.CreateOutbox(events),
                Observability,
                new SessionHostConfiguration("A", "A-1.1.0", "pool-a", 10, 3, testControl));
        }

        internal FakeMonotonicClock Clock { get; }
        internal FakeTimerService Timers { get; }
        internal RecordingHostTraceSink Trace { get; }
        internal ObservabilityServices Observability { get; }
        internal IReadOnlyList<AuditRecord> ObservabilityAudit { get; }
        internal FakeSlot Slot { get; }
        internal FakeAuth Auth { get; }
        internal FakeTransport Transport { get; }
        internal FakeEgress Egress { get; }
        internal FakeMutation Mutation { get; }
        internal SessionRegistry Registry { get; }

        internal void Enqueue(SessionCommand command)
        {
            var inbox = RegistryControl;
            Assert.Equal(EnqueueStatus.Accepted, inbox.TryEnqueue(in command).Status);
            Registry.PumpOnce();
        }

        internal void AcknowledgeBaseline(string sessionId)
        {
            Assert.True(Registry.TryGet(new ServerSessionId(sessionId), out var session));
            Assert.NotEmpty(Egress.Envelopes);
            var snapshotId = ReadSnapshotId(Egress.Envelopes[^1].Bytes);
            var ack = Validated(MvpEnvelopeWriter.WriteBaselineAck(
                Context(sessionId, 100), snapshotId, session.LastSnapshotRevision));
            Assert.True(Registry.HandleInbound(in ack).Accepted);
        }

        private IBoundedInbox<SessionCommand> RegistryControl
        {
            get
            {
                return Registry.ControlInboxForTest;
            }
        }

        public void Dispose() => Registry.Dispose();
    }

    private enum ConnectionCommandKind { Bind, Unbind, Close, SetDrain, EnqueueControlEnvelope }

    private sealed class FakeTransport : ITransportControlPort
    {
        internal List<ConnectionCommandKind> Commands { get; } = new();
        internal List<ConnectionCloseReason> CloseReasons { get; } = new();

        public EnqueueResult TrySend(in ConnectionCommand command)
        {
            if (command is ConnectionCommand.Close close)
            {
                CloseReasons.Add(close.Reason);
            }

            Commands.Add(command switch
            {
                ConnectionCommand.Bind => ConnectionCommandKind.Bind,
                ConnectionCommand.Unbind => ConnectionCommandKind.Unbind,
                ConnectionCommand.Close => ConnectionCommandKind.Close,
                ConnectionCommand.SetDrain => ConnectionCommandKind.SetDrain,
                ConnectionCommand.EnqueueControlEnvelope => ConnectionCommandKind.EnqueueControlEnvelope,
                _ => throw new InvalidOperationException(),
            });
            return new EnqueueResult(EnqueueStatus.Accepted, null);
        }
    }

    private sealed class FakeEgress : IEgressWriter
    {
        internal List<OutboundEnvelopeBytes> Envelopes { get; } = new();

        public EnqueueResult TryEnqueue(TransportConnectionId c, ConnectionEpoch e, in OutboundEnvelopeBytes envelope)
        {
            Envelopes.Add(envelope);
            return new EnqueueResult(EnqueueStatus.Accepted, null);
        }
    }

    private sealed class FakeAuth : IAuthorizationService
    {
        private ulong grants;
        internal int AuthenticateCalls { get; private set; }
        internal PrincipalId? LastAuthorizedPrincipal { get; private set; }

        public bool AdmissionMustStop => false;

        public AuthenticateOutcome Authenticate(in AuthenticateCommand command)
        {
            AuthenticateCalls++;
            return new AuthenticateOutcome(
                CredentialVerdict.Accepted,
                new PrincipalId("principal-1"),
                AntiReplayVerdict.Ok,
                null,
                null);
        }

        public PermissionGrant Authorize(PrincipalId principal, in SessionScope scope)
        {
            LastAuthorizedPrincipal = principal;
            return new PermissionGrant(
                principal,
                scope.Role,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                new GrantEpoch(++grants),
                new MonotonicInstant(long.MaxValue));
        }

        public AckResult EvaluateMessagePermission(in MvpPermissionGateRequest request)
            => new(true, null);
    }

    private sealed class FakeSlot : IWorldSlotHost, IWorldSlotAdmissionPort
    {
        internal AdmissionGateState GateState { get; set; } = AdmissionGateState.Open;
        internal AckResult BindResult { get; set; } = new(true, null);
        internal int BindCalls { get; private set; }
        internal SlotReservationId Reservation { get; set; } = new(7);
        internal SlotReservationId LastReservation { get; private set; }

        public AllocateResult Allocate(in SlotBudget budget) => new(true, new WorldSlotId(1), new SlotEpoch(1), null);
        public AdmissionReservationResult ReserveAdmission(AdmissionAttemptId attempt, ServerSessionId session)
            => new(Reservation.Value != 0, Reservation, new SlotEpoch(1), Reservation.Value == 0 ? "InvalidArgument" : null);
        public AckResult AbortAdmission(SlotReservationId reservation, SlotEpoch epoch) => new(true, null);
        public AckResult BindSession(SlotReservationId reservation, ServerSessionId session, SlotEpoch epoch)
        {
            BindCalls++;
            LastReservation = reservation;
            return BindResult;
        }
        public AckResult Quiesce(string reason, SlotEpoch epoch) => new(true, null);
        public SnapshotCutRef FixSnapshotCut(SlotEpoch epoch) => new(1);
        public AckResult Destroy(SlotEpoch epoch) => new(true, null);
        public AdmissionGateState Gate => GateState;
        public QuotaView Capacity => new(16, BindCalls);
        public AckResult ReportFault(string registeredErrorCode, HostFaultClass faultClass, SlotEpoch epoch) => new(true, null);
    }

    private sealed class FakeMutation : IWorldMutationSink
    {
        public EnqueueResult TryEnqueueOpaqueMutation(ReadOnlyMemory<byte> opaqueCommand)
            => new(EnqueueStatus.Accepted, null);
    }

    private sealed class FakeTimerService : ITimerService
    {
        private ulong next;
        internal List<(TimerId Id, MonotonicInstant DueAt, object Command)> Scheduled { get; } = new();
        public TimerId Schedule<TCommand>(MonotonicInstant dueAt, IBoundedInbox<TCommand> target, in TCommand command)
        {
            var id = new TimerId(++next);
            Scheduled.Add((id, dueAt, command!));
            return id;
        }
        public bool Cancel(TimerId id) => true;
        public void Dispose() { }
    }
}
