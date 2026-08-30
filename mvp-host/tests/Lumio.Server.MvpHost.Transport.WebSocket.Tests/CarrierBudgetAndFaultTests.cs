using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.TestKit;
using Lumio.Server.MvpHost.Transport.WebSocket;
using Lumio.Server.MvpHost.Transport;
using Xunit;

namespace Lumio.Server.MvpHost.Transport.WebSocket.Tests;

public sealed class CarrierBudgetAndFaultTests
{
    [Fact]
    public async Task OversizeAbortedBeforeBufferGrowthTest()
    {
        var clock = new FakeMonotonicClock();
        using var timers = new RecordingTimers();
        using var carrier = CreateCarrier(
            new WebSocketCarrierOptions(
                "ws://127.0.0.1:0/", false, true, "LocalSplitProcess", 64, 2, 15, "A", "A-1.1.0", "pool"),
            clock,
            timers,
            new AcceptingVerifier(),
            new AcceptingReplay(),
            new RecordingAudit());
        using var client = await ConnectAsync(carrier);

        var accepted = await carrier.AcceptAsync(CancellationToken.None);
        var oversized = new byte[256];
        await client.SendAsync(oversized, WebSocketMessageType.Text, true, CancellationToken.None);

        var receive = await carrier.ReceiveAsync(
            accepted.ConnectionId,
            new byte[8192],
            new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        Assert.False(receive.Received);
        Assert.True(receive.Closed);
        Assert.Equal(0, receive.ByteCount);
    }

    [Fact]
    public async Task FragmentedTextFramesAreOneBoundedMessageTest()
    {
        var clock = new FakeMonotonicClock();
        using var timers = new RecordingTimers();
        using var carrier = CreateCarrier(DefaultOptions(4096), clock, timers, new AcceptingVerifier(), new AcceptingReplay(), new RecordingAudit());
        using var client = await ConnectAsync(carrier);
        var accepted = await carrier.AcceptAsync(CancellationToken.None);

        await client.SendAsync(Encoding.UTF8.GetBytes("left"), WebSocketMessageType.Text, false, CancellationToken.None);
        await client.SendAsync(Encoding.UTF8.GetBytes("right"), WebSocketMessageType.Text, true, CancellationToken.None);

        var output = new byte[128];
        var receive = await carrier.ReceiveAsync(accepted.ConnectionId, output, CancellationToken.None);

        Assert.True(receive.Received);
        Assert.True(receive.EndOfMessage);
        Assert.Equal("leftright", Encoding.UTF8.GetString(output, 0, receive.ByteCount));
    }

    [Fact]
    public async Task IndependentWebSocketMessagesRemainSeparateTest()
    {
        var clock = new FakeMonotonicClock();
        using var timers = new RecordingTimers();
        using var carrier = CreateCarrier(DefaultOptions(4096), clock, timers, new AcceptingVerifier(), new AcceptingReplay(), new RecordingAudit());
        using var client = await ConnectAsync(carrier);
        var accepted = await carrier.AcceptAsync(CancellationToken.None);
        var output = new byte[128];

        await client.SendAsync(Encoding.UTF8.GetBytes("first"), WebSocketMessageType.Text, true, CancellationToken.None);
        await client.SendAsync(Encoding.UTF8.GetBytes("second"), WebSocketMessageType.Text, true, CancellationToken.None);

        var first = await carrier.ReceiveAsync(accepted.ConnectionId, output, CancellationToken.None);
        var firstBytes = output.AsSpan(0, first.ByteCount).ToArray();
        var second = await carrier.ReceiveAsync(accepted.ConnectionId, output, CancellationToken.None);
        var secondBytes = output.AsSpan(0, second.ByteCount).ToArray();

        Assert.Equal("first", Encoding.UTF8.GetString(firstBytes));
        Assert.Equal("second", Encoding.UTF8.GetString(secondBytes));
    }

    [Fact]
    public async Task OneWebSocketMessageIsOneEnvelopeTest()
    {
        var clock = new FakeMonotonicClock();
        using var timers = new RecordingTimers();
        var auditInbox = PlatformModule.CreateInbox<AuditRecord>(new QueueBudget(8, 8192));
        var diagnosticInbox = PlatformModule.CreateInbox<DiagnosticRecord>(new QueueBudget(8, 8192));
        var observability = ObservabilityModule.Create(
            auditInbox,
            diagnosticInbox,
            new FakeWallClock("2026-08-27T00:10:00Z"),
            new NullHostTraceSink(),
            new HostIdentity("A", "A-1.1.0", "websocket-test"));
        var carrier = CreateCarrier(DefaultOptions(65536), clock, timers, new AcceptingVerifier(), new AcceptingReplay(), observability.Audit);
        await using var carrierLifetime = carrier;
        using var transport = TransportService.Create(
            carrier,
            new PassThroughFaultPolicy(),
            clock,
            timers,
            observability,
            new TransportEndpointOptions("ws://127.0.0.1:0/", false, 65536, 2, "A", "A-1.1.0"));
        using var client = await ConnectAsync(carrier);
        var acceptTask = Task.Run(transport.TryAcceptOne);
        Assert.True(await acceptTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var id = new TransportConnectionId(1);
        var fixture = ContractMirrorFixtures.Load("fixtures/valid/replication-handshake.json").ToArray();
        var combined = fixture.Concat(fixture).ToArray();
        await client.SendAsync(combined, WebSocketMessageType.Text, true, CancellationToken.None);

        Assert.True(transport.PumpReceiveOnce(id));
        Assert.Equal(TransportConnectionState.Closed, transport.StateOf(id));
    }

    [Fact]
    public async Task SplitEnvelopeAcrossIndependentMessagesIsRejectedTest()
    {
        var clock = new FakeMonotonicClock();
        using var timers = new RecordingTimers();
        var auditInbox = PlatformModule.CreateInbox<AuditRecord>(new QueueBudget(8, 8192));
        var diagnosticInbox = PlatformModule.CreateInbox<DiagnosticRecord>(new QueueBudget(8, 8192));
        var observability = ObservabilityModule.Create(
            auditInbox,
            diagnosticInbox,
            new FakeWallClock("2026-08-27T00:10:00Z"),
            new NullHostTraceSink(),
            new HostIdentity("A", "A-1.1.0", "websocket-test"));
        var carrier = CreateCarrier(DefaultOptions(65536), clock, timers, new AcceptingVerifier(), new AcceptingReplay(), observability.Audit);
        await using var carrierLifetime = carrier;
        using var transport = TransportService.Create(
            carrier,
            new PassThroughFaultPolicy(),
            clock,
            timers,
            observability,
            new TransportEndpointOptions("ws://127.0.0.1:0/", false, 65536, 2, "A", "A-1.1.0"));
        using var client = await ConnectAsync(carrier);
        var acceptTask = Task.Run(transport.TryAcceptOne);
        Assert.True(await acceptTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var id = new TransportConnectionId(1);
        var fixture = ContractMirrorFixtures.Load("fixtures/valid/replication-handshake.json").ToArray();
        var midpoint = fixture.Length / 2;
        await client.SendAsync(fixture.AsMemory(0, midpoint), WebSocketMessageType.Text, true, CancellationToken.None);

        Assert.True(transport.PumpReceiveOnce(id));
        Assert.Equal(TransportConnectionState.Closed, transport.StateOf(id));
    }

    [Fact]
    public async Task BinaryMessageIsPolicyRejectedTest()
    {
        var clock = new FakeMonotonicClock();
        using var timers = new RecordingTimers();
        using var carrier = CreateCarrier(DefaultOptions(4096), clock, timers, new AcceptingVerifier(), new AcceptingReplay(), new RecordingAudit());
        using var client = await ConnectAsync(carrier);
        var accepted = await carrier.AcceptAsync(CancellationToken.None);
        await client.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, CancellationToken.None);

        var receive = await carrier.ReceiveAsync(accepted.ConnectionId, new byte[128], CancellationToken.None);
        Assert.False(receive.Received);
        Assert.True(receive.Closed);
    }

    [Fact]
    public async Task CloseFrameDetectedTest()
    {
        var clock = new FakeMonotonicClock();
        using var timers = new RecordingTimers();
        using var carrier = CreateCarrier(DefaultOptions(4096), clock, timers, new AcceptingVerifier(), new AcceptingReplay(), new RecordingAudit());
        using var client = await ConnectAsync(carrier);
        var accepted = await carrier.AcceptAsync(CancellationToken.None);
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);

        var receive = await carrier.ReceiveAsync(accepted.ConnectionId, new byte[128], CancellationToken.None);
        Assert.False(receive.Received);
        Assert.True(receive.Closed);
    }

    [Fact]
    public async Task ReceiveThrowsDetectedTest()
    {
        var clock = new FakeMonotonicClock();
        using var timers = new RecordingTimers();
        using var carrier = CreateCarrier(DefaultOptions(4096), clock, timers, new AcceptingVerifier(), new AcceptingReplay(), new RecordingAudit());
        using var client = await ConnectAsync(carrier);
        var accepted = await carrier.AcceptAsync(CancellationToken.None);
        var receiveTask = carrier.ReceiveAsync(accepted.ConnectionId, new byte[128], CancellationToken.None).AsTask();

        client.Dispose();
        var receive = await receiveTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(receive.Received);
        Assert.True(receive.Closed);
    }

    [Fact]
    public async Task IdleDeadlineDetectedTest()
    {
        var clock = new FakeMonotonicClock();
        using var timers = new RecordingTimers();
        using var carrier = CreateCarrier(DefaultOptions(4096), clock, timers, new AcceptingVerifier(), new AcceptingReplay(), new RecordingAudit());
        using var client = await ConnectAsync(carrier);
        var accepted = await carrier.AcceptAsync(CancellationToken.None);

        Assert.Contains(timers.Commands, command => command is ConnectionCommand.Close close && close.Id == accepted.ConnectionId);
        clock.Advance(TimeSpan.FromSeconds(15).Ticks);
        timers.FireAll();

        var output = new byte[32];
        var result = await client.ReceiveAsync(output, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, result.CloseStatus);
    }

    [Fact]
    public async Task TokenNeverReachesEnvelopeTest()
    {
        var clock = new FakeMonotonicClock();
        using var timers = new RecordingTimers();
        var verifier = new CapturingVerifier();
        using var carrier = CreateCarrier(DefaultOptions(4096), clock, timers, verifier, new AcceptingReplay(), new RecordingAudit());
        using var client = await ConnectAsync(carrier);

        _ = await carrier.AcceptAsync(CancellationToken.None);
        Assert.NotNull(verifier.Credential);
        Assert.Throws<ObjectDisposedException>(() => verifier.Credential!.Span.ToArray());

        var sourceRoot = Path.Combine(LocateMvpHostRoot(), "src", "Lumio.Server.MvpHost.Transport.WebSocket");
        var writerCalls = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadLines(file))
            .Where(line => line.Contains("MvpEnvelope" + "Writer", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(writerCalls);
    }

    [Fact]
    public async Task BadCredentialClosesWith1008Test()
    {
        var clock = new FakeMonotonicClock();
        using var timers = new RecordingTimers();
        var audit = new RecordingAudit();
        using var carrier = CreateCarrier(DefaultOptions(4096), clock, timers, new RejectingVerifier(), new AcceptingReplay(), audit);
        using var client = await ConnectAsync(carrier);

        var output = new byte[64];
        var result = await client.ReceiveAsync(output, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal((WebSocketCloseStatus)WebSocketCarrierConstants.CloseStatusPolicyViolation, result.CloseStatus);
        Assert.Empty(audit.SessionMessages);
        Assert.Single(audit.Rejections);
    }

    [Fact]
    public async Task BadCredentialWritesReleaseAuditWithGeneratedIdentityTest()
    {
        var clock = new FakeMonotonicClock();
        using var timers = new RecordingTimers();
        var auditInbox = PlatformModule.CreateInbox<AuditRecord>(new QueueBudget(8, 8192));
        var diagnosticInbox = PlatformModule.CreateInbox<DiagnosticRecord>(new QueueBudget(8, 8192));
        var trace = new RecordingHostTraceSink();
        var services = ObservabilityModule.Create(
            auditInbox,
            diagnosticInbox,
            new FakeWallClock("2026-08-27T00:10:00Z"),
            trace,
            new HostIdentity("A", "A-1.1.0", "websocket-test"));
        using var carrier = CreateCarrier(
            DefaultOptions(4096),
            clock,
            timers,
            new RejectingVerifier(),
            new AcceptingReplay(),
            services.Audit);
        using var client = await ConnectAsync(carrier);

        var output = new byte[64];
        _ = await client.ReceiveAsync(output, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        var record = Assert.Single(trace.Audits);
        Assert.False(string.IsNullOrWhiteSpace(record.EventId));
        Assert.False(string.IsNullOrWhiteSpace(record.Timestamp));
        Assert.Equal("Release", record.Correlation.Scope);
        Assert.Null(record.Correlation.SessionId);
    }

    [Fact]
    public async Task ServerInitiatedCloseTest()
    {
        var clock = new FakeMonotonicClock();
        using var timers = new RecordingTimers();
        using var carrier = CreateCarrier(DefaultOptions(4096), clock, timers, new AcceptingVerifier(), new AcceptingReplay(), new RecordingAudit());
        using var client = await ConnectAsync(carrier);
        var accepted = await carrier.AcceptAsync(CancellationToken.None);
        var payload = Encoding.UTF8.GetBytes("queued");

        Assert.True(carrier.TrySend(accepted.ConnectionId, payload));
        Assert.True(carrier.Close(accepted.ConnectionId, ConnectionCloseReason.MaintenanceKick));

        var buffer = new byte[64];
        var first = await client.ReceiveAsync(buffer, CancellationToken.None);
        var second = await client.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Text, first.MessageType);
        Assert.Equal(payload, buffer.AsSpan(0, first.Count).ToArray());
        Assert.Equal(WebSocketMessageType.Close, second.MessageType);
    }

    [Fact]
    public async Task EgressQueuePreservesFifoAndDefensiveCopiesTest()
    {
        var clock = new FakeMonotonicClock();
        using var timers = new RecordingTimers();
        using var carrier = CreateCarrier(DefaultOptions(4096), clock, timers, new AcceptingVerifier(), new AcceptingReplay(), new RecordingAudit());
        using var client = await ConnectAsync(carrier);
        var accepted = await carrier.AcceptAsync(CancellationToken.None);
        var first = Encoding.UTF8.GetBytes("first");
        var second = Encoding.UTF8.GetBytes("second");

        Assert.True(carrier.TrySend(accepted.ConnectionId, first));
        first[0] = (byte)'X';
        Assert.True(carrier.TrySend(accepted.ConnectionId, second));

        var buffer = new byte[64];
        var one = await client.ReceiveAsync(buffer, CancellationToken.None);
        var oneBytes = buffer.AsSpan(0, one.Count).ToArray();
        var two = await client.ReceiveAsync(buffer, CancellationToken.None);
        var twoBytes = buffer.AsSpan(0, two.Count).ToArray();
        Assert.Equal("first", Encoding.UTF8.GetString(oneBytes));
        Assert.Equal("second", Encoding.UTF8.GetString(twoBytes));
        Assert.Equal(WebSocketMessageType.Text, one.MessageType);
        Assert.Equal(WebSocketMessageType.Text, two.MessageType);
    }

    [Fact]
    public async Task CarrierRoundTripsValidFixtureTest()
    {
        var clock = new FakeMonotonicClock();
        using var timers = new RecordingTimers();
        var auditInbox = PlatformModule.CreateInbox<AuditRecord>(new QueueBudget(64, 65536));
        var diagnosticInbox = PlatformModule.CreateInbox<DiagnosticRecord>(new QueueBudget(64, 65536));
        var observability = ObservabilityModule.Create(
            auditInbox,
            diagnosticInbox,
            new FakeWallClock("2026-08-27T00:10:00Z"),
            new NullHostTraceSink(),
            new HostIdentity("A", "A-1.1.0", "websocket-test"));
        var carrier = CreateCarrier(
            DefaultOptions(65536),
            clock,
            timers,
            new AcceptingVerifier(),
            new AcceptingReplay(),
            observability.Audit);
        await using var carrierLifetime = carrier;
        using var transport = TransportService.Create(
            carrier,
            new PassThroughFaultPolicy(),
            clock,
            timers,
            observability,
            new TransportEndpointOptions("ws://127.0.0.1:0/", false, 65536, 2, "A", "A-1.1.0"));

        using var client = await ConnectAsync(carrier);
        var acceptTask = Task.Run(transport.TryAcceptOne);
        Assert.True(await acceptTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var id = new TransportConnectionId(1);
        var fixture = ContractMirrorFixtures.Load("fixtures/valid/replication-handshake.json");
        await client.SendAsync(fixture, WebSocketMessageType.Text, true, CancellationToken.None);

        Assert.True(transport.PumpReceiveOnce(id));
        var events = new List<ConnectionEvent>();
        while (transport.TryReceive(out var evt))
        {
            events.Add(evt);
        }

        var handshake = Assert.Single(events.OfType<ConnectionEvent.HandshakeEnvelope>());
        Assert.Equal("Handshake", handshake.Envelope.Header.MessageType);
    }

    [Fact]
    public async Task MaxConnectionsIsBoundedAtUpgradeTest()
    {
        var clock = new FakeMonotonicClock();
        using var timers = new RecordingTimers();
        using var carrier = CreateCarrier(
            new WebSocketCarrierOptions("ws://127.0.0.1:0/", false, true, "LocalSplitProcess", 4096, 1, 15, "A", "A-1.1.0", "pool"),
            clock,
            timers,
            new AcceptingVerifier(),
            new AcceptingReplay(),
            new RecordingAudit());
        using var first = await ConnectAsync(carrier);
        var accepted = await carrier.AcceptAsync(CancellationToken.None);

        using var second = new ClientWebSocket();
        second.Options.AddSubProtocol(WebSocketCarrierConstants.Subprotocol);
        second.Options.AddSubProtocol("dG9rZW4");
        second.Options.AddSubProtocol("bm9uY2Uy");
        await Assert.ThrowsAnyAsync<Exception>(
            () => second.ConnectAsync(new Uri(carrier.BoundUri), CancellationToken.None));
        Assert.Equal(1, carrier.ConnectionCount);
        Assert.True(carrier.Close(accepted.ConnectionId, ConnectionCloseReason.OwnerRequest));
    }

    private static WebSocketByteCarrier CreateCarrier(
        in WebSocketCarrierOptions options,
        IMonotonicClock clock,
        ITimerService timers,
        ICredentialVerifier verifier,
        IAntiReplayWindow replay,
        IAuditWriter audit)
        => WebSocketByteCarrier.Create(in options, verifier, replay, clock, timers, audit);

    private static WebSocketCarrierOptions DefaultOptions(int maxMessageBytes)
        => new("ws://127.0.0.1:0/", false, true, "LocalSplitProcess", maxMessageBytes, 2, 15, "A", "A-1.1.0", "pool");

    private static async Task<ClientWebSocket> ConnectAsync(WebSocketByteCarrier carrier)
    {
        var client = new ClientWebSocket();
        client.Options.AddSubProtocol(WebSocketCarrierConstants.Subprotocol);
        client.Options.AddSubProtocol("dG9rZW4");
        client.Options.AddSubProtocol("bm9uY2U");
        await client.ConnectAsync(new Uri(carrier.BoundUri), CancellationToken.None);
        return client;
    }

    private static string LocateMvpHostRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "eng", "verify-all.sh")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("mvp-host root not found");
    }

    private sealed class AcceptingVerifier : ICredentialVerifier
    {
        public CredentialVerification Verify(OpaqueCredentialInput credential, in VerificationContext context)
            => new(CredentialVerdict.Accepted, new PrincipalId("p"), null);
    }

    private sealed class RejectingVerifier : ICredentialVerifier
    {
        public CredentialVerification Verify(OpaqueCredentialInput credential, in VerificationContext context)
            => new(CredentialVerdict.Rejected, default, "rejected");
    }

    private sealed class CapturingVerifier : ICredentialVerifier
    {
        public OpaqueCredentialInput? Credential { get; private set; }

        public CredentialVerification Verify(OpaqueCredentialInput credential, in VerificationContext context)
        {
            this.Credential = credential;
            return new CredentialVerification(CredentialVerdict.Accepted, new PrincipalId("p"), null);
        }
    }

    private sealed class AcceptingReplay : IAntiReplayWindow
    {
        public AntiReplayVerdict Check(PrincipalId principal, string nonce, MonotonicInstant receivedAt)
            => AntiReplayVerdict.Ok;
    }

    private sealed class RecordingAudit : IAuditWriter
    {
        public List<string?> Rejections { get; } = new();
        public List<string> SessionMessages { get; } = new();

        public EnqueueResult WriteReleaseScopedReject(string releasePoolId, string productId, string gameReleaseId, string traceId, string producerId, ulong eventSeq, string? reasonCode)
        {
            this.Rejections.Add(reasonCode);
            return new EnqueueResult(EnqueueStatus.Accepted, null);
        }

        public EnqueueResult WriteSessionScoped(ServerSessionId sessionId, string productId, string gameReleaseId, string traceId, string producerId, ulong eventSeq, string message)
        {
            this.SessionMessages.Add(message);
            return new EnqueueResult(EnqueueStatus.Accepted, null);
        }
    }

    private sealed class RecordingTimers : ITimerService
    {
        private readonly List<(MonotonicInstant DueAt, IBoundedInbox<ConnectionCommand> Target, ConnectionCommand Command)> scheduled = new();

        public IReadOnlyList<ConnectionCommand> Commands => this.scheduled.Select(item => item.Command).ToList();

        public TimerId Schedule<T>(MonotonicInstant dueAt, IBoundedInbox<T> target, in T command)
        {
            if (typeof(T) == typeof(ConnectionCommand))
            {
                this.scheduled.Add((dueAt, (IBoundedInbox<ConnectionCommand>)target, (ConnectionCommand)(object)command!));
            }

            return new TimerId((ulong)this.scheduled.Count);
        }

        public bool Cancel(TimerId id) => true;

        public void FireAll()
        {
            foreach (var item in this.scheduled.ToArray())
            {
                item.Target.TryEnqueue(item.Command);
            }
        }

        public void Dispose() { }
    }
}
