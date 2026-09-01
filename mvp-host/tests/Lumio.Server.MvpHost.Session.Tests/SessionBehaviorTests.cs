using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    private static readonly string[] FrozenPublicSessionCommands =
    {
        "BeginDrain",
        "ConnectionCandidate",
        "DependencyResult",
        "Kick",
        "SlotFaulted",
        "TimerFired",
    };

    private static readonly string[] FrozenPublicWorldSlotCommands =
    {
        "AbortAdmission", "CommitAdmission", "DependencyAck", "Quiesce",
        "ReserveAdmission", "Stop", "TickPermit",
    };

    private static readonly string[] FrozenCarrierAcceptProperties =
    {
        "Accepted", "ConnectionId", "RequestedSubprotocols",
    };

    private static readonly string[] FrozenHandshakeEnvelopeProperties =
    {
        "Envelope", "Epoch", "Id",
    };

    private static readonly string[] FrozenConnectionCandidateProperties =
    {
        "ConnectionEpoch", "ConnectionId", "Handshake",
    };

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

    private static readonly string[] ServerOutboundMessageTypes =
    {
        "Handshake", "FullSnapshot", "Delta", "Error", "MaintenanceKick",
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
    public void ProductionReconnectWindowIsFiveHostMinutes()
    {
        var normalized = new SessionHostConfiguration(
            "A", "A-1.1.0", "pool-a", 0, 0, false).Normalize();

        Assert.Equal(300, SessionProvisionalDefaults.ReconnectWindowSeconds);
        Assert.Equal(SessionProvisionalDefaults.ReconnectWindowSeconds, normalized.ReconnectWindowSeconds);
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(normalized.ReconnectWindowSeconds));
    }

    [Fact]
    public void HostContractsDoesNotGrantProductionFriendAccessToSessionOrWorldSlot()
    {
        var hostContracts = typeof(IWorldSimulationPort).Assembly;
        var friendNames = hostContracts
            .GetCustomAttributes(
                typeof(System.Runtime.CompilerServices.InternalsVisibleToAttribute),
                inherit: false)
            .Cast<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName)
            .Where(name => name is not null)
            .Select(name => name!.Split(',')[0].Trim())
            .ToArray();

        Assert.DoesNotContain("Lumio.Server.MvpHost.Session", friendNames);
        Assert.DoesNotContain("Lumio.Server.MvpHost.WorldSlot", friendNames);
    }

    [Fact]
    public void ExternalTransportIngressOnlyEnqueuesUntilTheOwnerPumps()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = Assert.IsType<SessionBinding>(session.Binding);

        var closeResult = default(AckResult);
        var inboundResult = default(AckResult);
        var worker = new Thread(() =>
        {
            closeResult = harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
                binding.ConnectionId,
                binding.ConnectionEpoch,
                ConnectionCloseReason.Disconnect));

            var request = Validated(MvpEnvelopeWriter.WriteResyncRequest(
                Context("session-1", 700),
                "owner-lane"));
            inboundResult = harness.Registry.HandleInbound(
                binding.ConnectionId,
                binding.ConnectionEpoch,
                in request);
        });

        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));

        Assert.True(closeResult.Accepted);
        Assert.True(inboundResult.Accepted);
        Assert.Equal(ServerConnectionSessionState.Active, session.State);
        Assert.NotNull(session.Binding);

        harness.Registry.PumpOnce();

        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, session.State);
        Assert.Null(session.Binding);
    }

    [Fact]
    public void SynchronousClosedCallbackDuringBindIsDrainedAfterTheAdmissionSaga()
    {
        using var harness = new SessionHarness();
        harness.Transport.OnTrySend = command =>
        {
            if (command is ConnectionCommand.Bind bind)
            {
                ConnectionEvent closed = new ConnectionEvent.Closed(
                    bind.Id,
                    new ConnectionEpoch(bind.Epoch.Value + 1),
                    ConnectionCloseReason.Disconnect);
                _ = harness.Registry.HandleConnectionEvent(in closed);
            }
        };

        harness.Enqueue(Candidate(1, "session-1"));

        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, session.State);
        Assert.Null(session.Binding);
    }

    [Fact]
    public void FailedUnbindRetainsConnectionOwnershipUntilAnOwnerRetrySucceeds()
    {
        using var harness = new SessionHarness();
        harness.Egress.Script(new EnqueueResult(EnqueueStatus.Full, "QueueFull"));
        harness.Transport.Script(
            new EnqueueResult(EnqueueStatus.Accepted, null),
            new EnqueueResult(EnqueueStatus.Full, "QueueFull"),
            new EnqueueResult(EnqueueStatus.Accepted, null));

        harness.Enqueue(Candidate(1, "session-1"));

        Assert.Single(harness.Transport.SentCommands.OfType<ConnectionCommand.Unbind>());
        Assert.True(
            ReadPrivateDictionary(harness.Registry, "connectionSessions").Keys.Cast<ulong>().Contains(1UL),
            "connection mapping must survive a failed unbind enqueue");
        Assert.Equal(1, ReadPrivateDictionaryCount(harness.Registry, "pendingUnbinds"));

        harness.Registry.PumpOnce();

        Assert.Equal(2, harness.Transport.SentCommands.OfType<ConnectionCommand.Unbind>().Count());
        Assert.DoesNotContain(
            ReadPrivateDictionary(harness.Registry, "connectionSessions").Keys.Cast<ulong>(),
            key => key == 1UL);
    }

    [Fact]
    public void PersistentUnbindFailureRetainsIntentInBoundedDeadLetter()
    {
        using var harness = new SessionHarness();
        harness.Egress.Script(new EnqueueResult(EnqueueStatus.Full, "QueueFull"));
        harness.Transport.Script(
            new EnqueueResult(EnqueueStatus.Accepted, null),
            new EnqueueResult(EnqueueStatus.Full, "QueueFull"),
            new EnqueueResult(EnqueueStatus.Accepted, null),
            new EnqueueResult(EnqueueStatus.Full, "QueueFull"),
            new EnqueueResult(EnqueueStatus.Full, "QueueFull"));

        harness.Enqueue(Candidate(1, "session-1"));
        harness.Registry.PumpOnce();
        harness.Registry.PumpOnce();

        Assert.Equal(0, ReadPrivateDictionaryCount(harness.Registry, "pendingUnbinds"));
        Assert.Equal(1, ReadPrivateQueueCount(harness.Registry, "deadLetterUnbinds"));
        Assert.Contains(
            1UL,
            ReadPrivateDictionary(harness.Registry, "connectionSessions").Keys.Cast<ulong>());
    }

    [Fact]
    public void DisposeReleasesCommittedReservationsBeforeClosingTheRegistry()
    {
        var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out _));
        Assert.Equal(0, harness.Slot.ReleaseCalls);

        harness.Registry.Dispose();

        Assert.Equal(1, harness.Slot.ReleaseCalls);
        Assert.Empty(ReadPrivateDictionary(harness.Registry, "committedReservationsBySession"));
        harness.Dispose();
    }

    [Fact]
    public void DisposeRetainsFailedReservationCleanupEvidenceAndIsIdempotent()
    {
        var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.Slot.ScriptAbort(
            new AckResult(false, "QueueFull"),
            new AckResult(false, "QueueFull"),
            new AckResult(false, "QueueFull"));

        harness.Registry.Dispose();
        var releaseCalls = harness.Slot.ReleaseCalls;

        Assert.True(releaseCalls >= 1);
        Assert.True(
            ReadPrivateDictionary(harness.Registry, "committedReservationsBySession")
                .Contains("session-1"));
        Assert.True(
            ReadPrivateDictionaryCount(harness.Registry, "pendingReservationReleases") > 0
            || ReadPrivateQueueCount(harness.Registry, "deadLetterReservationReleases") > 0);

        harness.Registry.Dispose();

        Assert.Equal(releaseCalls, harness.Slot.ReleaseCalls);
        harness.Dispose();
    }

    [Fact]
    public void ExternalInboundQueueTakesAnImmutableEnvelopeCopy()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        var before = harness.Egress.Envelopes.Count;
        var bytes = MvpEnvelopeWriter.WriteResyncRequest(
            Context("session-1", 701),
            "copy-check").ToArray();
        var envelope = Validated(bytes);
        var result = default(AckResult);
        var worker = new Thread(() => result = harness.Registry.HandleInbound(in envelope));
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.True(result.Accepted);

        bytes[0] ^= 0xFF;
        harness.Registry.PumpOnce();

        Assert.Equal(before + 1, harness.Egress.Envelopes.Count);
        Assert.Equal("FullSnapshot", ReadType(harness.Egress.Envelopes[^1].Bytes));
    }

    [Fact]
    public void NormalHandshakeIngressOwnsBytesBeforeTheOwnerPumpRuns()
    {
        using var harness = new SessionHarness();
        var bytes = MvpEnvelopeWriter.WriteClientHandshake(Context("session-1", 701)).ToArray();
        var envelope = Validated(bytes);
        var handshake = new ConnectionEvent.HandshakeEnvelope(
            new TransportConnectionId(1),
            new ConnectionEpoch(0),
            envelope);

        ConnectionEvent connectionEvent = handshake;
        Assert.True(harness.Registry.HandleConnectionEvent(in connectionEvent).Accepted);
        CorruptClientRole(bytes);

        harness.Registry.PumpOnce();

        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out _));
    }

    [Fact]
    public void AuthenticatedHandshakeIngressOwnsBytesBeforeTheOwnerPumpRuns()
    {
        using var harness = new SessionHarness();
        var bytes = MvpEnvelopeWriter.WriteClientHandshake(Context("session-1", 702)).ToArray();
        var envelope = Validated(bytes);
        var handshake = new ConnectionEvent.HandshakeEnvelope(
            new TransportConnectionId(1),
            new ConnectionEpoch(0),
            envelope);

        Assert.True(harness.Registry.HandleAuthenticatedConnectionEvent(
            in handshake,
            new PrincipalId("principal-1"),
            "A",
            "A-1.1.0").Accepted);
        CorruptClientRole(bytes);

        harness.Registry.PumpOnce();

        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out _));
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
    public void DeltaAckMustMatchTheOutstandingConfirmationBeforeCursorAdvances()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));

        Assert.True(harness.Registry.NotifyAuthorityRevision(1).Accepted);
        var firstDelta = harness.Egress.Envelopes[^1].Bytes;
        var confirmationSequence = ReadRevision(firstDelta, "confirmationSequence");
        var envelopeCount = harness.Egress.Envelopes.Count;

        // A newer owner revision is coalesced while the first Delta is pending.
        Assert.True(harness.Registry.NotifyAuthorityRevision(2).Accepted);
        Assert.Equal(envelopeCount, harness.Egress.Envelopes.Count);

        var wrongAck = Validated(MvpEnvelopeWriter.WriteDeltaAck(
            Context("session-1", 101), confirmationSequence + 1, 1));
        Assert.False(harness.Registry.HandleInbound(in wrongAck).Accepted);
        Assert.Equal(0UL, session.LastSnapshotRevision);

        var matchingAck = Validated(MvpEnvelopeWriter.WriteDeltaAck(
            Context("session-1", 102), confirmationSequence, 1));
        Assert.True(harness.Registry.HandleInbound(in matchingAck).Accepted);
        Assert.Equal(1UL, session.LastSnapshotRevision);
        Assert.Equal(envelopeCount + 1, harness.Egress.Envelopes.Count);
        var coalescedDelta = harness.Egress.Envelopes[^1].Bytes;
        Assert.Equal(1UL, ReadRevision(coalescedDelta, "fromRevision"));
        Assert.Equal(2UL, ReadRevision(coalescedDelta, "toRevision"));

        Assert.False(harness.Registry.HandleInbound(in matchingAck).Accepted);
        Assert.Equal(1UL, session.LastSnapshotRevision);

        var incompatibleReplay = Validated(MvpEnvelopeWriter.WriteDeltaAck(
            Context("session-1", 103), confirmationSequence, 2));
        Assert.False(harness.Registry.HandleInbound(in incompatibleReplay).Accepted);
        Assert.Equal(1UL, session.LastSnapshotRevision);
    }

    [Fact]
    public void ExactDeltaAckReplayIsIdempotentWhenNoNewDeltaIsPending()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.NotifyAuthorityRevision(1).Accepted);
        var delta = harness.Egress.Envelopes[^1].Bytes;
        var sequence = ReadRevision(delta, "confirmationSequence");
        var ack = Validated(MvpEnvelopeWriter.WriteDeltaAck(
            Context("session-1", 201), sequence, 1));

        Assert.True(harness.Registry.HandleInbound(in ack).Accepted);
        Assert.True(harness.Registry.HandleInbound(in ack).Accepted);
    }

    [Fact]
    public void BaselineAckQueueFullIsolatesSessionInsteadOfLeavingActiveCursorStalled()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var baselineId = ReadSnapshotId(harness.Egress.Envelopes[^1].Bytes);
        Assert.True(harness.Registry.NotifyAuthorityRevision(1).Accepted);

        harness.Egress.Script(new EnqueueResult(EnqueueStatus.Full, "QueueFull"));
        var baselineAck = Validated(MvpEnvelopeWriter.WriteBaselineAck(
            Context("session-1", 101), baselineId, 0));
        var result = harness.Registry.HandleInbound(in baselineAck);

        Assert.True(result.Accepted);
        Assert.False(session.BaselineAcknowledged);
        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, session.State);
        Assert.Null(session.Binding);
        Assert.Null(session.PendingDeltaConfirmationSequence);
        Assert.Contains(ConnectionCloseReason.Fault, harness.Transport.CloseReasons);
    }

    [Fact]
    public void DeltaAckQueueFullIsolatesSessionInsteadOfLeavingAdvancedCursorStalled()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        Assert.True(harness.Registry.NotifyAuthorityRevision(1).Accepted);
        var firstDelta = harness.Egress.Envelopes[^1].Bytes;
        var confirmationSequence = ReadRevision(firstDelta, "confirmationSequence");
        Assert.True(harness.Registry.NotifyAuthorityRevision(2).Accepted);

        harness.Egress.Script(new EnqueueResult(EnqueueStatus.Full, "QueueFull"));
        var deltaAck = Validated(MvpEnvelopeWriter.WriteDeltaAck(
            Context("session-1", 102), confirmationSequence, 1));
        var result = harness.Registry.HandleInbound(in deltaAck);

        Assert.True(result.Accepted);
        Assert.Equal(1UL, session.LastSnapshotRevision);
        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, session.State);
        Assert.Null(session.Binding);
        Assert.Null(session.PendingDeltaConfirmationSequence);
        Assert.Contains(ConnectionCloseReason.Fault, harness.Transport.CloseReasons);
    }

    [Fact]
    public void AckBackpressureDetachesSessionWhileTransportCloseRetries()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var baselineId = ReadSnapshotId(harness.Egress.Envelopes[^1].Bytes);
        Assert.True(harness.Registry.NotifyAuthorityRevision(1).Accepted);

        harness.Egress.Script(new EnqueueResult(EnqueueStatus.Full, "QueueFull"));
        harness.Transport.Script(
            new EnqueueResult(EnqueueStatus.Full, "QueueFull"),
            new EnqueueResult(EnqueueStatus.Accepted, null));
        var baselineAck = Validated(MvpEnvelopeWriter.WriteBaselineAck(
            Context("session-1", 101), baselineId, 0));
        var result = harness.Registry.HandleInbound(in baselineAck);

        Assert.True(result.Accepted);
        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, session.State);
        Assert.Null(session.Binding);
        Assert.Single(harness.Transport.SentCommands.OfType<ConnectionCommand.Close>());

        harness.Registry.PumpOnce();

        Assert.Equal(2, harness.Transport.SentCommands.OfType<ConnectionCommand.Close>().Count());
    }

    [Fact]
    public void AuthorityRevisionIsolatesOnlyTheBackpressuredSession()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        harness.Enqueue(Candidate(2, "session-2"));
        harness.AcknowledgeBaseline("session-2");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var backpressured));
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-2"), out var healthy));
        var envelopeCount = harness.Egress.Envelopes.Count;

        harness.Egress.Script(new EnqueueResult(EnqueueStatus.Full, "QueueFull"));
        var result = harness.Registry.NotifyAuthorityRevision(1);

        Assert.True(result.Accepted);
        Assert.Equal(1UL, harness.Registry.AuthorityRevision);
        Assert.Null(backpressured.PendingDeltaConfirmationSequence);
        Assert.Equal(envelopeCount + 1, harness.Egress.Envelopes.Count);
        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, backpressured.State);
        Assert.Null(backpressured.Binding);
        Assert.Equal(ServerConnectionSessionState.Active, healthy.State);
        Assert.NotNull(healthy.Binding);
        Assert.NotNull(healthy.PendingDeltaConfirmationSequence);
        Assert.Equal("Delta", ReadType(harness.Egress.Envelopes[^1].Bytes));
        Assert.Contains(ConnectionCloseReason.Fault, harness.Transport.CloseReasons);
    }

    [Fact]
    public void LowerAuthorityRevisionIsRejectedWithoutLocalAdvancement()
    {
        using var harness = new SessionHarness();
        harness.Registry.PumpOnce();
        Assert.True(harness.Registry.NotifyAuthorityRevision(4).Accepted);

        var result = harness.Registry.NotifyAuthorityRevision(3);

        Assert.False(result.Accepted);
        Assert.Equal("RevisionConflict", result.StableErrorId);
        Assert.Equal(4UL, harness.Registry.AuthorityRevision);
    }

    [Fact]
    public void QueuedLowerAuthorityRevisionFaultsTheOwnerInsteadOfBeingDropped()
    {
        using var harness = new SessionHarness();
        harness.Registry.PumpOnce();
        Assert.True(harness.Registry.NotifyAuthorityRevision(4).Accepted);

        var queued = default(AckResult);
        var worker = new Thread(() =>
        {
            queued = harness.Registry.NotifyAuthorityRevision(3);
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.True(queued.Accepted);

        var error = Record.Exception(harness.Registry.PumpOnce);

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(4UL, harness.Registry.AuthorityRevision);
    }

    [Fact]
    public void TransportEvidenceUsesVerifiedPrincipalWithoutCallingCredentialAuth()
    {
        using var harness = new SessionHarness();
        var evidence = new TestAuthenticatedMetadata(
            new PrincipalId("transport-principal"),
            new TransportConnectionId(1),
            new ConnectionEpoch(0),
            "A",
            "A-1.1.0");
        Assert.True(harness.EnqueueAuthenticated(1, "session-1", evidence).Accepted);

        Assert.Equal(0, harness.Auth.AuthenticateCalls);
        Assert.Equal(new PrincipalId("transport-principal"), harness.Auth.LastAuthorizedPrincipal);
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        Assert.Equal(ServerConnectionSessionState.Syncing, session.State);
        Assert.False(typeof(TestAuthenticatedMetadata).IsPublic);
    }

    [Fact]
    public void TransportEvidenceCanBeConsumedOnlyOnce()
    {
        using var harness = new SessionHarness();
        var evidence = new TestAuthenticatedMetadata(
            new PrincipalId("transport-principal"),
            new TransportConnectionId(1),
            new ConnectionEpoch(0),
            "A",
            "A-1.1.0");
        Assert.True(harness.EnqueueAuthenticated(1, "session-1", evidence).Accepted);
        Assert.False(harness.EnqueueAuthenticated(1, "session-2", evidence).Accepted);

        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out _));
        Assert.False(harness.Registry.TryGet(new ServerSessionId("session-2"), out _));
        Assert.Contains(ConnectionCloseReason.PolicyReject, harness.Transport.CloseReasons);
    }

    [Fact]
    public void TransportEvidenceCannotBeReusedByAnotherSessionRegistry()
    {
        var evidence = new TestAuthenticatedMetadata(
            new PrincipalId("transport-principal"),
            new TransportConnectionId(1),
            new ConnectionEpoch(0),
            "A",
            "A-1.1.0");

        using var first = new SessionHarness();
        Assert.True(first.EnqueueAuthenticated(1, "session-first", evidence).Accepted);
        Assert.True(first.Registry.TryGet(new ServerSessionId("session-first"), out _));

        using var second = new SessionHarness();
        Assert.False(second.EnqueueAuthenticated(1, "session-second", evidence).Accepted);

        Assert.False(second.Registry.TryGet(new ServerSessionId("session-second"), out _));
        Assert.Contains(ConnectionCloseReason.PolicyReject, second.Transport.CloseReasons);
    }

    [Fact]
    public void TransportEvidenceHasNoPublicConstructor()
    {
        Assert.False(typeof(TestAuthenticatedMetadata).IsPublic);
        Assert.Empty(typeof(TestAuthenticatedMetadata).GetConstructors());
    }

    [Fact]
    public void PublicSessionCommandsRemainTheFrozenSix()
    {
        var publicCommands = typeof(SessionCommand)
            .GetNestedTypes(BindingFlags.Public)
            .Where(type => type.IsNestedPublic)
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(FrozenPublicSessionCommands, publicCommands);
    }

    [Fact]
    public void AuthenticationProofDoesNotExpandFrozenPublicRecords()
    {
        Assert.Equal(
            FrozenCarrierAcceptProperties,
            PublicDeclaredProperties(typeof(CarrierAccept)));
        Assert.Equal(
            FrozenHandshakeEnvelopeProperties,
            PublicDeclaredProperties(typeof(ConnectionEvent.HandshakeEnvelope)));
        Assert.Equal(
            FrozenConnectionCandidateProperties,
            PublicDeclaredProperties(typeof(SessionCommand.ConnectionCandidate)));
    }

    [Fact]
    public void ReservedSessionEventTailCannotBeBypassedByLaterPrimaryEvents()
    {
        using var harness = new SessionHarness();
        for (var i = 0; i < SessionProvisionalDefaults.EventOutboxMaxItems; i++)
        {
            SessionEvent filler = new SessionEvent.Disconnected(
                new ServerSessionId($"filler-{i}"),
                new SessionEpoch(1));
            Assert.Equal(EnqueueStatus.Accepted, harness.Events.TryEnqueue(in filler).Status);
        }

        harness.Enqueue(Candidate(1, "session-1") with
        {
            Handshake = Validated(MvpEnvelopeWriter.WriteClientHandshake(
                Context("session-1", 1) with { ProductId = "wrong" })),
        });
        Assert.True(harness.Registry.HasPendingTerminalEvents);

        Assert.True(harness.Events.TryDequeue(out _));
        harness.Enqueue(Candidate(2, "session-2") with
        {
            Handshake = Validated(MvpEnvelopeWriter.WriteClientHandshake(
                Context("session-2", 2) with { ProductId = "wrong" })),
        });

        var observed = new List<SessionEvent>();
        while (harness.Registry.TryDequeueEvent(out var sessionEvent))
        {
            observed.Add(sessionEvent);
        }

        var firstRejected = observed.FindIndex(evt => evt is SessionEvent.Rejected rejected
            && rejected.ConnectionId == new TransportConnectionId(1));
        var secondRejected = observed.FindIndex(evt => evt is SessionEvent.Rejected rejected
            && rejected.ConnectionId == new TransportConnectionId(2));
        Assert.True(firstRejected >= 0);
        Assert.True(secondRejected > firstRejected);
    }

    [Fact]
    public void PublicWorldSlotCommandsRemainTheFrozenSeven()
    {
        var publicCommands = typeof(WorldSlotCommand)
            .GetNestedTypes(BindingFlags.Public)
            .Where(type => type.IsNestedPublic)
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(FrozenPublicWorldSlotCommands, publicCommands);
    }

    [Fact]
    public async System.Threading.Tasks.Task PumpRejectsASecondOwnerThread()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));

        var error = await System.Threading.Tasks.Task.Run(
            () => Record.Exception(harness.Registry.PumpOnce));

        Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public void EvidenceCapabilityIsLimitedToTheTransportSessionPipelineAndTests()
    {
        var hostContracts = typeof(IWorldSimulationPort).Assembly;
        Assert.DoesNotContain(
            hostContracts.GetExportedTypes(),
            type => type.Name.Contains("AuthenticationEvidence", StringComparison.Ordinal)
                || type.Name.Contains("AuthenticationMetadata", StringComparison.Ordinal));
        Assert.DoesNotContain(
            hostContracts.GetExportedTypes().SelectMany(type => type.GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)),
            member => member.Name.Contains("AuthenticationEvidence", StringComparison.Ordinal)
                || member.Name.Contains("AuthenticationMetadata", StringComparison.Ordinal));
    }

    [Fact]
    public void EvidenceConsumptionIsInternalAndOneShot()
    {
        var evidence = new TestAuthenticatedMetadata(
            new PrincipalId("transport-principal"),
            new TransportConnectionId(1),
            new ConnectionEpoch(0),
            "A",
            "A-1.1.0");

        var consume = typeof(TestAuthenticatedMetadata).GetMethod(
            "TryConsume",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(consume);
        Assert.False(consume!.IsPublic);
        Assert.True(evidence.TryConsume());
        Assert.False(evidence.TryConsume());
    }

    [Fact]
    public void ReconnectMustUseTheOriginalPrincipal()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = session.Binding!.Value;

        harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.Disconnect));

        harness.Auth.NextPrincipal = new PrincipalId("different-principal");
        harness.Enqueue(Candidate(2, "session-1"));

        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, session.State);
        Assert.Null(session.Binding);
        Assert.Contains(ConnectionCloseReason.PolicyReject, harness.Transport.CloseReasons);
    }

    [Fact]
    public void InvalidTransportEvidenceGenerationIsRejectedBeforeAuthorization()
    {
        using var harness = new SessionHarness();
        var evidence = new TestAuthenticatedMetadata(
            new PrincipalId("transport-principal"),
            new TransportConnectionId(99),
            new ConnectionEpoch(0),
            "A",
            "A-1.1.0");

        Assert.False(harness.EnqueueAuthenticated(1, "session-1", evidence).Accepted);

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
    public void ResyncQueueFullIsolatesSessionInsteadOfLeavingClientWaiting()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));

        harness.Egress.Script(new EnqueueResult(EnqueueStatus.Full, "QueueFull"));
        var request = Validated(MvpEnvelopeWriter.WriteResyncRequest(Context("session-1", 99), "gap"));
        var result = harness.Registry.HandleInbound(in request);

        Assert.True(result.Accepted);
        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, session.State);
        Assert.Null(session.Binding);
        Assert.Contains(ConnectionCloseReason.Fault, harness.Transport.CloseReasons);
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
        harness.Registry.PumpOnce();

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
        harness.Registry.PumpOnce();
        var timer = harness.Timers.Scheduled[^1].Id;

        harness.Enqueue(new SessionCommand.TimerFired(timer, session.SessionId));

        Assert.Equal(ServerConnectionSessionState.Expired, session.State);
        Assert.Equal(1, harness.Slot.ReleaseCalls);
        Assert.DoesNotContain(harness.Trace.Acks, ack => ack.Effect == "Faulted");
        Assert.False(harness.Registry.TryDequeueTerminal(out var terminal)
            && terminal is SessionEvent.Faulted);
    }

    [Fact]
    public void ReservationReleaseRetriesAfterTransientOwnerQueueFailure()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = session.Binding!.Value;
        Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.Disconnect)).Accepted);
        harness.Registry.PumpOnce();
        var timer = Assert.IsType<TimerId>(session.PendingTimer);
        harness.Slot.ScriptAbort(
            new AckResult(false, "QueueFull"),
            new AckResult(true, null));

        harness.Enqueue(new SessionCommand.TimerFired(timer, session.SessionId));
        Assert.Equal(1, harness.Slot.ReleaseCalls);

        harness.Registry.PumpOnce();
        Assert.Equal(2, harness.Slot.ReleaseCalls);
        harness.Registry.PumpOnce();
        Assert.Equal(2, harness.Slot.ReleaseCalls);
    }

    [Fact]
    public void PermanentReservationReleaseFailureRetainsBoundedDeadLetterOwnership()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = Assert.IsType<SessionBinding>(session.Binding);
        Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.Disconnect)).Accepted);
        harness.Registry.PumpOnce();
        var timer = Assert.IsType<TimerId>(session.PendingTimer);
        harness.Slot.ScriptAbort(new AckResult(false, "StaleEpoch"));

        var error = Record.Exception(() => harness.Enqueue(new SessionCommand.TimerFired(
            timer,
            session.SessionId)));

        Assert.Null(error);
        Assert.Equal(ServerConnectionSessionState.Expired, session.State);
        Assert.Equal(1, ReadPrivateQueueCount(harness.Registry, "deadLetterReservationReleases"));
        Assert.Equal(0, ReadPrivateDictionaryCount(harness.Registry, "pendingReservationReleases"));
        Assert.True(ReadPrivateDictionary(harness.Registry, "committedReservationsBySession")
            .Contains(session.SessionId.Value));
    }

    [Fact]
    public void SimultaneousExpiryAndReconnectRacePublishesAStableLoserError()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = Assert.IsType<SessionBinding>(session.Binding);
        Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.Disconnect)).Accepted);
        harness.Registry.PumpOnce();
        var timer = Assert.IsType<TimerId>(session.PendingTimer);

        var reconnect = Candidate(2, "session-1");
        var control = harness.Registry.ControlInboxForTest;
        using var start = new Barrier(2);
        using var timerEnqueued = new ManualResetEventSlim(false);
        EnqueueStatus timerResult = EnqueueStatus.Closed;
        EnqueueStatus reconnectResult = EnqueueStatus.Closed;
        var timerProducer = new Thread(() =>
        {
            start.SignalAndWait();
            timerResult = control.TryEnqueue(
                new SessionCommand.TimerFired(timer, session.SessionId)).Status;
            timerEnqueued.Set();
        });
        var reconnectProducer = new Thread(() =>
        {
            start.SignalAndWait();
            Assert.True(timerEnqueued.Wait(TimeSpan.FromSeconds(5)));
            reconnectResult = control.TryEnqueue(reconnect).Status;
        });
        timerProducer.Start();
        reconnectProducer.Start();
        Assert.True(timerProducer.Join(TimeSpan.FromSeconds(5)));
        Assert.True(reconnectProducer.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(EnqueueStatus.Accepted, timerResult);
        Assert.Equal(EnqueueStatus.Accepted, reconnectResult);

        harness.Registry.PumpOnce();

        Assert.Equal(ServerConnectionSessionState.Expired, session.State);
        Assert.Contains(
            harness.Egress.Envelopes,
            envelope => ReadType(envelope.Bytes) == "Error"
                && ReadReasonCode(envelope.Bytes) == "SessionMismatch");
        Assert.Contains(ConnectionCloseReason.PolicyReject, harness.Transport.CloseReasons);
    }

    [Fact]
    public void ParallelAdminCallsAllocateUniqueMonotonicAuditSequences()
    {
        using var harness = new SessionHarness(testControl: true);
        const int callCount = 64;

        Parallel.For(0, callCount, _ =>
            harness.Registry.Admin!.Kick(
                new ServerSessionId("missing-session"),
                "MaintenanceKick"));

        var sequences = harness.ObservabilityAudit
            .Select(record => record.Correlation.EventSeq)
            .OrderBy(value => value)
            .ToArray();
        Assert.Equal(callCount, sequences.Length);
        Assert.Equal(callCount, sequences.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, callCount).Select(Convert.ToUInt64), sequences);
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
        harness.Registry.PumpOnce();

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
    public void ReconnectSnapshotRevisionStrictlyAdvancesPastTheLastDelta()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.NotifyAuthorityRevision(4).Accepted);
        var delta = harness.Egress.Envelopes[^1].Bytes;
        Assert.Equal("Delta", ReadType(delta));
        var lastDeltaRevision = ReadRevision(delta, "toRevision");
        var confirmationSequence = ReadRevision(delta, "confirmationSequence");
        var deltaAck = Validated(MvpEnvelopeWriter.WriteDeltaAck(
            Context("session-1", 101), confirmationSequence, lastDeltaRevision));
        Assert.True(harness.Registry.HandleInbound(in deltaAck).Accepted);

        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = session.Binding!.Value;
        Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.Disconnect)).Accepted);
        harness.Registry.PumpOnce();

        // The owner must observe a real authoritative revision before a strict
        // advancement can be claimed; reconnect itself must not manufacture one.
        Assert.True(harness.Registry.NotifyAuthorityRevision(lastDeltaRevision + 1).Accepted);
        harness.Enqueue(Candidate(2, "session-1"));
        var snapshot = harness.Egress.Envelopes[^1].Bytes;
        Assert.Equal("FullSnapshot", ReadType(snapshot));
        Assert.True(ReadRevision(snapshot, "gameRevision") > lastDeltaRevision);
    }

    [Fact]
    public void ReconnectDefersUntilAuthorityReallyAdvances()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.NotifyAuthorityRevision(1).Accepted);
        var delta = harness.Egress.Envelopes[^1].Bytes;
        var lastDeltaRevision = ReadRevision(delta, "toRevision");

        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = session.Binding!.Value;
        Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.Disconnect)).Accepted);
        harness.Registry.PumpOnce();
        var reconnectTimer = Assert.IsType<TimerId>(session.PendingTimer);

        var envelopeCount = harness.Egress.Envelopes.Count;
        var authenticateCalls = harness.Auth.AuthenticateCalls;
        harness.Enqueue(Candidate(2, "session-1"));

        Assert.Equal(envelopeCount, harness.Egress.Envelopes.Count);
        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, session.State);
        Assert.Null(session.Binding);
        Assert.Equal(authenticateCalls + 1, harness.Auth.AuthenticateCalls);
        Assert.Equal(lastDeltaRevision, harness.Registry.AuthorityRevision);
        Assert.True(harness.Registry.NotifyAuthorityRevision(lastDeltaRevision).Accepted);
        Assert.Equal(envelopeCount, harness.Egress.Envelopes.Count);
        Assert.Equal(authenticateCalls + 1, harness.Auth.AuthenticateCalls);
        Assert.True(harness.Registry.NotifyAuthorityRevision(lastDeltaRevision + 1).Accepted);

        var snapshot = harness.Egress.Envelopes[^1].Bytes;
        Assert.Equal("FullSnapshot", ReadType(snapshot));
        Assert.True(ReadRevision(snapshot, "gameRevision") > lastDeltaRevision);
        Assert.Equal(authenticateCalls + 1, harness.Auth.AuthenticateCalls);
        Assert.Equal(ServerConnectionSessionState.Syncing, session.State);
        Assert.NotNull(session.Binding);

        harness.Enqueue(new SessionCommand.TimerFired(reconnectTimer, session.SessionId));
        Assert.Equal(ServerConnectionSessionState.Syncing, session.State);
    }

    [Fact]
    public void ReconnectSnapshotEnqueueFailureCannotAdvanceAuthorityRevisionLocally()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.NotifyAuthorityRevision(1).Accepted);
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = Assert.IsType<SessionBinding>(session.Binding);
        Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.Disconnect)).Accepted);
        harness.Registry.PumpOnce();
        harness.Enqueue(Candidate(2, "session-1"));
        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, session.State);

        harness.Egress.Script(new EnqueueResult(EnqueueStatus.Full, "QueueFull"));
        Assert.True(harness.Registry.NotifyAuthorityRevision(2).Accepted);

        Assert.Equal(2UL, harness.Registry.AuthorityRevision);
        Assert.Equal(ServerConnectionSessionState.Closed, session.State);
    }

    [Fact]
    public void DeferredReconnectConsumesEvidenceImmediatelyButDefersGrantDerivation()
    {
        using var harness = new SessionHarness();
        var deferred = EnterReconnectWindowAfterOutstandingDelta(harness);
        var evidence = new TestAuthenticatedMetadata(
            new PrincipalId("principal-1"),
            new TransportConnectionId(2),
            new ConnectionEpoch(0),
            "A",
            "A-1.1.0");
        var credentialAuthCalls = harness.Auth.AuthenticateCalls;
        var authorizeCalls = harness.Auth.AuthorizeCalls;

        Assert.True(harness.EnqueueAuthenticated(2, "session-1", evidence).Accepted);

        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, deferred.Session.State);
        Assert.Null(deferred.Session.Binding);
        Assert.Equal(credentialAuthCalls, harness.Auth.AuthenticateCalls);
        Assert.Equal(authorizeCalls, harness.Auth.AuthorizeCalls);
        Assert.False(evidence.TryConsume());
    }

    [Fact]
    public void DeferredReconnectRetainsNoProofAndDerivesAFreshGrantAfterAuthorityAdvance()
    {
        using var harness = new SessionHarness();
        var deferred = EnterReconnectWindowAfterOutstandingDelta(harness);
        var evidence = new TestAuthenticatedMetadata(
            new PrincipalId("principal-1"),
            new TransportConnectionId(2),
            new ConnectionEpoch(0),
            "A",
            "A-1.1.0");
        var credentialAuthCalls = harness.Auth.AuthenticateCalls;
        var authorizeCalls = harness.Auth.AuthorizeCalls;

        Assert.True(harness.EnqueueAuthenticated(2, "session-1", evidence).Accepted);

        Assert.Equal(authorizeCalls, harness.Auth.AuthorizeCalls);
        Assert.False(SessionRegistryRetainsReference(harness.Registry, evidence));

        Assert.True(harness.Registry.NotifyAuthorityRevision(deferred.LastDeltaRevision + 1).Accepted);

        var binding = Assert.IsType<SessionBinding>(deferred.Session.Binding);
        Assert.Equal(new TransportConnectionId(2), binding.ConnectionId);
        Assert.NotEqual(deferred.FirstGrant, binding.Grant);
        Assert.Equal(credentialAuthCalls, harness.Auth.AuthenticateCalls);
        Assert.Equal(authorizeCalls + 1, harness.Auth.AuthorizeCalls);
        Assert.Equal(new PrincipalId("principal-1"), harness.Auth.LastAuthorizedPrincipal);
    }

    [Theory]
    [InlineData("wrong-principal", "A", "A-1.1.0")]
    [InlineData("principal-1", "A", "A-9.9.9")]
    public void InvalidEvidenceCannotOccupyTheDeferredReconnectSlot(
        string principal,
        string productId,
        string gameReleaseId)
    {
        using var harness = new SessionHarness();
        var deferred = EnterReconnectWindowAfterOutstandingDelta(harness);
        var invalidEvidence = new TestAuthenticatedMetadata(
            new PrincipalId(principal),
            new TransportConnectionId(2),
            new ConnectionEpoch(0),
            productId,
            gameReleaseId);
        var validEvidence = new TestAuthenticatedMetadata(
            new PrincipalId("principal-1"),
            new TransportConnectionId(3),
            new ConnectionEpoch(0),
            "A",
            "A-1.1.0");
        var authorizeCalls = harness.Auth.AuthorizeCalls;

        Assert.True(harness.EnqueueAuthenticated(2, "session-1", invalidEvidence).Accepted);
        Assert.True(harness.EnqueueAuthenticated(3, "session-1", validEvidence).Accepted);

        Assert.Equal(authorizeCalls, harness.Auth.AuthorizeCalls);
        Assert.True(harness.Registry.NotifyAuthorityRevision(deferred.LastDeltaRevision + 1).Accepted);

        var binding = Assert.IsType<SessionBinding>(deferred.Session.Binding);
        Assert.Equal(new TransportConnectionId(3), binding.ConnectionId);
        Assert.Equal(authorizeCalls + 1, harness.Auth.AuthorizeCalls);
        Assert.False(invalidEvidence.TryConsume());
        Assert.False(validEvidence.TryConsume());
    }

    [Fact]
    public void ReconnectWindowExpiryRejectsAnEarlierDeferredCandidateDeterministically()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.NotifyAuthorityRevision(1).Accepted);
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = session.Binding!.Value;
        Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.Disconnect)).Accepted);
        harness.Registry.PumpOnce();
        var timer = Assert.IsType<TimerId>(session.PendingTimer);
        var authenticateCalls = harness.Auth.AuthenticateCalls;

        harness.Enqueue(Candidate(2, "session-1"));
        harness.Enqueue(new SessionCommand.TimerFired(timer, session.SessionId));

        Assert.Equal(ServerConnectionSessionState.Expired, session.State);
        Assert.Null(session.Binding);
        Assert.Equal(authenticateCalls + 1, harness.Auth.AuthenticateCalls);
        Assert.Equal("Error", ReadType(harness.Egress.Envelopes[^1].Bytes));
        Assert.Equal("SessionMismatch", ReadReasonCode(harness.Egress.Envelopes[^1].Bytes));
        Assert.Contains(ConnectionCloseReason.PolicyReject, harness.Transport.CloseReasons);
    }

    [Fact]
    public void ReconnectCandidateReceivesAStableErrorWhenExpiryWinsFirst()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = Assert.IsType<SessionBinding>(session.Binding);
        Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.Disconnect)).Accepted);
        harness.Registry.PumpOnce();
        var timer = Assert.IsType<TimerId>(session.PendingTimer);

        harness.Enqueue(new SessionCommand.TimerFired(timer, session.SessionId));
        harness.Enqueue(Candidate(2, "session-1"));

        Assert.Equal(ServerConnectionSessionState.Expired, session.State);
        Assert.Equal("Error", ReadType(harness.Egress.Envelopes[^1].Bytes));
        Assert.Equal("SessionMismatch", ReadReasonCode(harness.Egress.Envelopes[^1].Bytes));
        Assert.Contains(ConnectionCloseReason.PolicyReject, harness.Transport.CloseReasons);
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
    public void KickSendsEnvelopeBeforeCloseTest()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        Assert.NotNull(harness.Registry.Admin);
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = Assert.IsType<SessionBinding>(session.Binding);

        var result = harness.Registry.Admin!.Kick(new ServerSessionId("session-1"), "MaintenanceKick");
        harness.Registry.PumpOnce();

        Assert.True(result.Accepted);
        Assert.Equal("MaintenanceKick", ReadType(harness.Egress.Envelopes[^1].Bytes));
        Assert.Equal(ConnectionCommandKind.Close, harness.Transport.Commands[^1]);
        Assert.True(
            harness.Egress.Envelopes.Count > 0
            && harness.Transport.Commands.LastIndexOf(ConnectionCommandKind.Close)
                >= harness.Transport.Commands.FindIndex(kind => kind == ConnectionCommandKind.Close));
        Assert.Equal(ServerConnectionSessionState.Kicked, session.State);
        Assert.Equal(1, harness.Slot.ReleaseCalls);
        Assert.Null(session.PendingTimer);

        Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.MaintenanceKick)).Accepted);
        harness.Registry.PumpOnce();

        Assert.Equal(ServerConnectionSessionState.Kicked, session.State);
        Assert.DoesNotContain(
            harness.Trace.States,
            state => state.SessionState == nameof(ServerConnectionSessionState.ReconnectWindow));
        Assert.Contains(harness.DrainEventsOfType<SessionEvent.Kicked>(), _ => true);
        Assert.Single(harness.ObservabilityAudit);
    }

    [Fact]
    public void KickWithLiveBindingRefusesLaterReconnect()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = Assert.IsType<SessionBinding>(session.Binding);

        Assert.True(harness.Registry.Admin!.Kick(session.SessionId, "MaintenanceKick").Accepted);
        harness.Registry.PumpOnce();
        Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.MaintenanceKick)).Accepted);
        harness.Registry.PumpOnce();

        harness.Enqueue(Candidate(2, "session-1"));

        Assert.Equal(ServerConnectionSessionState.Kicked, session.State);
        Assert.Null(session.Binding);
        Assert.False(
            harness.Registry.TryGet(new ServerSessionId("session-1"), out var remaining)
            && remaining.State is ServerConnectionSessionState.Syncing
                or ServerConnectionSessionState.Active
                or ServerConnectionSessionState.ReconnectWindow);
        Assert.Contains(ConnectionCloseReason.PolicyReject, harness.Transport.CloseReasons);
        Assert.Contains(
            harness.DrainEventsOfType<SessionEvent.Rejected>(),
            rejected => rejected.StableErrorId == "SessionMismatch");
    }

    [Fact]
    public void AuthBusyRetriesWithinAdmissionAttemptBudgetThenSucceeds()
    {
        using var harness = new SessionHarness();
        harness.Auth.Script(
            BusyOutcome("AuthBusy"),
            BusyOutcome("AuthBusy"),
            new AuthenticateOutcome(
                CredentialVerdict.Accepted,
                new PrincipalId("principal-1"),
                AntiReplayVerdict.Ok,
                null,
                null));

        harness.Enqueue(Candidate(1, "session-1"));

        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        Assert.Equal(ServerConnectionSessionState.Syncing, session.State);
        Assert.Equal(3, harness.Auth.AuthenticateCalls);
        Assert.DoesNotContain(
            harness.DrainEventsOfType<SessionEvent.Rejected>(),
            rejected => rejected.StableErrorId is "AuthBusy" or "AggregateBusy");
    }

    [Fact]
    public void AuthBusyExhaustsAdmissionAttemptBudgetThenStableReject()
    {
        using var harness = new SessionHarness();
        harness.Auth.Script(
            BusyOutcome("AuthBusy"),
            BusyOutcome("AuthBusy"),
            BusyOutcome("AuthBusy"));

        harness.Enqueue(Candidate(1, "session-1"));

        Assert.False(harness.Registry.TryGet(new ServerSessionId("session-1"), out _));
        Assert.Equal(3, harness.Auth.AuthenticateCalls);
        var rejected = Assert.Single(harness.DrainEventsOfType<SessionEvent.Rejected>());
        Assert.Equal("QueueFull", rejected.StableErrorId);
        Assert.DoesNotContain("AuthBusy", harness.Egress.Envelopes.Select(envelope =>
        {
            try
            {
                return ReadReasonCode(envelope.Bytes);
            }
            catch (Exception)
            {
                return ReadType(envelope.Bytes);
            }
        }));
    }

    [Fact]
    public void AggregateBusyRetriesWithinAdmissionAttemptBudgetThenSucceeds()
    {
        using var harness = new SessionHarness();
        var busy = new SessionReservationResult(false, default, new SlotEpoch(1), new WorldSlotId(1), "AggregateBusy");
        var ok = new SessionReservationResult(true, new SlotReservationId(7), new SlotEpoch(1), new WorldSlotId(1), null);
        harness.Slot.ScriptReserve(busy, busy, ok);

        harness.Enqueue(Candidate(1, "session-1"));

        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        Assert.Equal(ServerConnectionSessionState.Syncing, session.State);
        Assert.Equal(3, harness.Slot.ReserveCalls);
        Assert.Equal(new SlotReservationId(7), harness.Slot.LastReservation);
    }

    [Fact]
    public void AggregateBusyExhaustsAdmissionAttemptBudgetThenStableReject()
    {
        using var harness = new SessionHarness();
        var busy = new SessionReservationResult(false, default, new SlotEpoch(1), new WorldSlotId(1), "AggregateBusy");
        harness.Slot.ScriptReserve(busy, busy, busy);

        harness.Enqueue(Candidate(1, "session-1"));

        Assert.False(harness.Registry.TryGet(new ServerSessionId("session-1"), out _));
        Assert.Equal(3, harness.Slot.ReserveCalls);
        var rejected = Assert.Single(harness.DrainEventsOfType<SessionEvent.Rejected>());
        Assert.Equal("QueueFull", rejected.StableErrorId);
    }

    [Fact]
    public void ActiveSessionControlInboxFullIsolatesTheSession()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        Assert.Equal(ServerConnectionSessionState.Active, session.State);

        var inbox = harness.Registry.ControlInboxForTest;
        SessionCommand filler = new SessionCommand.TimerFired(new TimerId(99), new ServerSessionId("missing"));
        for (var i = 0; i < SessionProvisionalDefaults.ControlInboxMaxItems; i++)
        {
            Assert.Equal(EnqueueStatus.Accepted, inbox.TryEnqueue(in filler).Status);
        }

        var result = harness.Registry.Kick(session.SessionId, "MaintenanceKick");

        Assert.True(result.Accepted);
        Assert.Equal(ServerConnectionSessionState.Kicked, session.State);
        Assert.Null(session.Binding);
        Assert.Contains(ConnectionCloseReason.MaintenanceKick, harness.Transport.CloseReasons);
    }

    [Fact]
    public void ActiveSessionClosedIsNotDroppedWhenOwnerIngressIsFull()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = Assert.IsType<SessionBinding>(session.Binding);

        for (ulong id = 1000; id < 1000 + (ulong)SessionProvisionalDefaults.ControlInboxMaxItems; id++)
        {
            Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
                new TransportConnectionId(id),
                new ConnectionEpoch(0),
                ConnectionCloseReason.Disconnect)).Accepted);
        }

        var result = harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.Disconnect));

        Assert.True(result.Accepted);
        Assert.NotEqual("QueueFull", result.StableErrorId);
        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, session.State);
        Assert.Null(session.Binding);
    }

    [Fact]
    public void ActiveSessionInboundIsIsolatedWhenOwnerIngressIsFull()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = Assert.IsType<SessionBinding>(session.Binding);

        for (ulong id = 2000; id < 2000 + (ulong)SessionProvisionalDefaults.ControlInboxMaxItems; id++)
        {
            Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
                new TransportConnectionId(id),
                new ConnectionEpoch(0),
                ConnectionCloseReason.Disconnect)).Accepted);
        }

        var inbound = Validated(MvpEnvelopeWriter.WriteResyncRequest(Context("session-1", 99), "gap"));
        var result = default(AckResult);
        var worker = new Thread(() => result = harness.Registry.HandleInbound(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            in inbound));
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));

        Assert.True(result.Accepted);
        Assert.NotEqual("QueueFull", result.StableErrorId);
        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, session.State);
        Assert.Null(session.Binding);
    }

    [Fact]
    public void AdmissionSagaEightStepsTest()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));

        Assert.Equal(
            TracedAdmissionEffects,
            harness.Trace.Acks.Select(ack => ack.Effect).ToArray());
        Assert.True(harness.Trace.Acks.Select(ack => ack.AdmissionAttemptId).Distinct().Count() == 1);
    }

    [Theory]
    [InlineData(AdmissionEffectKind.ReadGate)]
    [InlineData(AdmissionEffectKind.Authenticate)]
    [InlineData(AdmissionEffectKind.MatchExactRelease)]
    [InlineData(AdmissionEffectKind.ReserveSlot)]
    [InlineData(AdmissionEffectKind.CommitSlot)]
    [InlineData(AdmissionEffectKind.CreateSession)]
    [InlineData(AdmissionEffectKind.BindConnection)]
    [InlineData(AdmissionEffectKind.StartReplication)]
    public void ExactlyOnceCompensationTest(AdmissionEffectKind failedEffect)
    {
        using var harness = new SessionHarness();
        InjectAdmissionFailure(harness, failedEffect);
        var compensateCount = harness.Trace.Acks.Count(ack => ack.Effect == "Compensate");
        Assert.Equal(1, compensateCount);
        Assert.False(harness.Registry.TryGet(new ServerSessionId("session-1"), out var live)
            && live.State is ServerConnectionSessionState.Syncing or ServerConnectionSessionState.Active);

        var attemptId = harness.Trace.Acks.First(ack => ack.AdmissionAttemptId is not null).AdmissionAttemptId!.Value;
        harness.Enqueue(new SessionCommand.DependencyResult(
            new AdmissionAttemptId(attemptId),
            failedEffect,
            false,
            "QueueFull"));

        Assert.Equal(compensateCount, harness.Trace.Acks.Count(ack => ack.Effect == "Compensate"));
    }

    [Fact]
    public void ServerOutboundMessageTypeSubsetTest()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.NotifyAuthorityRevision(1).Accepted);
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        Assert.True(harness.Registry.Admin!.Kick(session.SessionId, "MaintenanceKick").Accepted);
        harness.Registry.PumpOnce();

        var types = harness.Egress.Envelopes.Select(envelope => ReadType(envelope.Bytes)).Distinct().ToArray();
        Assert.All(types, type => Assert.Contains(type, ServerOutboundMessageTypes));
        Assert.DoesNotContain("BaselineAck", types);
        Assert.DoesNotContain("DeltaAck", types);
        Assert.DoesNotContain("ResyncRequest", types);
    }

    [Fact]
    public void OutboundReliabilityIsReliableTest()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.NotifyAuthorityRevision(1).Accepted);
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        Assert.True(harness.Registry.Admin!.Kick(session.SessionId, "MaintenanceKick").Accepted);
        harness.Registry.PumpOnce();

        foreach (var envelope in harness.Egress.Envelopes)
        {
            Assert.True(MvpEnvelopeReader.TryReadHeader(envelope.Bytes.Span, out var header).Status == EnvelopeParseStatus.Ok);
            Assert.Equal(MvpWireConstants.Reliability, header.Reliability);
        }
    }

    [Fact]
    public void KickWithoutLiveBindingTransitionsToKicked()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = Assert.IsType<SessionBinding>(session.Binding);
        Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.Disconnect)).Accepted);
        harness.Registry.PumpOnce();

        Assert.True(harness.Registry.Admin!.Kick(session.SessionId, "MaintenanceKick").Accepted);
        harness.Registry.PumpOnce();

        Assert.Equal(ServerConnectionSessionState.Kicked, session.State);
        Assert.Equal(1, harness.Slot.ReleaseCalls);
    }

    [Fact]
    public void KickWaitsForTerminalEnvelopeAdmissionBeforeClosingTransport()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.Egress.Script(
            new EnqueueResult(EnqueueStatus.Full, "QueueFull"),
            new EnqueueResult(EnqueueStatus.Accepted, null));

        Assert.True(harness.Registry.Admin!.Kick(
            new ServerSessionId("session-1"),
            "MaintenanceKick").Accepted);
        harness.Registry.PumpOnce();

        Assert.Empty(harness.Transport.CloseReasons);
        Assert.DoesNotContain(harness.Egress.Envelopes, envelope => ReadType(envelope.Bytes) == "MaintenanceKick");

        harness.Registry.PumpOnce();

        Assert.Equal("MaintenanceKick", ReadType(harness.Egress.Envelopes[^1].Bytes));
        Assert.Equal(ConnectionCloseReason.MaintenanceKick, harness.Transport.CloseReasons[^1]);
    }

    [Fact]
    public void TerminalEnvelopeRetryConvergesWhenConnectionAlreadyClosed()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = Assert.IsType<SessionBinding>(session.Binding);
        harness.Egress.Script(
            new EnqueueResult(EnqueueStatus.Full, "QueueFull"),
            new EnqueueResult(EnqueueStatus.Closed, "StaleConnectionGeneration"));

        Assert.True(harness.Registry.Admin!.Kick(
            new ServerSessionId("session-1"),
            "MaintenanceKick").Accepted);
        harness.Registry.PumpOnce();
        Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.Disconnect)).Accepted);

        harness.Registry.PumpOnce();
        Assert.True(harness.Registry.Admin.Kick(session.SessionId, "MaintenanceKick").Accepted);
        harness.Registry.PumpOnce();

        Assert.Equal(ServerConnectionSessionState.Kicked, session.State);
    }

    [Fact]
    public void DuplicateKickConvergesWhenFirstCloseAlreadyRetiredConnection()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        harness.Egress.Script(
            new EnqueueResult(EnqueueStatus.Accepted, null),
            new EnqueueResult(EnqueueStatus.Closed, "StaleConnectionGeneration"));

        Assert.True(harness.Registry.Admin!.Kick(session.SessionId, "MaintenanceKick").Accepted);
        Assert.True(harness.Registry.Admin.Kick(session.SessionId, "MaintenanceKick").Accepted);

        harness.Registry.PumpOnce();

        Assert.Equal(ServerConnectionSessionState.Kicked, session.State);
        Assert.Null(session.Binding);
        Assert.Single(
            harness.Egress.Envelopes,
            envelope => ReadType(envelope.Bytes) == "MaintenanceKick");
        Assert.Equal(
            new[] { ConnectionCloseReason.MaintenanceKick },
            harness.Transport.CloseReasons);
    }

    [Fact]
    public void BeginDrainCannotBypassABackpressuredKickEnvelope()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.Egress.Script(
            new EnqueueResult(EnqueueStatus.Full, "QueueFull"),
            new EnqueueResult(EnqueueStatus.Full, "QueueFull"),
            new EnqueueResult(EnqueueStatus.Accepted, null));

        Assert.True(harness.Registry.Admin!.Kick(
            new ServerSessionId("session-1"),
            "MaintenanceKick").Accepted);
        harness.Registry.PumpOnce();
        Assert.Empty(harness.Transport.CloseReasons);

        Assert.True(harness.Registry.Admin!.BeginDrain(new MonotonicInstant(long.MaxValue)).Accepted);
        harness.Registry.PumpOnce();

        Assert.Empty(harness.Transport.CloseReasons);
        Assert.DoesNotContain(
            harness.Egress.Envelopes,
            envelope => ReadType(envelope.Bytes) == "MaintenanceKick");

        harness.Registry.PumpOnce();

        Assert.Equal("MaintenanceKick", ReadType(harness.Egress.Envelopes[^1].Bytes));
        Assert.Equal(
            new[] { ConnectionCloseReason.MaintenanceKick },
            harness.Transport.CloseReasons);
    }

    [Fact]
    public void SameBatchDrainCannotBypassABackpressuredKickEnvelope()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.Egress.Script(
            new EnqueueResult(EnqueueStatus.Full, "QueueFull"),
            new EnqueueResult(EnqueueStatus.Accepted, null));

        Assert.True(harness.Registry.Admin!.Kick(
            new ServerSessionId("session-1"),
            "MaintenanceKick").Accepted);
        Assert.True(harness.Registry.Admin.BeginDrain(new MonotonicInstant(long.MaxValue)).Accepted);

        harness.Registry.PumpOnce();

        Assert.Empty(harness.Transport.CloseReasons);
        Assert.DoesNotContain(
            harness.Egress.Envelopes,
            envelope => ReadType(envelope.Bytes) == "MaintenanceKick");

        harness.Registry.PumpOnce();

        Assert.Equal("MaintenanceKick", ReadType(harness.Egress.Envelopes[^1].Bytes));
        Assert.Equal(
            new[] { ConnectionCloseReason.MaintenanceKick },
            harness.Transport.CloseReasons);
    }

    [Fact]
    public void PersistentCloseRetryDoesNotBlockHealthySessionCommands()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.Enqueue(Candidate(2, "session-2"));
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var isolated));
        var baselineId = ReadSnapshotId(harness.Egress.Envelopes[0].Bytes);
        Assert.True(harness.Registry.NotifyAuthorityRevision(1).Accepted);

        harness.Egress.Script(new EnqueueResult(EnqueueStatus.Full, "QueueFull"));
        harness.Transport.Script(
            new EnqueueResult(EnqueueStatus.Full, "QueueFull"),
            new EnqueueResult(EnqueueStatus.Full, "QueueFull"));
        var baselineAck = Validated(MvpEnvelopeWriter.WriteBaselineAck(
            Context("session-1", 101), baselineId, 0));
        Assert.True(harness.Registry.HandleInbound(in baselineAck).Accepted);
        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, isolated.State);

        Assert.True(harness.Registry.Admin!.Kick(
            new ServerSessionId("session-2"),
            "MaintenanceKick").Accepted);
        harness.Registry.PumpOnce();

        Assert.Contains(
            harness.Egress.Envelopes,
            envelope => ReadType(envelope.Bytes) == "MaintenanceKick");
        Assert.Contains(ConnectionCloseReason.MaintenanceKick, harness.Transport.CloseReasons);
    }

    [Fact]
    public void PolicyRejectWaitsForErrorEnvelopeAdmissionBeforeClosingTransport()
    {
        using var harness = new SessionHarness();
        harness.Egress.Script(
            new EnqueueResult(EnqueueStatus.Full, "QueueFull"),
            new EnqueueResult(EnqueueStatus.Accepted, null));

        var mismatchContext = Context("session-1", 1) with { ProductId = "wrong" };
        harness.Enqueue(new SessionCommand.ConnectionCandidate(
            new TransportConnectionId(1),
            new ConnectionEpoch(0),
            Validated(MvpEnvelopeWriter.WriteClientHandshake(mismatchContext))));

        Assert.Empty(harness.Transport.CloseReasons);
        Assert.Empty(harness.Egress.Envelopes);

        harness.Registry.PumpOnce();

        Assert.Equal("Error", ReadType(harness.Egress.Envelopes[^1].Bytes));
        Assert.Equal("ReleaseMismatch", ReadReasonCode(harness.Egress.Envelopes[^1].Bytes));
        Assert.Equal(ConnectionCloseReason.PolicyReject, harness.Transport.CloseReasons[^1]);
    }

    [Fact]
    public void FullSnapshotCompensationClosesWithThePostUnbindEpoch()
    {
        using var harness = new SessionHarness();
        harness.Egress.Script(new EnqueueResult(EnqueueStatus.Full, "QueueFull"));

        harness.Enqueue(Candidate(1, "session-1"));

        var unbind = Assert.Single(harness.Transport.SentCommands.OfType<ConnectionCommand.Unbind>());
        var close = Assert.Single(harness.Transport.SentCommands.OfType<ConnectionCommand.Close>());
        Assert.Equal(unbind.Id, close.Id);
        Assert.Equal(new ConnectionEpoch(unbind.Epoch.Value + 1), close.Epoch);
        Assert.True(
            harness.Transport.SentCommands.IndexOf(unbind)
            < harness.Transport.SentCommands.IndexOf(close));
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
        harness.Registry.PumpOnce();
        var timer = harness.Timers.Scheduled[^1].Id;

        var stale = new SessionCommand.TimerFired(new TimerId(timer.Value + 1), session.SessionId);
        harness.Enqueue(stale);
        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, session.State);

        var due = new SessionCommand.TimerFired(timer, session.SessionId);
        harness.Enqueue(due);
        Assert.Equal(ServerConnectionSessionState.Expired, session.State);
        Assert.Null(session.ReplicationContext);
    }

    [Fact]
    public void ScheduledReconnectTimerPayloadCarriesANonzeroFencingIdentity()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = Assert.IsType<SessionBinding>(session.Binding);

        Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.Disconnect)).Accepted);
        harness.Registry.PumpOnce();

        var scheduled = harness.Timers.Scheduled[^1];
        var payload = Assert.IsType<SessionCommand.TimerFired>(scheduled.Command);
        Assert.NotEqual(new TimerId(0), payload.Timer);
        Assert.Equal(session.SessionId, payload.SessionId);
    }

    [Fact]
    public void OldScheduledTimerPayloadCannotExpireANewerReconnectWindow()
    {
        using var harness = new SessionHarness();
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var firstBinding = Assert.IsType<SessionBinding>(session.Binding);
        Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            firstBinding.ConnectionId,
            firstBinding.ConnectionEpoch,
            ConnectionCloseReason.Disconnect)).Accepted);
        harness.Registry.PumpOnce();
        var oldPayload = Assert.IsType<SessionCommand.TimerFired>(harness.Timers.Scheduled[^1].Command);

        harness.Enqueue(Candidate(2, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        var secondBinding = Assert.IsType<SessionBinding>(session.Binding);
        Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            secondBinding.ConnectionId,
            secondBinding.ConnectionEpoch,
            ConnectionCloseReason.Disconnect)).Accepted);
        harness.Registry.PumpOnce();
        var newerTimer = Assert.IsType<TimerId>(session.PendingTimer);

        harness.Enqueue(oldPayload);

        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, session.State);
        Assert.Equal(newerTimer, session.PendingTimer);
        Assert.NotNull(session.ReplicationContext);
    }

    [Fact]
    public void TerminalSessionChurnRetiresOrBoundsRegistryEntries()
    {
        using var harness = new SessionHarness();
        var churn = SessionProvisionalDefaults.EventOutboxMaxItems + 1;

        for (var index = 1; index <= churn; index++)
        {
            var sessionId = $"session-{index}";
            harness.Enqueue(Candidate((ulong)index, sessionId));
            Assert.True(harness.Registry.TryGet(new ServerSessionId(sessionId), out var session));
            var binding = Assert.IsType<SessionBinding>(session.Binding);
            Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
                binding.ConnectionId,
                binding.ConnectionEpoch,
                ConnectionCloseReason.Disconnect)).Accepted);
            harness.Registry.PumpOnce();
            var timer = Assert.IsType<TimerId>(session.PendingTimer);

            harness.Enqueue(new SessionCommand.TimerFired(timer, session.SessionId));

            Assert.Equal(ServerConnectionSessionState.Expired, session.State);
            harness.DrainEvents();
        }

        Assert.True(
            harness.Registry.SessionCount <= SessionProvisionalDefaults.EventOutboxMaxItems,
            $"terminal session registry retained {harness.Registry.SessionCount} entries");
        Assert.False(harness.Registry.TryGet(new ServerSessionId("session-1"), out _));
    }

    private static AuthenticateOutcome BusyOutcome(string busyCode)
        => new(
            CredentialVerdict.Rejected,
            default,
            AntiReplayVerdict.Ok,
            busyCode,
            null);

    private static void InjectAdmissionFailure(SessionHarness harness, AdmissionEffectKind failedEffect)
    {
        switch (failedEffect)
        {
            case AdmissionEffectKind.ReadGate:
                harness.Slot.GateState = AdmissionGateState.Closed;
                harness.Enqueue(Candidate(1, "session-1"));
                break;
            case AdmissionEffectKind.Authenticate:
                harness.Auth.Script(new AuthenticateOutcome(
                    CredentialVerdict.Rejected,
                    default,
                    AntiReplayVerdict.Ok,
                    "RoleMismatch",
                    null));
                harness.Enqueue(Candidate(1, "session-1"));
                break;
            case AdmissionEffectKind.MatchExactRelease:
                harness.Enqueue(Candidate(1, "session-1") with
                {
                    Handshake = Validated(MvpEnvelopeWriter.WriteClientHandshake(
                        Context("session-1", 1) with { ProductId = "wrong" })),
                });
                break;
            case AdmissionEffectKind.ReserveSlot:
                harness.Slot.Reservation = default;
                harness.Enqueue(Candidate(1, "session-1"));
                break;
            case AdmissionEffectKind.CommitSlot:
                harness.Slot.BindResult = new AckResult(false, "CapacityExceeded");
                harness.Enqueue(Candidate(1, "session-1"));
                break;
            case AdmissionEffectKind.CreateSession:
                harness.Enqueue(Candidate(1, "session-1"));
                Assert.True(harness.Registry.Admin!.Kick(new ServerSessionId("session-1"), "MaintenanceKick").Accepted);
                harness.Registry.PumpOnce();
                harness.Enqueue(Candidate(2, "session-1"));
                break;
            case AdmissionEffectKind.BindConnection:
                harness.Transport.Script(new EnqueueResult(EnqueueStatus.Full, "QueueFull"));
                harness.Enqueue(Candidate(1, "session-1"));
                break;
            case AdmissionEffectKind.StartReplication:
                harness.Egress.Script(new EnqueueResult(EnqueueStatus.Full, "QueueFull"));
                harness.Enqueue(Candidate(1, "session-1"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failedEffect), failedEffect, "Unsupported admission effect");
        }
    }

    private static SessionCommand.ConnectionCandidate Candidate(ulong connection, string session)
        => new(
            new TransportConnectionId(connection),
            new ConnectionEpoch(0),
            Validated(MvpEnvelopeWriter.WriteClientHandshake(Context(session, connection))));

    private static (
        ServerConnectionSession Session,
        ulong LastDeltaRevision,
        PermissionGrantRef FirstGrant) EnterReconnectWindowAfterOutstandingDelta(SessionHarness harness)
    {
        harness.Enqueue(Candidate(1, "session-1"));
        harness.AcknowledgeBaseline("session-1");
        Assert.True(harness.Registry.NotifyAuthorityRevision(1).Accepted);
        var delta = harness.Egress.Envelopes[^1].Bytes;
        Assert.Equal("Delta", ReadType(delta));
        var lastDeltaRevision = ReadRevision(delta, "toRevision");
        Assert.True(harness.Registry.TryGet(new ServerSessionId("session-1"), out var session));
        var binding = Assert.IsType<SessionBinding>(session.Binding);

        Assert.True(harness.Registry.HandleConnectionEvent(new ConnectionEvent.Closed(
            binding.ConnectionId,
            binding.ConnectionEpoch,
            ConnectionCloseReason.Disconnect)).Accepted);
        harness.Registry.PumpOnce();
        Assert.Equal(ServerConnectionSessionState.ReconnectWindow, session.State);

        return (session, lastDeltaRevision, binding.Grant);
    }

    private static bool SessionRegistryRetainsReference(SessionRegistry registry, object target)
    {
        foreach (var field in typeof(SessionRegistry).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (field.GetValue(registry) is not System.Collections.IDictionary dictionary)
            {
                continue;
            }

            foreach (var value in dictionary.Values)
            {
                if (ObjectGraphContainsReference(value, target, depth: 4))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int ReadPrivateQueueCount(object instance, string fieldName)
    {
        var field = typeof(SessionRegistry).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(instance) is System.Collections.ICollection collection
            ? collection.Count
            : 0;
    }

    private static int ReadPrivateDictionaryCount(object instance, string fieldName)
    {
        var field = typeof(SessionRegistry).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(instance) is System.Collections.IDictionary dictionary
            ? dictionary.Count
            : 0;
    }

    private static System.Collections.IDictionary ReadPrivateDictionary(object instance, string fieldName)
        => (System.Collections.IDictionary)typeof(SessionRegistry)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)!;

    private static bool ObjectGraphContainsReference(object? value, object target, int depth)
    {
        if (value is null || depth < 0)
        {
            return false;
        }

        if (ReferenceEquals(value, target))
        {
            return true;
        }

        var type = value.GetType();
        if (type.IsValueType || value is string)
        {
            return false;
        }

        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            foreach (var child in current
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Select(fieldInfo => fieldInfo.GetValue(value)))
            {
                if (ObjectGraphContainsReference(child, target, depth - 1))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ValidatedEnvelopeBytes Validated(ReadOnlyMemory<byte> bytes)
    {
        var parsed = MvpEnvelopeReader.TryReadHeader(bytes.Span, out var header);
        Assert.Equal(EnvelopeParseStatus.Ok, parsed.Status);
        return new ValidatedEnvelopeBytes(bytes, header);
    }

    private static void CorruptClientRole(byte[] bytes)
    {
        var marker = Encoding.UTF8.GetBytes("\"role\":\"Client\"");
        var offset = bytes.AsSpan().IndexOf(marker);
        Assert.True(offset >= 0);
        bytes[offset + marker.Length - 7] = (byte)'X';
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

    private static ulong ReadRevision(ReadOnlyMemory<byte> bytes, string field)
    {
        using var document = System.Text.Json.JsonDocument.Parse(bytes);
        var body = document.RootElement.GetProperty("body");
        if (body.TryGetProperty(field, out var direct))
        {
            return direct.GetUInt64();
        }

        return body.GetProperty("sessionRevisionVector").GetProperty(field).GetUInt64();
    }

    private static string ReadReasonCode(ReadOnlyMemory<byte> bytes)
    {
        using var document = System.Text.Json.JsonDocument.Parse(bytes);
        return document.RootElement
            .GetProperty("body")
            .GetProperty("reasonCode")
            .GetString()!;
    }

    private static string[] PublicDeclaredProperties(Type type)
        => type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

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
            Events = PlatformModule.CreateInbox<SessionEvent>(new QueueBudget(256, 65536));
            Registry = SessionRegistry.Create(
                Slot,
                Auth,
                Transport,
                Egress,
                testControl ? Mutation : null,
                Clock,
                Timers,
                controls,
                PlatformModule.CreateOutbox(Events),
                Observability,
                new SessionHostConfiguration("A", "A-1.1.0", "pool-a", 10, 3, testControl));
            Registry.AttachEventInbox(Events);
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
        internal IBoundedInbox<SessionEvent> Events { get; }
        internal SessionRegistry Registry { get; }

        internal void Enqueue(SessionCommand command)
        {
            var inbox = RegistryControl;
            Assert.Equal(EnqueueStatus.Accepted, inbox.TryEnqueue(in command).Status);
            Registry.PumpOnce();
        }

        internal AckResult EnqueueAuthenticated(
            ulong connection,
            string session,
            TestAuthenticatedMetadata evidence)
        {
            var connectionId = new TransportConnectionId(connection);
            var epoch = new ConnectionEpoch(0);
            if (evidence.TransportConnectionId != connectionId
                || evidence.ConnectionEpoch != epoch
                || !evidence.TryConsume())
            {
                _ = Transport.TrySend(new ConnectionCommand.Close(
                    connectionId,
                    epoch,
                    ConnectionCloseReason.PolicyReject));
                return new AckResult(false, "StaleConnectionGeneration");
            }

            var handshake = new ConnectionEvent.HandshakeEnvelope(
                connectionId,
                epoch,
                Validated(MvpEnvelopeWriter.WriteClientHandshake(Context(session, connection))));
            var result = Registry.HandleAuthenticatedConnectionEvent(
                in handshake,
                evidence.PrincipalId,
                evidence.ProductId,
                evidence.GameReleaseId);
            if (result.Accepted)
            {
                Registry.PumpOnce();
            }

            return result;
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

        internal void DrainEvents()
        {
            while (Events.TryDequeue(out _))
            {
            }

            while (Registry.TryDequeueTerminal(out _))
            {
            }
        }

        internal List<TEvent> DrainEventsOfType<TEvent>()
            where TEvent : SessionEvent
        {
            var found = new List<TEvent>();
            while (Registry.TryDequeueEvent(out var sessionEvent))
            {
                if (sessionEvent is TEvent typed)
                {
                    found.Add(typed);
                }
            }

            return found;
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
        private Queue<EnqueueResult> Results { get; } = new();

        internal List<ConnectionCommandKind> Commands { get; } = new();
        internal List<ConnectionCommand> SentCommands { get; } = new();
        internal List<ConnectionCloseReason> CloseReasons { get; } = new();
        internal Action<ConnectionCommand>? OnTrySend { get; set; }

        internal void Script(params EnqueueResult[] results)
        {
            foreach (var result in results)
            {
                Results.Enqueue(result);
            }
        }

        public EnqueueResult TrySend(in ConnectionCommand command)
        {
            SentCommands.Add(command);
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
            this.OnTrySend?.Invoke(command);
            return Results.TryDequeue(out var scripted)
                ? scripted
                : new EnqueueResult(EnqueueStatus.Accepted, null);
        }
    }

    private sealed class FakeEgress : IEgressWriter
    {
        internal List<OutboundEnvelopeBytes> Envelopes { get; } = new();

        private Queue<EnqueueResult> Results { get; } = new();

        internal void Script(params EnqueueResult[] results)
        {
            foreach (var result in results)
            {
                Results.Enqueue(result);
            }
        }

        public EnqueueResult TryEnqueue(TransportConnectionId c, ConnectionEpoch e, in OutboundEnvelopeBytes envelope)
        {
            if (Results.TryDequeue(out var scripted) && scripted.Status != EnqueueStatus.Accepted)
            {
                return scripted;
            }

            Envelopes.Add(envelope);
            return new EnqueueResult(EnqueueStatus.Accepted, null);
        }
    }

    private sealed class FakeAuth : IAuthorizationService
    {
        private ulong grants;
        internal int AuthenticateCalls { get; private set; }
        internal int AuthorizeCalls { get; private set; }
        internal PrincipalId? LastAuthorizedPrincipal { get; private set; }
        internal PrincipalId NextPrincipal { get; set; } = new("principal-1");
        private readonly Queue<AuthenticateOutcome> scriptedOutcomes = new();

        public bool AdmissionMustStop => false;

        internal void Script(params AuthenticateOutcome[] outcomes)
        {
            foreach (var outcome in outcomes)
            {
                scriptedOutcomes.Enqueue(outcome);
            }
        }

        public AuthenticateOutcome Authenticate(in AuthenticateCommand command)
        {
            AuthenticateCalls++;
            if (scriptedOutcomes.TryDequeue(out var scripted))
            {
                return scripted;
            }

            return new AuthenticateOutcome(
                CredentialVerdict.Accepted,
                NextPrincipal,
                AntiReplayVerdict.Ok,
                null,
                null);
        }

        public PermissionGrant Authorize(PrincipalId principal, in SessionScope scope)
        {
            AuthorizeCalls++;
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

    private sealed class FakeSlot
        : IWorldSlotHost, ISessionWorldSlotPort
    {
        private readonly HashSet<string> boundSessions = new(StringComparer.Ordinal);
        internal AdmissionGateState GateState { get; set; } = AdmissionGateState.Open;
        internal AckResult BindResult { get; set; } = new(true, null);
        internal int BindCalls { get; private set; }
        internal int ReleaseCalls { get; private set; }
        private Queue<AckResult> AbortResults { get; } = new();
        internal SlotReservationId Reservation { get; set; } = new(7);
        internal SlotReservationId LastReservation { get; private set; }
        internal int ReserveCalls { get; private set; }
        private readonly Queue<SessionReservationResult> scriptedReservations = new();

        public AllocateResult Allocate(in SlotBudget budget) => new(true, new WorldSlotId(1), new SlotEpoch(1), null);

        internal void ScriptReserve(params SessionReservationResult[] results)
        {
            foreach (var result in results)
            {
                scriptedReservations.Enqueue(result);
            }
        }

        public SessionReservationResult ReserveAdmission(AdmissionAttemptId attempt, ServerSessionId session)
        {
            ReserveCalls++;
            if (scriptedReservations.TryDequeue(out var scripted))
            {
                return scripted;
            }

            return new SessionReservationResult(
                Reservation.Value != 0,
                Reservation,
                new SlotEpoch(1),
                new WorldSlotId(1),
                Reservation.Value == 0 ? "InvalidArgument" : null);
        }
        public AckResult AbortAdmission(SlotReservationId reservation, SlotEpoch epoch)
            => new(true, null);

        public AckResult ReleaseCommittedReservation(
            SlotReservationId reservation,
            ServerSessionId session,
            SlotEpoch epoch)
        {
            ReleaseCalls++;
            var result = AbortResults.TryDequeue(out var scripted)
                ? scripted
                : new AckResult(true, null);
            if (result.Accepted)
            {
                boundSessions.Remove(session.Value);
            }

            return result;
        }
        internal void ScriptAbort(params AckResult[] results)
        {
            foreach (var result in results)
            {
                AbortResults.Enqueue(result);
            }
        }
        public AckResult BindSession(SlotReservationId reservation, ServerSessionId session, SlotEpoch epoch)
        {
            BindCalls++;
            LastReservation = reservation;
            if (BindResult.Accepted)
            {
                boundSessions.Add(session.Value);
            }

            return BindResult;
        }
        public AckResult Quiesce(string reason, SlotEpoch epoch) => new(true, null);
        public SnapshotCutRef FixSnapshotCut(SlotEpoch epoch) => new(1);
        public AckResult Destroy(SlotEpoch epoch) => new(true, null);
        public AdmissionGateState Gate => GateState;
        public QuotaView Capacity => new(16, boundSessions.Count);
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

// Session tests use local authenticated metadata so the production Session assembly
// never needs a dependency on the transport-owned authentication side channel.
internal sealed class TestAuthenticatedMetadata
{
    private int consumed;

    internal TestAuthenticatedMetadata(
        PrincipalId principalId,
        TransportConnectionId transportConnectionId,
        ConnectionEpoch connectionEpoch,
        string productId,
        string gameReleaseId)
    {
        PrincipalId = principalId;
        TransportConnectionId = transportConnectionId;
        ConnectionEpoch = connectionEpoch;
        ProductId = productId;
        GameReleaseId = gameReleaseId;
    }

    internal PrincipalId PrincipalId { get; }
    internal TransportConnectionId TransportConnectionId { get; }
    internal ConnectionEpoch ConnectionEpoch { get; }
    internal string ProductId { get; }
    internal string GameReleaseId { get; }

    internal bool TryConsume()
        => Interlocked.Exchange(ref consumed, 1) == 0;
}
