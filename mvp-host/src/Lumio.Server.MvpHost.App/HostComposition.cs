using System;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.MvpHost.Admission;
using Lumio.Server.MvpHost.Auth;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.Transport;

namespace Lumio.Server.MvpHost.App;

/// <summary>
/// Explicit composition root for the process shell. No container or ambient
/// service lookup is used; each dependency is constructed and owned here.
/// </summary>
public sealed class HostComposition : IAsyncDisposable
{
#if MVP_HOST_FULL_GRAPH
    private readonly FullGraphComposition? fullGraph;
#endif
    private bool disposed;

    private HostComposition(
        HostCommandLineOptions options,
        IMonotonicClock clock,
        IWallClock wallClock,
        ITimerService timers,
        INamedThreadSupervisor threads,
        IHostTraceSink trace,
        JsonLinesHostTraceSink? traceFile,
        ObservabilityServices observability,
        InjectedExactByteCredentialVerifier verifier,
        MvpAntiReplayWindow antiReplay,
        MvpAuthorizationService authorization,
        PassThroughFaultPolicy transportFaultPolicy,
        HostProtocolServer protocolServer,
        ISessionAdminPort? admin
#if MVP_HOST_FULL_GRAPH
        , FullGraphComposition? fullGraph
#endif
        )
    {
        Options = options;
        Clock = clock;
        WallClock = wallClock;
        Timers = timers;
        Threads = threads;
        Trace = trace;
        TraceFile = traceFile;
        Observability = observability;
        Verifier = verifier;
        AntiReplay = antiReplay;
        Authorization = authorization;
        TransportFaultPolicy = transportFaultPolicy;
        ProtocolServer = protocolServer;
        Admin = admin;
#if MVP_HOST_FULL_GRAPH
        this.fullGraph = fullGraph;
#endif
    }

    public HostCommandLineOptions Options { get; }

    public IMonotonicClock Clock { get; }

    public IWallClock WallClock { get; }

    public ITimerService Timers { get; }

    public INamedThreadSupervisor Threads { get; }

    public IHostTraceSink Trace { get; }

    public JsonLinesHostTraceSink? TraceFile { get; }

    public ObservabilityServices Observability { get; }

    public InjectedExactByteCredentialVerifier Verifier { get; }

    public MvpAntiReplayWindow AntiReplay { get; }

    public MvpAuthorizationService Authorization { get; }

    /// <summary>Production always injects the explicit pass-through policy.</summary>
    public PassThroughFaultPolicy TransportFaultPolicy { get; }

    public HostProtocolServer ProtocolServer { get; }

    public ISessionAdminPort? Admin { get; }

    public TestControlServer? TestControl { get; private set; }

    public string BoundListenUri
    {
        get
        {
#if MVP_HOST_FULL_GRAPH
            return fullGraph?.BoundUri ?? ProtocolServer.BoundUri;
#else
            return ProtocolServer.BoundUri;
#endif
        }
    }

    public string BoundTestControlUri => TestControl?.BoundUri ?? "-";

    internal bool HasFatalFault
    {
        get
        {
            _ = Options;
#if MVP_HOST_FULL_GRAPH
            return fullGraph?.HasFatalFault == true;
#else
            return false;
#endif
        }
    }

    internal Task FatalTask
    {
        get
        {
            _ = Options;
#if MVP_HOST_FULL_GRAPH
            return fullGraph?.FatalTask ?? NeverCompletingTask;
#else
            return NeverCompletingTask;
#endif
        }
    }

    internal Task CompletionTask
    {
        get
        {
            _ = Options;
#if MVP_HOST_FULL_GRAPH
            return fullGraph?.CompletionTask ?? NeverCompletingTask;
#else
            return NeverCompletingTask;
#endif
        }
    }

    public static RoomAdmissionRegistry CreateRoomAdmissionRegistry(
        byte admissionKeyId,
        ReadOnlyMemory<byte> admissionPublicKey,
        IAdmissionClock clock,
        IMonotonicClock monotonic,
        ITimerService timers,
        int reconnectWindowSeconds)
    {
        return RoomAdmissionFactory.Create(
            admissionKeyId,
            admissionPublicKey,
            clock,
            monotonic,
            timers,
            reconnectWindowSeconds);
    }

    public static HostComposition Create(HostCommandLineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.SharedSecretFile))
        {
            throw new InvalidOperationException("--shared-secret-file is required and must be readable");
        }

        var clock = PlatformModule.CreateClock();
        var wallClock = PlatformModule.CreateWallClock();
        var timers = PlatformModule.CreateTimerService(clock);
        var threads = PlatformModule.CreateThreadSupervisor();
        var traceFile = options.AuditTraceFile is null ? null : new JsonLinesHostTraceSink(options.AuditTraceFile);
        IHostTraceSink trace = (IHostTraceSink?)traceFile ?? new NullHostTraceSink();

        var identity = new HostIdentity(options.ProductId, options.GameReleaseId, "lumio-mvp-host");
        var auditInbox = PlatformModule.CreateInbox<AuditRecord>(new QueueBudget(256, 256 * 1024));
        var diagnosticInbox = PlatformModule.CreateInbox<DiagnosticRecord>(new QueueBudget(256, 256 * 1024));
        var observability = ObservabilityModule.Create(
            auditInbox,
            diagnosticInbox,
            wallClock,
            trace,
            in identity);

        var verifier = InjectedExactByteCredentialVerifier.FromSecretFile(options.SharedSecretFile);
        var antiReplay = MvpAntiReplayWindow.Create(
            clock,
            AuthProvisionalDefaults.AntiReplayWindowSeconds,
            AuthProvisionalDefaults.ReplayStormThreshold);
        var authorization = MvpAuthorizationService.Create(
            verifier,
            antiReplay,
            clock,
            observability,
            in identity,
            HostCommandLineOptions.DefaultReleasePoolId);
        var transportFaultPolicy = new PassThroughFaultPolicy();
        var protocolServer = new HostProtocolServer(options, trace, observability.Audit, authorization, clock);
        ISessionAdminPort? admin = options.EnableTestControl
            ? new HostSessionAdminAdapter(protocolServer)
            : null;
#if MVP_HOST_FULL_GRAPH
        var fullGraph = FullGraphComposition.Create(
            options,
            authorization,
            verifier,
            antiReplay,
            clock,
            timers,
            threads,
            observability,
            transportFaultPolicy,
            trace);
        admin = fullGraph.Admin;
#endif

        return new HostComposition(
            options,
            clock,
            wallClock,
            timers,
            threads,
            trace,
            traceFile,
            observability,
            verifier,
            antiReplay,
            authorization,
            transportFaultPolicy,
            protocolServer,
            admin
#if MVP_HOST_FULL_GRAPH
            , fullGraph
#endif
            );
    }

    private static readonly Task NeverCompletingTask =
        new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously).Task;

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
#if MVP_HOST_FULL_GRAPH
        if (fullGraph is not null)
        {
            fullGraph.Start();
        }
        else
#endif
        {
            await ProtocolServer.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        if (Options.EnableTestControl)
        {
            var requested = Options.TestControlListenUri ?? "http://127.0.0.1:0";
            LiveElevenHost? liveEleven = null;
#if MVP_HOST_FULL_GRAPH
            liveEleven = fullGraph?.LiveEleven;
#endif
            TestControl = await TestControlServer.StartAsync(
                requested,
                () => Admin,
                Clock,
                liveEleven,
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal AckResult BeginShutdown()
    {
        if (disposed)
        {
            return new AckResult(false, "ContextClosing");
        }

        var deadline = Clock.Now;
#if MVP_HOST_FULL_GRAPH
        if (fullGraph is not null)
        {
            return fullGraph.BeginShutdown(deadline);
        }
#endif
        return ProtocolServer.BeginDrain(deadline);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (TestControl is not null)
        {
            await TestControl.DisposeAsync().ConfigureAwait(false);
        }

#if MVP_HOST_FULL_GRAPH
        if (fullGraph is not null)
        {
            await fullGraph.DisposeAsync().ConfigureAwait(false);
        }
        else
#endif
        {
            await ProtocolServer.DisposeAsync().ConfigureAwait(false);
        }
        Authorization.CloseQueues();
        Timers.Dispose();
        Threads.Dispose();
        TraceFile?.Dispose();
    }

    private sealed class HostSessionAdminAdapter : ISessionAdminPort
    {
        private readonly HostProtocolServer server;

        internal HostSessionAdminAdapter(HostProtocolServer server) => this.server = server;

        public AckResult BeginDrain(MonotonicInstant graceDeadline) => server.BeginDrain(graceDeadline);

        public AckResult Kick(ServerSessionId sessionId, string registeredReasonCode)
            => server.Kick(sessionId, registeredReasonCode);

        public AckResult InjectWorldMutation(ServerSessionId onBehalfOf, ReadOnlyMemory<byte> opaqueCommand)
            => server.InjectWorldMutation(onBehalfOf, opaqueCommand);
    }
}
