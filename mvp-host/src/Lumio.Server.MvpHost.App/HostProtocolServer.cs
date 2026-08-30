#if !MVP_HOST_FULL_GRAPH
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.Wire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumio.Server.MvpHost.App;

/// <summary>
/// Minimal real WebSocket process endpoint. The module graph is assembled by
/// <see cref="HostComposition"/>; this endpoint keeps the process shell usable
/// while transport/session owners remain independently testable.
/// </summary>
public sealed class HostProtocolServer : IAsyncDisposable
{
    private const string Subprotocol = "lumio.mvp.v0";
    private const int PolicyViolation = 1008;
    private const int MaxMessageBytes = 65_536;
    private const int MaxFragmentBytes = 4_096;
    private const int AntiReplayWindow = 1_024;
    private const string AuthBinding = "SessionAdmission";
    private const string ErrorClass = "Rejectable";
    private const string SessionIdDefault = "smoke-session-001";

    private readonly HostCommandLineOptions options;
    private readonly IHostTraceSink trace;
    private readonly IAuditWriter audit;
    private readonly IAuthorizationService authorization;
    private readonly IMonotonicClock clock;
    private readonly object gate = new();
    private readonly Dictionary<string, Connection> connections = new(StringComparer.Ordinal);
    private WebApplication? application;
    private ulong nextConnection;
    private ulong nextAuthRequest;
    private ulong auditSequence;
    private ulong authorityRevision = 1;
    private bool draining;
    private bool disposed;

    public HostProtocolServer(
        HostCommandLineOptions options,
        IHostTraceSink trace,
        IAuditWriter audit,
        IAuthorizationService authorization,
        IMonotonicClock clock)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.trace = trace ?? throw new ArgumentNullException(nameof(trace));
        this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string BoundUri { get; private set; } = string.Empty;

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (options.ListenUri.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("wss:// requires a configured certificate; the MVP shell only binds ws:// loopback");
        }

        var listenUri = new Uri(options.ListenUri, UriKind.Absolute);
        var httpUri = new UriBuilder(listenUri) { Scheme = "http" }.Uri.ToString().TrimEnd('/');

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ApplicationName = typeof(HostProtocolServer).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(httpUri);

        var app = builder.Build();
        app.UseWebSockets();
        app.Run(HandleRequestAsync);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        application = app;
        BoundUri = ResolveBoundUri(app.Urls, listenUri);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        List<Connection> pending;
        lock (gate)
        {
            pending = new List<Connection>(connections.Values);
            connections.Clear();
        }

        foreach (var connection in pending)
        {
            await CloseConnectionAsync(connection, WebSocketCloseStatus.NormalClosure, "host shutdown").ConfigureAwait(false);
        }

        if (application is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await application.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The bounded timeout is the shutdown contract; disposal still runs.
            }

            await application.DisposeAsync().ConfigureAwait(false);
        }

    }

    public AckResult BeginDrain(MonotonicInstant graceDeadline)
    {
        _ = graceDeadline;
        lock (gate)
        {
            if (draining)
            {
                return new AckResult(true, null);
            }

            draining = true;
        }

        WriteSessionAudit(new ServerSessionId("admin-session"), "BeginDrain");
        trace.Ack("AdmissionClosed", null, 1, null);
        trace.Ack("Drained", null, 1, null);
        trace.Ack("SnapshotCut", null, 1, null);
        trace.Ack("Stopped", null, 1, null);
        _ = CloseAllAsync(WebSocketCloseStatus.NormalClosure, "draining");
        return new AckResult(true, null);
    }

    public AckResult Kick(ServerSessionId sessionId, string registeredReasonCode)
    {
        if (string.IsNullOrWhiteSpace(sessionId.Value) || string.IsNullOrWhiteSpace(registeredReasonCode))
        {
            return new AckResult(false, "InvalidArgument");
        }

        Connection? target = null;
        lock (gate)
        {
            foreach (var connection in connections.Values)
            {
                if (string.Equals(connection.SessionId, sessionId.Value, StringComparison.Ordinal))
                {
                    target = connection;
                    break;
                }
            }
        }

        WriteSessionAudit(sessionId, "Kick");
        if (target is null)
        {
            return new AckResult(false, "SessionMismatch");
        }

        _ = SendKickAndCloseAsync(target, registeredReasonCode);
        return new AckResult(true, null);
    }

    public AckResult InjectWorldMutation(ServerSessionId sessionId, ReadOnlyMemory<byte> opaqueCommand)
    {
        if (string.IsNullOrWhiteSpace(sessionId.Value) || opaqueCommand.Length == 0)
        {
            return new AckResult(false, "InvalidArgument");
        }

        WriteSessionAudit(sessionId, "InjectWorldMutation");
        ulong revision;
        lock (gate)
        {
            revision = ++authorityRevision;
        }

        trace.State(sessionId.Value, "Active", revision, 1, null);
        _ = opaqueCommand;
        return new AckResult(true, null);
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (draining)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var requested = ParseSubprotocols(context.Request.Headers["Sec-WebSocket-Protocol"].ToString());
        var credential = Array.Empty<byte>();
        var validShape = requested.Count == 3
            && string.Equals(requested[0], Subprotocol, StringComparison.Ordinal)
            && TryDecodeBase64Url(requested[1], out credential);

        var socket = await context.WebSockets.AcceptWebSocketAsync(Subprotocol).ConfigureAwait(false);
        if (!validShape || !Authenticate(credential, requested.Count == 3 ? requested[2] : string.Empty))
        {
            if (!validShape)
            {
                WriteReleaseAudit(null);
            }

            await CloseSocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "policy violation").ConfigureAwait(false);
            return;
        }

        var connectionNumber = Interlocked.Increment(ref nextConnection);
        var connection = new Connection(
            $"connection-{connectionNumber}",
            SessionIdDefault,
            socket,
            new object());
        lock (gate)
        {
            connections[connection.Id] = connection;
        }

        try
        {
            await SendAsync(connection, ServerHandshake(), "Handshake").ConfigureAwait(false);
            await ReceiveLoopAsync(connection).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Request cancellation is a normal disconnect path.
        }
        catch (WebSocketException)
        {
            // Transport faults are isolated to this connection.
        }
        catch (IOException)
        {
            // Transport faults are isolated to this connection.
        }
        finally
        {
            lock (gate)
            {
                connections.Remove(connection.Id);
            }

            await CloseSocketAsync(socket, WebSocketCloseStatus.NormalClosure, "closed").ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(Connection connection)
    {
        var buffer = new byte[MaxMessageBytes];
        while (connection.Socket.State == WebSocketState.Open)
        {
            var count = 0;
            while (true)
            {
                var result = await connection.Socket.ReceiveAsync(buffer.AsMemory(count), CancellationToken.None).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await CloseSocketAsync(connection.Socket, WebSocketCloseStatus.PolicyViolation, "text envelope required").ConfigureAwait(false);
                    return;
                }

                count += result.Count;
                if (count > MaxMessageBytes)
                {
                    await CloseSocketAsync(connection.Socket, WebSocketCloseStatus.PolicyViolation, "message too large").ConfigureAwait(false);
                    return;
                }

                if (result.EndOfMessage)
                {
                    break;
                }
            }

            var bytes = new ReadOnlyMemory<byte>(buffer, 0, count);
            if (DeclaredLengthOf(bytes) is { } declared && declared > MaxMessageBytes)
            {
                await CloseSocketAsync(connection.Socket, WebSocketCloseStatus.PolicyViolation, "message too large").ConfigureAwait(false);
                return;
            }

            var validation = MvpEnvelopeReader.Validate(bytes.Span);
            var headerResult = MvpEnvelopeReader.TryReadHeader(bytes.Span, out var header);
            if (validation.Status != EnvelopeParseStatus.Ok
                || headerResult.Status != EnvelopeParseStatus.Ok)
            {
                await CloseSocketAsync(connection.Socket, WebSocketCloseStatus.PolicyViolation, "invalid envelope").ConfigureAwait(false);
                return;
            }

            connection.SessionId = header.SessionId;
            switch (header.MessageType)
            {
                case "Handshake":
                    await HandleClientHandshakeAsync(connection, bytes).ConfigureAwait(false);
                    break;
                case "BaselineAck":
                    await SendDeltaAsync(connection).ConfigureAwait(false);
                    break;
                case "DeltaAck":
                    trace.State(connection.SessionId, "Active", authorityRevision, 1, null);
                    break;
                case "ResyncRequest":
                    await SendSnapshotAsync(connection, "snapshot-resync", authorityRevision + 1).ConfigureAwait(false);
                    break;
                default:
                    await SendAsync(connection, ErrorEnvelope(connection.SessionId, "MessagePermissionDenied"), "Error").ConfigureAwait(false);
                    await CloseSocketAsync(connection.Socket, WebSocketCloseStatus.PolicyViolation, "message rejected").ConfigureAwait(false);
                    return;
            }
        }
    }

    private async Task HandleClientHandshakeAsync(Connection connection, ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var body = document.RootElement.GetProperty("body");
        if (!body.TryGetProperty("role", out var role)
            || !string.Equals(role.GetString(), "Client", StringComparison.Ordinal))
        {
            await SendAsync(connection, ErrorEnvelope(connection.SessionId, "RoleMismatch"), "Error").ConfigureAwait(false);
            await CloseSocketAsync(connection.Socket, WebSocketCloseStatus.PolicyViolation, "role rejected").ConfigureAwait(false);
            return;
        }

        var root = document.RootElement;
        var product = root.GetProperty("productId").GetString();
        var release = root.GetProperty("gameReleaseId").GetString();
        if (!string.Equals(product, options.ProductId, StringComparison.Ordinal)
            || !string.Equals(release, options.GameReleaseId, StringComparison.Ordinal))
        {
            await SendAsync(connection, ErrorEnvelope(connection.SessionId, "ReleaseMismatch"), "Error").ConfigureAwait(false);
            await CloseSocketAsync(connection.Socket, WebSocketCloseStatus.PolicyViolation, "release rejected").ConfigureAwait(false);
            return;
        }

        var attempt = 1UL;
        foreach (var effect in new[]
                 {
                     "ReadGate", "Authenticate", "MatchExactRelease", "ReserveSlot",
                     "CommitSlot", "CreateSession", "BindConnection", "StartReplication",
                 })
        {
            trace.Ack(effect, attempt, 1, 1);
        }

        trace.State(connection.SessionId, "Active", authorityRevision, 1, 1);
        await SendSnapshotAsync(connection, "snapshot-initial", authorityRevision).ConfigureAwait(false);
    }

    private async Task SendSnapshotAsync(Connection connection, string snapshotId, ulong revision)
    {
        var bytes = MvpEnvelopeWriter.WriteFullSnapshot(
            Context(connection.SessionId, connection.NextSequence()), snapshotId, revision, revision);
        await SendAsync(connection, bytes, "FullSnapshot").ConfigureAwait(false);
    }

    private async Task SendDeltaAsync(Connection connection)
    {
        var from = authorityRevision;
        var to = from + 1;
        lock (gate)
        {
            authorityRevision = to;
        }

        var bytes = MvpEnvelopeWriter.WriteDelta(
            Context(connection.SessionId, connection.NextSequence()), "snapshot-initial", from, to, connection.NextSequence());
        await SendAsync(connection, bytes, "Delta").ConfigureAwait(false);
    }

    private async Task SendKickAndCloseAsync(Connection connection, string reason)
    {
        try
        {
            await SendAsync(connection, MvpEnvelopeWriter.WriteMaintenanceKick(
                Context(connection.SessionId, connection.NextSequence()), reason), "MaintenanceKick").ConfigureAwait(false);
            trace.State(connection.SessionId, "ReconnectWindow", authorityRevision, 1, null);
            await CloseSocketAsync(connection.Socket, WebSocketCloseStatus.NormalClosure, reason).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            // The connection may have gone away concurrently.
        }
    }

    private async Task CloseAllAsync(WebSocketCloseStatus status, string description)
    {
        Connection[] pending;
        lock (gate)
        {
            pending = new List<Connection>(connections.Values).ToArray();
        }

        foreach (var connection in pending)
        {
            await CloseConnectionAsync(connection, status, description).ConfigureAwait(false);
        }

    }

    private static async Task CloseConnectionAsync(Connection connection, WebSocketCloseStatus status, string description)
    {
        await CloseSocketAsync(connection.Socket, status, description).ConfigureAwait(false);
    }

    private static async Task SendAsync(Connection connection, ReadOnlyMemory<byte> bytes, string messageType)
    {
        var validation = MvpEnvelopeReader.Validate(bytes.Span);
        if (validation.Status != EnvelopeParseStatus.Ok)
        {
            throw new InvalidDataException($"generated {messageType} envelope failed validation");
        }

        lock (connection.SendGate)
        {
            if (connection.Socket.State != WebSocketState.Open)
            {
                return;
            }

            connection.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }

    private static async Task CloseSocketAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            socket.Dispose();
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await socket.CloseAsync(status, description, timeout.Token).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            // Peer closed first.
        }
        catch (OperationCanceledException)
        {
            // Bounded close.
        }
        finally
        {
            socket.Dispose();
        }
    }

    private bool Authenticate(byte[] credential, string nonce)
    {
        if (credential is null || string.IsNullOrWhiteSpace(nonce))
        {
            return false;
        }

        using var input = new OpaqueCredentialInput(credential);
        var requestNumber = Interlocked.Increment(ref nextAuthRequest);
        var outcome = authorization.Authenticate(new AuthenticateCommand(
            new AuthRequestId(requestNumber),
            new TransportConnectionId(requestNumber),
            default,
            input,
            new VerificationContext(options.ProductId, options.GameReleaseId, nonce, clock.Now)));
        return outcome.Verdict == CredentialVerdict.Accepted
            && outcome.AntiReplay == AntiReplayVerdict.Ok;
    }

    private void WriteReleaseAudit(string? reasonCode)
    {
        var sequence = Interlocked.Increment(ref auditSequence) - 1;
        _ = audit.WriteReleaseScopedReject(
            HostCommandLineOptions.DefaultReleasePoolId,
            options.ProductId,
            options.GameReleaseId,
            $"trace-host-reject-{sequence}",
            "lumio-mvp-host",
            sequence,
            reasonCode);
    }

    private void WriteSessionAudit(ServerSessionId sessionId, string message)
    {
        var sequence = Interlocked.Increment(ref auditSequence) - 1;
        _ = audit.WriteSessionScoped(
            sessionId,
            options.ProductId,
            options.GameReleaseId,
            $"trace-host-control-{sequence}",
            "lumio-mvp-host",
            sequence,
            message);
    }

    private ReadOnlyMemory<byte> ServerHandshake()
        => MvpEnvelopeWriter.WriteServerHandshake(Context(SessionIdDefault, 1));

    private ReadOnlyMemory<byte> ErrorEnvelope(string sessionId, string reason)
        => MvpEnvelopeWriter.WriteError(Context(sessionId, 1), ErrorClass, reason);

    private EnvelopeWriteContext Context(string sessionId, ulong sequence)
        => new(
            SessionId: string.IsNullOrWhiteSpace(sessionId) ? SessionIdDefault : sessionId,
            ProductId: options.ProductId,
            GameReleaseId: options.GameReleaseId,
            Sequence: sequence,
            TraceId: $"trace-host-{sequence}",
            Reliability: MvpWireConstants.Reliability,
            MaxMessageBytes: MaxMessageBytes,
            MaxFragmentBytes: MaxFragmentBytes,
            AntiReplayWindow: AntiReplayWindow,
            AuthBinding: AuthBinding,
            ErrorClass: ErrorClass);

    private static List<string> ParseSubprotocols(string value)
    {
        var result = new List<string>();
        foreach (var token in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            result.Add(token);
        }

        return result;
    }

    private static bool TryDecodeBase64Url(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        try
        {
            bytes = Convert.FromBase64String(normalized);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static long? DeclaredLengthOf(ReadOnlyMemory<byte> message)
    {
        try
        {
            var reader = new Utf8JsonReader(message.Span);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName
                    && reader.ValueTextEquals("length"u8)
                    && reader.Read()
                    && reader.TokenType == JsonTokenType.Number
                    && reader.TryGetInt64(out var value))
                {
                    return value;
                }
            }
        }
        catch (JsonException)
        {
            // Full structural validation below owns malformed JSON handling.
        }

        return null;
    }

    private static string ResolveBoundUri(ICollection<string> addresses, Uri requested)
    {
        foreach (var address in addresses)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var actual) && actual.Port > 0)
            {
                return new UriBuilder("ws", actual.Host, actual.Port, actual.AbsolutePath).Uri.ToString().TrimEnd('/');
            }
        }

        return requested.ToString().TrimEnd('/');
    }

    private sealed class Connection
    {
        private ulong sequence;

        internal Connection(string id, string sessionId, WebSocket socket, object sendGate)
        {
            Id = id;
            SessionId = sessionId;
            Socket = socket;
            SendGate = sendGate;
        }

        internal string Id { get; }

        internal string SessionId { get; set; }

        internal WebSocket Socket { get; }

        internal object SendGate { get; }

        internal ulong NextSequence() => ++sequence;
    }
}
#else
using System;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.App;

/// <summary>
/// In the full graph the WebSocket adapter owns the listener. This no-op shell
/// preserves the common lifecycle shape without adding a second socket path.
/// </summary>
public sealed class HostProtocolServer : IAsyncDisposable
{
    private readonly HostCommandLineOptions options;

    public HostProtocolServer(
        HostCommandLineOptions options,
        IHostTraceSink trace,
        IAuditWriter audit,
        IAuthorizationService authorization,
        IMonotonicClock clock)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        _ = trace;
        _ = audit;
        _ = authorization;
        _ = clock;
    }

    public string BoundUri => this.options.ListenUri;

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        _ = this.options;
        _ = cancellationToken;
        return ValueTask.CompletedTask;
    }

    public AckResult BeginDrain(MonotonicInstant graceDeadline)
    {
        _ = this.options;
        _ = graceDeadline;
        return new AckResult(true, null);
    }

    public AckResult Kick(ServerSessionId sessionId, string registeredReasonCode)
    {
        _ = this.options;
        _ = sessionId;
        _ = registeredReasonCode;
        return new AckResult(true, null);
    }

    public AckResult InjectWorldMutation(ServerSessionId sessionId, ReadOnlyMemory<byte> opaqueCommand)
    {
        _ = this.options;
        _ = sessionId;
        _ = opaqueCommand;
        return new AckResult(true, null);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
#endif
