using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Xml.Linq;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.Transport.WebSocket;
using Xunit;

namespace Lumio.Server.MvpHost.Transport.WebSocket.Tests;

public sealed class CarrierContractTests
{
    private static readonly string[] ExpectedFrameworks = { "net10.0" };

    [Fact]
    public void ProjectShapeUsesLayerFiveAndOnlyTransportReference()
    {
        var path = Path.Combine(
            LocateMvpHostRoot(),
            "src",
            "Lumio.Server.MvpHost.Transport.WebSocket",
            "Lumio.Server.MvpHost.Transport.WebSocket.csproj");
        var project = XDocument.Load(path);

        Assert.Equal("5", project.Descendants("MvpHostLayer").Single().Value);
        Assert.Single(
            project.Descendants("ProjectReference"),
            reference => reference.Attribute("Include")?.Value?.Contains("Lumio.Server.MvpHost.Transport.csproj", StringComparison.Ordinal) == true);
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Single(
            project.Descendants("FrameworkReference"),
            reference => reference.Attribute("Include")?.Value == "Microsoft.AspNetCore.App");
        Assert.DoesNotContain(
            project.Descendants("OutputType").Select(element => element.Value),
            value => string.Equals(value, "Exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoThirdPartyPackageTest()
    {
        var path = Path.Combine(
            LocateMvpHostRoot(),
            "src",
            "Lumio.Server.MvpHost.Transport.WebSocket",
            "obj",
            "project.assets.json");
        Assert.True(File.Exists(path), "restore must run before the package graph assertion");

        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var frameworks = document["project"]!["frameworks"]!.AsObject().Select(property => property.Key).ToArray();
        Assert.Equal(ExpectedFrameworks, frameworks);

        var libraries = document["libraries"]!.AsObject();
        var thirdParty = libraries
            .Where(property => property.Value!["type"]?.GetValue<string>() == "package")
            .Select(property => property.Key)
            .Where(key => !key.StartsWith("Microsoft.CodeAnalysis.BannedApiAnalyzers/", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(thirdParty);
    }

    [Fact]
    public void TransportCoreStaysFrameworkFreeTest()
    {
        var path = Path.Combine(
            LocateMvpHostRoot(),
            "src",
            "Lumio.Server.MvpHost.Transport",
            "Lumio.Server.MvpHost.Transport.csproj");
        var project = XDocument.Load(path);
        Assert.Empty(project.Descendants("FrameworkReference"));
    }

    [Fact]
    public void CarrierImplementsFrozenByteCarrierSurface()
    {
        Assert.True(typeof(IByteCarrier).IsAssignableFrom(typeof(WebSocketByteCarrier)));
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(WebSocketByteCarrier)));

        var methods = typeof(WebSocketByteCarrier)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        var properties = typeof(WebSocketByteCarrier)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(WebSocketByteCarrier.AcceptAsync), methods);
        Assert.Contains(nameof(WebSocketByteCarrier.ReceiveAsync), methods);
        Assert.Contains(nameof(WebSocketByteCarrier.TrySend), methods);
        Assert.Contains(nameof(WebSocketByteCarrier.Close), methods);
        Assert.Contains(nameof(WebSocketByteCarrier.BoundUri), properties);
    }

    [Fact]
    public void NoSocketTypeTest()
    {
        var sourceRoot = Path.Combine(
            LocateMvpHostRoot(),
            "src",
            "Lumio.Server.MvpHost.Transport.WebSocket");
        var banned = new[]
        {
            "System.Net." + "Sockets.Socket",
            "Thread.Sleep",
            "Task.Delay",
            "DateTime.UtcNow",
            "DateTimeOffset.UtcNow",
        };

        var offenders = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => banned
                .Where(value => File.ReadAllText(file).Contains(value, StringComparison.Ordinal))
                .Select(value => $"{Path.GetFileName(file)}: {value}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ConstantsRetainPrivateMvpMarkers()
    {
        Assert.Equal("lumio.mvp.v0", WebSocketCarrierConstants.Subprotocol);
        Assert.Equal(1008, WebSocketCarrierConstants.CloseStatusPolicyViolation);
        Assert.Equal("WebSocketTransport", WebSocketCarrierConstants.ProvisionalTransportCapability);
    }

    [Theory]
    [InlineData(false, "LocalSplitProcess", "ws://127.0.0.1:0/", "CapabilityMissing")]
    [InlineData(true, "RemoteDS", "ws://127.0.0.1:0/", "TargetProfileMismatch")]
    [InlineData(true, "LocalSplitProcess", "ws://localhost:0/", "TargetProfileMismatch")]
    [InlineData(true, "LocalSplitProcess", "ws://127.0.0.1:0/", "none")]
    public void InsecureLoopbackGatingTest(
        bool allowInsecure,
        string hostProfile,
        string uri,
        string expectedError)
    {
        using var clock = new NoopClock();
        using var timers = new NoopTimers();
        using var carrier = WebSocketByteCarrier.Create(
            new WebSocketCarrierOptions(
                uri,
                RequireTls: false,
                AllowInsecureLoopback: allowInsecure,
                HostProfile: hostProfile,
                MaxMessageBytes: 4096,
                MaxConnections: 2,
                IdleTimeoutSeconds: 15,
                ProductId: "A",
                GameReleaseId: "A-1.1.0",
                ReleasePoolId: "pool"),
            new NoopVerifier(),
            new NoopReplay(),
            clock,
            timers,
            new NoopAudit());

        var result = carrier.BindEndpoint();
        if (expectedError == "none")
        {
            Assert.True(result.Bound);
            Assert.Null(result.StableErrorId);
        }
        else
        {
            Assert.False(result.Bound);
            Assert.Equal(expectedError, result.StableErrorId);
        }
    }

    [Fact]
    public void RequireTlsRejectsWsWithoutSilentDowngrade()
    {
        using var clock = new NoopClock();
        using var timers = new NoopTimers();
        using var carrier = WebSocketByteCarrier.Create(
            new WebSocketCarrierOptions(
                "ws://127.0.0.1:0/",
                RequireTls: true,
                AllowInsecureLoopback: true,
                HostProfile: "LocalSplitProcess",
                MaxMessageBytes: 4096,
                MaxConnections: 2,
                IdleTimeoutSeconds: 15,
                ProductId: "A",
                GameReleaseId: "A-1.1.0",
                ReleasePoolId: "pool"),
            new NoopVerifier(),
            new NoopReplay(),
            clock,
            timers,
            new NoopAudit());

        var result = carrier.BindEndpoint();
        Assert.False(result.Bound);
        Assert.Equal("TargetProfileMismatch", result.StableErrorId);
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

    private sealed class NoopClock : IMonotonicClock, IDisposable
    {
        public MonotonicInstant Now => new(0);
        public void Dispose() { }
    }

    private sealed class NoopTimers : ITimerService
    {
        public TimerId Schedule<T>(MonotonicInstant dueAt, IBoundedInbox<T> target, in T command) => new(1);
        public bool Cancel(TimerId id) => true;
        public void Dispose() { }
    }

    private sealed class NoopVerifier : ICredentialVerifier
    {
        public CredentialVerification Verify(OpaqueCredentialInput credential, in VerificationContext context)
            => new(CredentialVerdict.Accepted, new PrincipalId("p"), null);
    }

    private sealed class NoopReplay : IAntiReplayWindow
    {
        public AntiReplayVerdict Check(PrincipalId principal, string nonce, MonotonicInstant receivedAt)
            => AntiReplayVerdict.Ok;
    }

    private sealed class NoopAudit : IAuditWriter
    {
        public EnqueueResult WriteReleaseScopedReject(string releasePoolId, string productId, string gameReleaseId, string traceId, string producerId, ulong eventSeq, string? reasonCode)
            => new(EnqueueStatus.Accepted, null);
        public EnqueueResult WriteSessionScoped(ServerSessionId sessionId, string productId, string gameReleaseId, string traceId, string producerId, ulong eventSeq, string message)
            => new(EnqueueStatus.Accepted, null);
    }
}
