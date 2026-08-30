using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebSocketConnection = System.Net.WebSockets.WebSocket;

namespace Lumio.Server.MvpHost.Transport.WebSocket;

/// <summary>
/// ASP.NET Core WebSocket implementation of <see cref="IByteCarrier"/>.
///
/// The adapter owns only WebSocket framing and bounded carrier queues. Envelope
/// parsing, authentication after the channel handshake, permission checks, and
/// transport state transitions remain in their owning modules.
/// </summary>
public sealed class WebSocketByteCarrier : IByteCarrier, IAsyncDisposable, IDisposable
{
    private const int EgressCapacity = 512;
    private const long EgressByteCapacity = 1024 * 1024;
    private const int CloseFlushMilliseconds = 1000;
    private const string AuditProducer = "transport-websocket";

    private readonly object gate = new();
    private readonly Dictionary<ulong, ConnectionState> connections = new();
    private readonly Channel<AcceptedConnection> accepted;
    private readonly ICredentialVerifier verifier;
    private readonly IAntiReplayWindow antiReplay;
    private readonly IMonotonicClock clock;
    private readonly ITimerService timers;
    private readonly IAuditWriter audit;
    private readonly WebSocketCarrierOptions options;
    private readonly TimerCommandInbox timerCommands;
    private readonly AcceptedQueue acceptedQueue;

    private WebApplication? application;
    private BindEndpointResult bindResult;
    private bool bindAttempted;
    private bool disposed;
    private int pendingReservations;
    private ulong nextConnectionId;
    private ulong auditSequence;

    // Interface-shaped view keeps queue registration discoverable without
    // forcing the timer hot path through an interface dispatch.
    private IBoundedInbox<ConnectionCommand> TimerInboxContract => this.timerCommands;
    private IBoundedOutbox<AcceptedConnection> AcceptedQueueContract => this.acceptedQueue;

    private WebSocketByteCarrier(
        in WebSocketCarrierOptions options,
        ICredentialVerifier verifier,
        IAntiReplayWindow antiReplay,
        IMonotonicClock clock,
        ITimerService timers,
        IAuditWriter audit)
    {
        this.options = options;
        this.verifier = verifier;
        this.antiReplay = antiReplay;
        this.clock = clock;
        this.timers = timers;
        this.audit = audit;
        this.accepted = Channel.CreateBounded<AcceptedConnection>(new BoundedChannelOptions(
            Math.Max(1, options.MaxConnections))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        this.timerCommands = new TimerCommandInbox(this, Math.Max(1, options.MaxConnections * 2));
        this.acceptedQueue = new AcceptedQueue(this.accepted);
    }

    /// <summary>Constructs and attempts the configured listener.</summary>
    public static WebSocketByteCarrier Create(
        in WebSocketCarrierOptions options,
        ICredentialVerifier verifier,
        IAntiReplayWindow antiReplay,
        IMonotonicClock clock,
        ITimerService timers,
        IAuditWriter audit)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(antiReplay);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(audit);

        var carrier = new WebSocketByteCarrier(in options, verifier, antiReplay, clock, timers, audit);
        carrier.BindEndpoint();
        return carrier;
    }

    /// <summary>The actual bound WebSocket URI, or an empty string when binding failed.</summary>
    public string BoundUri
    {
        get
        {
            lock (this.gate)
            {
                return this.bindResult.Bound ? this.bindResult.BoundUri ?? string.Empty : string.Empty;
            }
        }
    }

    /// <summary>Number of accepted or pending authenticated connections.</summary>
    public int ConnectionCount
    {
        get
        {
            lock (this.gate)
            {
                return this.connections.Count + this.pendingReservations;
            }
        }
    }

    /// <summary>Returns the carrier-local epoch for diagnostics and adapter tests.</summary>
    public ConnectionEpoch EpochOf(TransportConnectionId connection)
    {
        lock (this.gate)
        {
            return this.connections.TryGetValue(connection.Value, out var state)
                ? state.Epoch
                : default;
        }
    }

    /// <summary>
    /// Returns the cached bind result. The overload accepting options is useful for
    /// composition roots that keep the common transport-style bind call shape.
    /// </summary>
    public BindEndpointResult BindEndpoint()
    {
        lock (this.gate)
        {
            if (this.bindAttempted)
            {
                return this.bindResult;
            }

            this.bindAttempted = true;
            var result = this.StartListener();
            this.bindResult = result;
            return result;
        }
    }

    public BindEndpointResult BindEndpoint(in WebSocketCarrierOptions requestedOptions)
        => requestedOptions.Equals(this.options)
            ? this.BindEndpoint()
            : new BindEndpointResult(false, requestedOptions.ListenUri ?? string.Empty, "InvalidArgument");

    /// <inheritdoc />
    public async ValueTask<CarrierAccept> AcceptAsync(CancellationToken ct)
    {
        this.ProcessPendingTimerCommands();

        lock (this.gate)
        {
            if (!this.bindResult.Bound)
            {
                return new CarrierAccept(false, default, ImmutableArray<string>.Empty);
            }
        }

        try
        {
            while (await this.accepted.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (this.accepted.Reader.TryRead(out var item))
                {
                    if (!item.State.IsClosed)
                    {
                        return new CarrierAccept(
                            Accepted: true,
                            ConnectionId: item.State.Id,
                            RequestedSubprotocols: item.RequestedSubprotocols)
                        {
                            AuthenticationEvidence = item.AuthenticationEvidence,
                        };
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }

        return new CarrierAccept(false, default, ImmutableArray<string>.Empty);
    }

    /// <inheritdoc />
    public async ValueTask<CarrierReceive> ReceiveAsync(
        TransportConnectionId c,
        Memory<byte> buffer,
        CancellationToken ct)
    {
        this.ProcessPendingTimerCommands();

        if (!this.TryGetConnection(c, out var state))
        {
            return ClosedReceive();
        }

        await state.ReceiveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (state.IsClosed)
            {
                return ClosedReceive();
            }

            if (buffer.Length == 0)
            {
                await this.RejectReceiveAsync(state, ConnectionCloseReason.Fault, ct).ConfigureAwait(false);
                return ClosedReceive();
            }

            var total = 0;
            while (true)
            {
                if (total >= buffer.Length)
                {
                    // No unbounded accumulator: a message that cannot fit in the
                    // caller's bounded receive buffer is rejected before growth.
                    await this.RejectReceiveAsync(state, ConnectionCloseReason.Fault, ct).ConfigureAwait(false);
                    return ClosedReceive();
                }

                ValueWebSocketReceiveResult result;
                try
                {
                    result = await state.Socket.ReceiveAsync(buffer.Slice(total), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    this.RequestClose(state, ConnectionCloseReason.Disconnect, flush: false);
                    return ClosedReceive();
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await AcknowledgePeerCloseAsync(state, state.Socket.CloseStatus, ct).ConfigureAwait(false);
                    this.RequestClose(state, ConnectionCloseReason.Disconnect, flush: false);
                    return ClosedReceive();
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await this.RejectReceiveAsync(state, ConnectionCloseReason.PolicyReject, ct).ConfigureAwait(false);
                    return ClosedReceive();
                }

                total += result.Count;

                // Count is checked immediately after every receive. No buffer
                // proportional to an untrusted message length is allocated.
                if (total > this.options.MaxMessageBytes)
                {
                    await this.RejectReceiveAsync(state, ConnectionCloseReason.PolicyReject, ct).ConfigureAwait(false);
                    return ClosedReceive();
                }

                if (result.EndOfMessage)
                {
                    this.Touch(state);
                    return new CarrierReceive(
                        Received: true,
                        ByteCount: total,
                        EndOfMessage: true,
                        Closed: false);
                }
            }
        }
        finally
        {
            state.ReceiveGate.Release();
        }
    }

    /// <inheritdoc />
    public bool TrySend(TransportConnectionId c, ReadOnlyMemory<byte> bytes)
    {
        this.ProcessPendingTimerCommands();

        if (!this.TryGetConnection(c, out var state))
        {
            return false;
        }

        if (bytes.Length == 0
            || bytes.Length > this.options.MaxMessageBytes
            || bytes.Length > EgressByteCapacity)
        {
            return false;
        }

        lock (state.Gate)
        {
            if (state.IsClosed || state.CloseRequested)
            {
                return false;
            }

            if (state.QueuedBytes > EgressByteCapacity - bytes.Length)
            {
                return false;
            }

            var copy = bytes.ToArray();
            var item = new EgressItem(copy);
            if (state.EgressQueue.TryPublish(in item).Status != EnqueueStatus.Accepted)
            {
                return false;
            }

            state.QueuedBytes += copy.Length;
            return true;
        }
    }

    /// <inheritdoc />
    public bool Close(TransportConnectionId c, ConnectionCloseReason reason)
    {
        this.ProcessPendingTimerCommands();

        return this.TryGetConnection(c, out var state)
            && this.RequestClose(state, reason, flush: true);
    }

    /// <summary>Applies timer-delivered typed close commands without creating a polling thread.</summary>
    public int ProcessPendingTimerCommands()
        => this.timerCommands.Drain();

    public async ValueTask DisposeAsync()
    {
        List<ConnectionState> snapshot;
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            snapshot = this.connections.Values.ToList();
        }

        this.accepted.Writer.TryComplete();
        this.timerCommands.Close();
        foreach (var state in snapshot)
        {
            this.RequestClose(state, ConnectionCloseReason.Disconnect, flush: false);
        }

        if (this.application is not null)
        {
            try
            {
                await this.application.StopAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch
            {
                // Disposal is best effort after all connection close signals have
                // been issued; Kestrel may already have observed the same close.
            }

            await this.application.DisposeAsync().ConfigureAwait(false);
            this.application = null;
        }
    }

    public void Dispose()
        => this.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private BindEndpointResult StartListener()
    {
        if (!TryNormalizeEndpoint(
                this.options,
                out var normalizedUri,
                out var outputScheme,
                out var validationError))
        {
            return new BindEndpointResult(false, this.options.ListenUri ?? string.Empty, validationError);
        }

        WebApplication? built = null;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(WebSocketByteCarrier).Assembly.GetName().Name,
                Args = Array.Empty<string>(),
            });
            builder.WebHost.UseKestrel();
            builder.WebHost.UseUrls(normalizedUri);
            builder.Logging.ClearProviders();

            built = builder.Build();
            built.UseWebSockets();
            built.Run(this.HandleRequestAsync);
            built.StartAsync().GetAwaiter().GetResult();

            var actual = built.Urls.FirstOrDefault() ?? normalizedUri;
            var actualUri = new Uri(actual, UriKind.Absolute);
            var bound = new UriBuilder(outputScheme, actualUri.Host, actualUri.Port)
            {
                Path = actualUri.AbsolutePath,
                Query = actualUri.Query,
            }.Uri.ToString();

            this.application = built;
            return new BindEndpointResult(true, bound, null);
        }
        catch (Exception)
        {
            if (built is not null)
            {
                try
                {
                    built.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                }
            }

            // ArtifactMissing is a registered stable error and is the honest
            // result for a secure endpoint with no usable TLS material.
            return new BindEndpointResult(false, this.options.ListenUri ?? string.Empty, "ArtifactMissing");
        }
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string header = context.Request.Headers["Sec-WebSocket-Protocol"].ToString();
        if (!TryParseCredentialProtocols(header, out var tokenBytes, out var nonce, out _))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!this.TryReserveConnection(out var connectionId))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        WebSocketConnection? socket = null;
        ConnectionState? state = null;
        try
        {
            // The selected response protocol is deliberately only the public
            // protocol marker; opaque token and nonce values never leave this scope.
            socket = await context.WebSockets.AcceptWebSocketAsync(WebSocketCarrierConstants.Subprotocol)
                .ConfigureAwait(false);

            var receivedAt = this.clock.Now;
            CredentialVerification verification;
            using (var credential = new OpaqueCredentialInput(tokenBytes))
            {
                verification = this.verifier.Verify(
                    credential,
                    new VerificationContext(
                        this.options.ProductId,
                        this.options.GameReleaseId,
                        nonce,
                        receivedAt));
            }
            Array.Clear(tokenBytes);

            if (verification.Verdict != CredentialVerdict.Accepted)
            {
                this.WriteRejectAudit(null);
                await RejectSocketAsync(socket).ConfigureAwait(false);
                return;
            }

            var replay = this.antiReplay.Check(verification.Principal, nonce, receivedAt);
            if (replay != AntiReplayVerdict.Ok)
            {
                this.WriteRejectAudit("SessionAntiReplay");
                await RejectSocketAsync(socket).ConfigureAwait(false);
                return;
            }

            state = new ConnectionState(
                connectionId,
                new ConnectionEpoch(0),
                socket);

            lock (this.gate)
            {
                this.pendingReservations--;
                this.connections.Add(connectionId.Value, state);
            }

            this.ScheduleIdle(state);
            state.SendLoop = this.RunSendLoopAsync(state);

            var accepted = new AcceptedConnection(
                state,
                // Return only the negotiated marker. Keeping credentials in
                // CarrierAccept would unnecessarily extend their lifetime.
                ImmutableArray.Create(WebSocketCarrierConstants.Subprotocol),
                new TransportAuthenticationEvidence(
                    verification.Principal,
                    connectionId,
                    state.Epoch,
                    this.options.ProductId,
                    this.options.GameReleaseId));
            if (this.acceptedQueue.TryPublish(in accepted).Status != EnqueueStatus.Accepted)
            {
                this.RequestClose(state, ConnectionCloseReason.PolicyReject, flush: false);
                return;
            }

            // Drop all handshake-carriage references before awaiting the lifetime
            // of the connection. The async state machine must not retain opaque
            // credentials or the raw protocol header for that whole lifetime.
            header = string.Empty;
            nonce = string.Empty;
            Array.Clear(tokenBytes);
            tokenBytes = Array.Empty<byte>();
            context.Request.Headers.Remove("Sec-WebSocket-Protocol");
            await state.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (state is not null)
            {
                this.RequestClose(state, ConnectionCloseReason.Disconnect, flush: false);
            }
        }
        catch (Exception) when (socket is not null)
        {
            if (state is not null)
            {
                this.RequestClose(state, ConnectionCloseReason.Disconnect, flush: false);
            }
            else
            {
                this.WriteRejectAudit(null);
                try
                {
                    await RejectSocketAsync(socket).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
        finally
        {
            // The verifier owns the credential only for the call duration. Clear
            // the decoded byte array on every path, including an upgrade failure.
            Array.Clear(tokenBytes);
            if (state is null)
            {
                this.ReleaseReservation(connectionId);
            }
        }
    }

    private async Task RunSendLoopAsync(ConnectionState state)
    {
        try
        {
            while (await state.Egress.Reader.WaitToReadAsync(state.SendToken).ConfigureAwait(false))
            {
                while (state.Egress.Reader.TryRead(out var item))
                {
                    lock (state.Gate)
                    {
                        state.QueuedBytes -= item.Bytes.Length;
                    }

                    if (state.Socket.State != WebSocketState.Open)
                    {
                        continue;
                    }

                    await state.Socket.SendAsync(
                            item.Bytes,
                            WebSocketMessageType.Text,
                            endOfMessage: true,
                            state.SendToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (state.SendCancellation.IsCancellationRequested
            || state.FlushCancellation?.IsCancellationRequested == true)
        {
        }
        catch
        {
            this.MarkClosed(state);
        }
        finally
        {
            if (state.CloseRequested && !state.IsClosed)
            {
                await this.SendCloseFrameAsync(state).ConfigureAwait(false);
            }
            else
            {
                this.MarkClosed(state);
            }
        }
    }

    private bool RequestClose(ConnectionState state, ConnectionCloseReason reason, bool flush)
    {
        lock (state.Gate)
        {
            if (state.IsClosed || state.CloseRequested)
            {
                return false;
            }

            state.CloseRequested = true;
            state.CloseReason = reason;
            state.FlushOnClose = flush;
            if (flush && state.FlushCancellation is null)
            {
                state.FlushCancellation = CancellationTokenSource.CreateLinkedTokenSource(state.SendCancellation.Token);
                state.FlushCancellation.CancelAfter(CloseFlushMilliseconds);
            }
            state.Egress.Writer.TryComplete();
        }

        if (!flush)
        {
            state.SendCancellation.Cancel();
            this.MarkClosed(state);
        }

        return true;
    }

    private async Task RejectReceiveAsync(
        ConnectionState state,
        ConnectionCloseReason reason,
        CancellationToken callerToken)
    {
        if (!this.RequestClose(state, reason, flush: true))
        {
            return;
        }

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            budget.CancelAfter(CloseFlushMilliseconds);
            await state.Completion.Task.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The bounded close budget is exhausted; MarkClosed will be reached
            // by the sender's cancellation path and the receive contract remains
            // a closed result.
        }
    }

    private async Task SendCloseFrameAsync(ConnectionState state)
    {
        if (!state.FlushOnClose)
        {
            this.MarkClosed(state);
            return;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(
            state.SendCancellation.Token,
            state.FlushCancellation?.Token ?? CancellationToken.None);
        budget.CancelAfter(CloseFlushMilliseconds);

        try
        {
            if (state.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await state.Socket.CloseOutputAsync(
                        CloseStatusOf(state.CloseReason),
                        state.CloseReason.ToString(),
                        budget.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            state.SendCancellation.Cancel();
            this.MarkClosed(state);
        }
    }

    private static async Task AcknowledgePeerCloseAsync(
        ConnectionState state,
        WebSocketCloseStatus? closeStatus,
        CancellationToken callerToken)
    {
        try
        {
            if (state.Socket.State == WebSocketState.CloseReceived)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
                budget.CancelAfter(CloseFlushMilliseconds);
                await state.Socket.CloseOutputAsync(
                        closeStatus ?? WebSocketCloseStatus.NormalClosure,
                        string.Empty,
                        budget.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }

    private void MarkClosed(ConnectionState state)
    {
        var remove = false;
        lock (state.Gate)
        {
            if (!state.IsClosed)
            {
                state.IsClosed = true;
                remove = true;
            }
        }

        if (!remove)
        {
            return;
        }

        if (state.IdleTimer is { } timer)
        {
            this.timers.Cancel(timer);
            state.IdleTimer = null;
        }

        lock (this.gate)
        {
            this.connections.Remove(state.Id.Value);
        }

        state.Egress.Writer.TryComplete();
        state.SendCancellation.Cancel();

        try
        {
            state.Socket.Dispose();
        }
        catch
        {
        }

        state.Completion.TrySetResult(true);
    }

    private void Touch(ConnectionState state)
    {
        if (state.IsClosed)
        {
            return;
        }

        if (state.IdleTimer is { } old)
        {
            this.timers.Cancel(old);
        }

        this.ScheduleIdle(state);
    }

    private void ScheduleIdle(ConnectionState state)
    {
        if (state.IsClosed)
        {
            return;
        }

        var idleSeconds = this.options.IdleTimeoutSeconds == 0
            ? TransportProvisionalLimits.IdleTimeoutSeconds
            : this.options.IdleTimeoutSeconds;
        var due = new MonotonicInstant(
            this.clock.Now.Ticks + TimeSpan.FromSeconds(idleSeconds).Ticks);
        var command = new ConnectionCommand.Close(state.Id, state.Epoch, ConnectionCloseReason.Disconnect);
        ConnectionCommand typedCommand = command;
        try
        {
            var timer = this.timers.Schedule<ConnectionCommand>(due, this.timerCommands, in typedCommand);
            if (state.IsClosed)
            {
                this.timers.Cancel(timer);
            }
            else
            {
                state.IdleTimer = timer;
            }
        }
        catch
        {
            state.IdleTimer = null;
        }
    }

    private void ApplyTimerCommand(in ConnectionCommand command)
    {
        if (command is not ConnectionCommand.Close close)
        {
            return;
        }

        if (this.TryGetConnection(close.Id, out var state) && state.Epoch == close.Epoch)
        {
            this.RequestClose(state, close.Reason, flush: true);
        }
    }

    private bool TryReserveConnection(out TransportConnectionId id)
    {
        lock (this.gate)
        {
            if (this.disposed
                || this.options.MaxConnections <= 0
                || this.connections.Count + this.pendingReservations >= this.options.MaxConnections)
            {
                id = default;
                return false;
            }

            this.pendingReservations++;
            var value = ++this.nextConnectionId;
            if (value == 0)
            {
                value = ++this.nextConnectionId;
            }

            id = new TransportConnectionId(value);
            return true;
        }
    }

    private void ReleaseReservation(TransportConnectionId id)
    {
        lock (this.gate)
        {
            if (this.pendingReservations > 0 && !this.connections.ContainsKey(id.Value))
            {
                this.pendingReservations--;
            }
        }
    }

    private bool TryGetConnection(TransportConnectionId id, out ConnectionState state)
    {
        lock (this.gate)
        {
            return this.connections.TryGetValue(id.Value, out state!);
        }
    }

    private void WriteRejectAudit(string? reasonCode)
    {
        ulong sequence;
        lock (this.gate)
        {
            sequence = this.auditSequence++;
        }

        try
        {
            _ = this.audit.WriteReleaseScopedReject(
                this.options.ReleasePoolId,
                this.options.ProductId,
                this.options.GameReleaseId,
                $"ws-reject-{sequence}",
                AuditProducer,
                sequence,
                reasonCode);
        }
        catch
        {
            // An authentication rejection must never become an application
            // envelope merely because the audit sink is backpressured.
        }
    }

    private static CarrierReceive ClosedReceive()
        => new(Received: false, ByteCount: 0, EndOfMessage: true, Closed: true);

    private static WebSocketCloseStatus CloseStatusOf(ConnectionCloseReason reason)
        => reason is ConnectionCloseReason.Fault or ConnectionCloseReason.PolicyReject
            ? (WebSocketCloseStatus)WebSocketCarrierConstants.CloseStatusPolicyViolation
            : WebSocketCloseStatus.NormalClosure;

    private static async Task RejectSocketAsync(WebSocketConnection socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var budget = new CancellationTokenSource(CloseFlushMilliseconds);
                await socket.CloseOutputAsync(
                        (WebSocketCloseStatus)WebSocketCarrierConstants.CloseStatusPolicyViolation,
                        "Policy violation",
                        budget.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            socket.Dispose();
        }
    }

    private static bool TryParseCredentialProtocols(
        string raw,
        out byte[] token,
        out string nonce,
        out ImmutableArray<string> requested)
    {
        token = Array.Empty<byte>();
        nonce = string.Empty;
        requested = ImmutableArray<string>.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || !string.Equals(parts[0], WebSocketCarrierConstants.Subprotocol, StringComparison.Ordinal)
            || !TryDecodeBase64Url(parts[1], out token)
            || token.Length == 0
            || !IsBase64Url(parts[2]))
        {
            Array.Clear(token);
            token = Array.Empty<byte>();
            return false;
        }

        nonce = parts[2];
        // Keep only the negotiated marker in the public carrier result; token and
        // nonce are intentionally not retained after this method returns.
        requested = ImmutableArray.Create(WebSocketCarrierConstants.Subprotocol);
        return true;
    }

    private static bool TryDecodeBase64Url(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (!IsBase64Url(value) || value.Length % 4 == 1)
        {
            return false;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private static bool IsBase64Url(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length % 4 == 1)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!((c is >= 'A' and <= 'Z')
                || (c is >= 'a' and <= 'z')
                || (c is >= '0' and <= '9')
                || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryNormalizeEndpoint(
        in WebSocketCarrierOptions options,
        out string normalized,
        out string outputScheme,
        out string? error)
    {
        normalized = string.Empty;
        outputScheme = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(options.ListenUri)
            || options.MaxMessageBytes <= 0
            || options.MaxMessageBytes > EgressByteCapacity
            || options.MaxConnections <= 0
            || options.IdleTimeoutSeconds < 0
            || string.IsNullOrWhiteSpace(options.ProductId)
            || string.IsNullOrWhiteSpace(options.GameReleaseId)
            || string.IsNullOrWhiteSpace(options.ReleasePoolId))
        {
            error = "InvalidArgument";
            return false;
        }

        if (!Uri.TryCreate(options.ListenUri, UriKind.Absolute, out var uri))
        {
            error = "InvalidArgument";
            return false;
        }

        if (uri.Port is < 0 or > 65535)
        {
            error = "InvalidArgument";
            return false;
        }

        if (string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase))
        {
            outputScheme = "ws";
            var host = uri.Host.Trim('[', ']');
            var loopback = string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
                || string.Equals(host, "::1", StringComparison.Ordinal);

            if (options.RequireTls)
            {
                error = "TargetProfileMismatch";
                return false;
            }

            if (!options.AllowInsecureLoopback)
            {
                error = "CapabilityMissing";
                return false;
            }

            if (!loopback
                || (options.HostProfile is not "LocalSplitProcess" and not "LocalEmbedded"))
            {
                error = "TargetProfileMismatch";
                return false;
            }
        }
        else if (string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
        {
            outputScheme = "wss";
            if (!options.RequireTls)
            {
                // A secure endpoint is still valid when TLS is not required; the
                // URI itself requests it and Kestrel remains responsible for certs.
            }
        }
        else
        {
            error = "InvalidArgument";
            return false;
        }

        var scheme = outputScheme == "wss" ? "https" : "http";
        var builder = new UriBuilder(uri)
        {
            Scheme = scheme,
            Port = uri.Port,
        };
        normalized = builder.Uri.ToString();
        return true;
    }

    private sealed class ConnectionState : IDisposable
    {
        internal ConnectionState(
            TransportConnectionId id,
            ConnectionEpoch epoch,
            WebSocketConnection socket)
        {
            this.Id = id;
            this.Epoch = epoch;
            this.Socket = socket;
            this.Egress = Channel.CreateBounded<EgressItem>(new BoundedChannelOptions(EgressCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
            this.EgressQueue = new EgressQueue(this.Egress);
        }

        internal readonly object Gate = new();
        internal readonly SemaphoreSlim ReceiveGate = new(1, 1);
        internal readonly CancellationTokenSource SendCancellation = new();
        internal readonly Channel<EgressItem> Egress;
        internal readonly EgressQueue EgressQueue;
        internal readonly TaskCompletionSource<bool> Completion = new(
            false,
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TransportConnectionId Id;
        internal readonly ConnectionEpoch Epoch;
        internal readonly WebSocketConnection Socket;
        internal Task? SendLoop;
        internal TimerId? IdleTimer;
        internal long QueuedBytes;
        internal bool CloseRequested;
        internal bool FlushOnClose;
        internal bool IsClosed;
        internal ConnectionCloseReason CloseReason;
        internal CancellationTokenSource? FlushCancellation;

        // Interface-shaped view keeps the queue registration machine-readable;
        // the concrete field remains the hot-path type used by the sender.
        private IBoundedOutbox<EgressItem> EgressQueueContract => this.EgressQueue;

        internal CancellationToken SendToken
            => this.FlushCancellation?.Token ?? this.SendCancellation.Token;

        public void Dispose()
        {
            this.SendCancellation.Dispose();
            this.ReceiveGate.Dispose();
            this.FlushCancellation?.Dispose();
        }
    }

    private readonly record struct EgressItem(byte[] Bytes);

    private readonly record struct AcceptedConnection(
        ConnectionState State,
        ImmutableArray<string> RequestedSubprotocols,
        TransportAuthenticationEvidence AuthenticationEvidence);

    private sealed class AcceptedQueue : IBoundedOutbox<AcceptedConnection>
    {
        private readonly Channel<AcceptedConnection> channel;

        internal AcceptedQueue(Channel<AcceptedConnection> channel) => this.channel = channel;

        public EnqueueResult TryPublish(in AcceptedConnection item)
            => this.channel.Writer.TryWrite(item)
                ? new EnqueueResult(EnqueueStatus.Accepted, null)
                : new EnqueueResult(EnqueueStatus.Full, "QueueFull");
    }

    private sealed class EgressQueue : IBoundedOutbox<EgressItem>
    {
        private readonly Channel<EgressItem> channel;

        internal EgressQueue(Channel<EgressItem> channel) => this.channel = channel;

        public EnqueueResult TryPublish(in EgressItem item)
            => this.channel.Writer.TryWrite(item)
                ? new EnqueueResult(EnqueueStatus.Accepted, null)
                : new EnqueueResult(EnqueueStatus.Full, "QueueFull");
    }

    /// <summary>
    /// Timer targets are typed commands, not delegates. A due idle timer calls this
    /// inbox, which applies the command and leaves no adapter-owned polling thread.
    /// </summary>
    private sealed class TimerCommandInbox : IBoundedInbox<ConnectionCommand>
    {
        private readonly WebSocketByteCarrier owner;
        private readonly Queue<ConnectionCommand> commands = new();
        private readonly object gate = new();
        private readonly QueueBudget budget;
        private bool closed;

        internal TimerCommandInbox(WebSocketByteCarrier owner, int capacity)
        {
            this.owner = owner;
            this.budget = new QueueBudget(Math.Max(1, capacity), Math.Max(1, capacity * 256L));
        }

        public QueueBudget Budget => this.budget;

        public EnqueueResult TryEnqueue(in ConnectionCommand item)
        {
            lock (this.gate)
            {
                if (this.closed)
                {
                    return new EnqueueResult(EnqueueStatus.Closed, "ContextClosing");
                }

                if (this.commands.Count >= this.budget.MaxItems)
                {
                    return new EnqueueResult(EnqueueStatus.Full, "QueueFull");
                }

                this.commands.Enqueue(item);
            }

            // This is still typed timer delivery: no unbounded callback or thread
            // is exposed by ITimerService, and the command itself is retained for
            // ProcessPendingTimerCommands diagnostics.
            this.owner.ApplyTimerCommand(in item);
            return new EnqueueResult(EnqueueStatus.Accepted, null);
        }

        public bool TryDequeue(out ConnectionCommand item)
        {
            lock (this.gate)
            {
                if (this.commands.Count > 0)
                {
                    item = this.commands.Dequeue();
                    return true;
                }
            }

            item = null!;
            return false;
        }

        public int Count
        {
            get
            {
                lock (this.gate)
                {
                    return this.commands.Count;
                }
            }
        }

        public void Close()
        {
            lock (this.gate)
            {
                this.closed = true;
                this.commands.Clear();
            }
        }

        internal int Drain()
        {
            var count = 0;
            while (this.TryDequeue(out _))
            {
                count++;
            }

            return count;
        }
    }
}
