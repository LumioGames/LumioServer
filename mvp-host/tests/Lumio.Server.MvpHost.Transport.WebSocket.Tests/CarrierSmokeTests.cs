using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.Transport.WebSocket;
using Xunit;

namespace Lumio.Server.MvpHost.Transport.WebSocket.Tests;

public sealed class CarrierSmokeTests
{
    [Fact]
    public async Task SubprotocolNegotiationTest()
    {
        using var clock = new DummyClock();
        using var timers = new DummyTimers();
        var verifier = new DummyVerifier();
        var audit = new DummyAudit();
        var options = new WebSocketCarrierOptions(
            "ws://127.0.0.1:0/",
            RequireTls: false,
            AllowInsecureLoopback: true,
            HostProfile: "LocalSplitProcess",
            MaxMessageBytes: 4096,
            MaxConnections: 4,
            IdleTimeoutSeconds: 15,
            ProductId: "A",
            GameReleaseId: "A-1.1.0",
            ReleasePoolId: "pool");

        await using var carrier = WebSocketByteCarrier.Create(in options, verifier, new DummyReplay(), clock, timers, audit);
        Assert.StartsWith("ws://127.0.0.1:", carrier.BoundUri, StringComparison.Ordinal);

        using var client = new ClientWebSocket();
        client.Options.AddSubProtocol(WebSocketCarrierConstants.Subprotocol);
        client.Options.AddSubProtocol("dG9rZW4");
        client.Options.AddSubProtocol("bm9uY2U");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(new Uri(carrier.BoundUri), cancellation.Token);

        var accepted = await carrier.AcceptAsync(cancellation.Token);
        Assert.True(accepted.Accepted);
        Assert.Equal(WebSocketCarrierConstants.Subprotocol, client.SubProtocol);

        var payload = Encoding.UTF8.GetBytes("hello");
        await client.SendAsync(payload, WebSocketMessageType.Text, true, cancellation.Token);
        var buffer = new byte[128];
        var received = await carrier.ReceiveAsync(accepted.ConnectionId, buffer, cancellation.Token);
        Assert.True(received.Received);
        Assert.Equal(payload.Length, received.ByteCount);
        Assert.Equal(payload, buffer.AsSpan(0, received.ByteCount).ToArray());

        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cancellation.Token);
        _ = await carrier.ReceiveAsync(accepted.ConnectionId, buffer, cancellation.Token);
    }

    [Fact]
    public async Task RejectedCredentialProducesPolicyCloseAndAudit()
    {
        using var clock = new DummyClock();
        using var timers = new DummyTimers();
        var audit = new DummyAudit();
        var options = Options();
        await using var carrier = WebSocketByteCarrier.Create(
            in options,
            new RejectingVerifier(),
            new DummyReplay(),
            clock,
            timers,
            audit);

        using var client = new ClientWebSocket();
        client.Options.AddSubProtocol(WebSocketCarrierConstants.Subprotocol);
        client.Options.AddSubProtocol("dG9rZW4");
        client.Options.AddSubProtocol("bm9uY2U");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(new Uri(carrier.BoundUri), cancellation.Token);

        var buffer = new byte[32];
        var result = await client.ReceiveAsync(buffer, cancellation.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal((WebSocketCloseStatus)WebSocketCarrierConstants.CloseStatusPolicyViolation, result.CloseStatus);
        Assert.Empty(audit.Calls);
        Assert.Single(audit.Rejections);
    }

    [Fact]
    public async Task PolicyCloseFlushesQueuedEnvelopeBeforeCloseFrame()
    {
        using var clock = new DummyClock();
        using var timers = new DummyTimers();
        var options = Options();
        await using var carrier = WebSocketByteCarrier.Create(
            in options,
            new DummyVerifier(),
            new DummyReplay(),
            clock,
            timers,
            new DummyAudit());
        using var client = new ClientWebSocket();
        client.Options.AddSubProtocol(WebSocketCarrierConstants.Subprotocol);
        client.Options.AddSubProtocol("dG9rZW4");
        client.Options.AddSubProtocol("Zmx1c2g");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(new Uri(carrier.BoundUri), cancellation.Token);
        var accepted = await carrier.AcceptAsync(cancellation.Token);
        var payload = Encoding.UTF8.GetBytes("terminal-envelope");

        Assert.True(carrier.TrySend(accepted.ConnectionId, payload));
        Assert.True(carrier.Close(accepted.ConnectionId, ConnectionCloseReason.PolicyReject));
        Assert.Equal(0, carrier.ConnectionCount);

        var buffer = new byte[64];
        var envelope = await client.ReceiveAsync(buffer, cancellation.Token);
        Assert.Equal(WebSocketMessageType.Text, envelope.MessageType);
        Assert.Equal(payload, buffer.AsSpan(0, envelope.Count).ToArray());
        var closed = await client.ReceiveAsync(buffer, cancellation.Token);
        Assert.Equal(WebSocketMessageType.Close, closed.MessageType);
        Assert.Equal((WebSocketCloseStatus)WebSocketCarrierConstants.CloseStatusPolicyViolation, closed.CloseStatus);
    }

    [Fact]
    public async Task RealSecretAndBase64UrlNonceReachTheAcceptedCarrierQueue()
    {
        using var clock = new DummyClock();
        using var timers = new DummyTimers();
        var options = Options();
        await using var carrier = WebSocketByteCarrier.Create(
            in options,
            new ExactVerifier(Encoding.UTF8.GetBytes("test-secret")),
            new DummyReplay(),
            clock,
            timers,
            new DummyAudit());

        using var client = new ClientWebSocket();
        client.Options.AddSubProtocol(WebSocketCarrierConstants.Subprotocol);
        client.Options.AddSubProtocol("dGVzdC1zZWNyZXQ");
        client.Options.AddSubProtocol("dual-client-one");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(new Uri(carrier.BoundUri), cancellation.Token);

        var accepted = await carrier.AcceptAsync(cancellation.Token);
        Assert.True(accepted.Accepted);
        Assert.False(carrier.TryTakeAuthenticationMetadata(
            accepted.ConnectionId,
            new ConnectionEpoch(1),
            out _,
            out _,
            out _));
        Assert.True(carrier.TryTakeAuthenticationMetadata(
            accepted.ConnectionId,
            new ConnectionEpoch(0),
            out var principal,
            out var productId,
            out var gameReleaseId));
        Assert.Equal(new PrincipalId("p"), principal);
        Assert.Equal("A", productId);
        Assert.Equal("A-1.1.0", gameReleaseId);
        Assert.False(carrier.TryTakeAuthenticationMetadata(
            accepted.ConnectionId,
            new ConnectionEpoch(0),
            out _,
            out _,
            out _));
    }

    [Fact]
    public async Task AuthenticationMetadataIsUnavailableAfterConnectionClose()
    {
        using var clock = new DummyClock();
        using var timers = new DummyTimers();
        var options = Options();
        await using var carrier = WebSocketByteCarrier.Create(
            in options,
            new DummyVerifier(),
            new DummyReplay(),
            clock,
            timers,
            new DummyAudit());

        using var client = new ClientWebSocket();
        client.Options.AddSubProtocol(WebSocketCarrierConstants.Subprotocol);
        client.Options.AddSubProtocol("dG9rZW4");
        client.Options.AddSubProtocol("Y2xvc2U");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(new Uri(carrier.BoundUri), cancellation.Token);
        var accepted = await carrier.AcceptAsync(cancellation.Token);
        Assert.True(accepted.Accepted);

        Assert.True(carrier.Close(accepted.ConnectionId, ConnectionCloseReason.OwnerRequest));
        Assert.False(carrier.TryTakeAuthenticationMetadata(
            accepted.ConnectionId,
            new ConnectionEpoch(0),
            out _,
            out _,
            out _));
    }

    [Fact]
    public async Task ServerCloseFlushesQueuedMessageBeforeCloseFrame()
    {
        using var clock = new DummyClock();
        using var timers = new DummyTimers();
        var options = Options();
        await using var carrier = WebSocketByteCarrier.Create(
            in options,
            new DummyVerifier(),
            new DummyReplay(),
            clock,
            timers,
            new DummyAudit());

        using var client = new ClientWebSocket();
        client.Options.AddSubProtocol(WebSocketCarrierConstants.Subprotocol);
        client.Options.AddSubProtocol("dG9rZW4");
        client.Options.AddSubProtocol("bm9uY2U");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(new Uri(carrier.BoundUri), cancellation.Token);
        var accepted = await carrier.AcceptAsync(cancellation.Token);

        var payload = Encoding.UTF8.GetBytes("queued");
        Assert.True(carrier.TrySend(accepted.ConnectionId, payload));
        Assert.True(carrier.Close(accepted.ConnectionId, ConnectionCloseReason.MaintenanceKick));

        var receiveBuffer = new byte[128];
        var first = await client.ReceiveAsync(receiveBuffer, cancellation.Token);
        Assert.Equal(WebSocketMessageType.Text, first.MessageType);
        Assert.Equal(payload, receiveBuffer.AsSpan(0, first.Count).ToArray());
        var second = await client.ReceiveAsync(receiveBuffer, cancellation.Token);
        Assert.Equal(WebSocketMessageType.Close, second.MessageType);
    }

    private static WebSocketCarrierOptions Options()
        => new(
            "ws://127.0.0.1:0/",
            RequireTls: false,
            AllowInsecureLoopback: true,
            HostProfile: "LocalSplitProcess",
            MaxMessageBytes: 4096,
            MaxConnections: 4,
            IdleTimeoutSeconds: 15,
            ProductId: "A",
            GameReleaseId: "A-1.1.0",
            ReleasePoolId: "pool");

    private sealed class DummyClock : IMonotonicClock, IDisposable
    {
        public MonotonicInstant Now => new(0);
        public void Dispose() { }
    }

    private sealed class DummyTimers : ITimerService
    {
        public TimerId Schedule<T>(MonotonicInstant dueAt, IBoundedInbox<T> target, in T command)
            => new(1);
        public bool Cancel(TimerId id) => true;
        public void Dispose() { }
    }

    private sealed class DummyVerifier : ICredentialVerifier
    {
        public CredentialVerification Verify(OpaqueCredentialInput credential, in VerificationContext context)
            => new(CredentialVerdict.Accepted, new PrincipalId("p"), null);
    }

    private sealed class ExactVerifier : ICredentialVerifier
    {
        private readonly byte[] expected;

        internal ExactVerifier(byte[] expected) => this.expected = expected;

        public CredentialVerification Verify(OpaqueCredentialInput credential, in VerificationContext context)
            => CryptographicOperations.FixedTimeEquals(credential.Span, expected)
                ? new CredentialVerification(CredentialVerdict.Accepted, new PrincipalId("p"), null)
                : new CredentialVerification(CredentialVerdict.Rejected, default, "mismatch");
    }

    private sealed class RejectingVerifier : ICredentialVerifier
    {
        public CredentialVerification Verify(OpaqueCredentialInput credential, in VerificationContext context)
            => new(CredentialVerdict.Rejected, default, "rejected");
    }

    private sealed class DummyReplay : IAntiReplayWindow
    {
        public AntiReplayVerdict Check(PrincipalId principal, string nonce, MonotonicInstant receivedAt)
            => AntiReplayVerdict.Ok;
    }

    private sealed class DummyAudit : IAuditWriter
    {
        public List<string?> Rejections { get; } = new();
        public List<string> Calls { get; } = new();

        public EnqueueResult WriteReleaseScopedReject(string releasePoolId, string productId, string gameReleaseId, string traceId, string producerId, ulong eventSeq, string? reasonCode)
        {
            this.Rejections.Add(reasonCode);
            return new EnqueueResult(EnqueueStatus.Accepted, null);
        }
        public EnqueueResult WriteSessionScoped(ServerSessionId sessionId, string productId, string gameReleaseId, string traceId, string producerId, ulong eventSeq, string message)
        {
            this.Calls.Add(message);
            return new EnqueueResult(EnqueueStatus.Accepted, null);
        }
    }
}
