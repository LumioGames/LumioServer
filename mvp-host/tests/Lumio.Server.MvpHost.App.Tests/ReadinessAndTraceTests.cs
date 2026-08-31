using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Platform;
using Xunit;

namespace Lumio.Server.MvpHost.App.Tests;

public sealed class ReadinessAndTraceTests
{
    private static readonly string[] QuiesceEffects =
    {
        "AdmissionClosed", "Drained", "SnapshotCut", "Stopped",
    };

    [Fact]
    public async Task SignalShutdownWaitsForQuiesceBeforeReturningSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lumio-signal-quiesce-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var secret = Path.Combine(root, "secret.bin");
        var trace = Path.Combine(root, "server.jsonl");
        File.WriteAllBytes(secret, Encoding.UTF8.GetBytes("test-secret"));
        var options = new App.HostCommandLineOptions(
            "ws://127.0.0.1:0",
            true,
            App.HostCommandLineOptions.DefaultHostProfile,
            App.HostCommandLineOptions.DefaultProductId,
            App.HostCommandLineOptions.DefaultGameReleaseId,
            secret,
            1,
            true,
            "http://127.0.0.1:0",
            trace);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await using var composition = App.HostComposition.Create(options);
            await composition.StartAsync(timeout.Token);

            var exitCode = await App.Program.QuiesceForSignalAsync(composition, timeout.Token);

            Assert.Equal(App.HostExitCodes.Success, exitCode);
            await composition.DisposeAsync();
            var effects = new List<string>();
            foreach (var line in File.ReadLines(trace))
            {
                using var document = JsonDocument.Parse(line);
                var traceEntry = document.RootElement;
                if (traceEntry.GetProperty("kind").GetString() == "ack"
                    && traceEntry.GetProperty("effect").ValueKind == JsonValueKind.String
                    && traceEntry.GetProperty("effect").GetString() is { } effect
                    && Array.IndexOf(QuiesceEffects, effect) >= 0)
                {
                    effects.Add(effect);
                }
            }

            Assert.Equal(QuiesceEffects, effects);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void IngressRoutingRejectsStaleFramesAndReportsWorldQueueFull()
    {
        var composition = typeof(App.Program).Assembly.GetType(
            "Lumio.Server.MvpHost.App.FullGraphComposition",
            throwOnError: true)!;
        var ingressType = composition.GetNestedType("MultiplexedIngress", BindingFlags.NonPublic)!;
        var ingress = ingressType
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                new[] { typeof(QueueBudget) },
                modifiers: null)!
            .Invoke(new object[] { new QueueBudget(1, 16) });
        var route = composition.GetMethod(
            "RouteIngress",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(route);

        var envelope = new ValidatedEnvelopeBytes(new byte[] { 1 }, default);
        var rejected = InvokeRoute(
            route,
            new AckResult(false, "StaleConnectionGeneration"),
            ingress,
            envelope);
        var accepted = InvokeRoute(route, new AckResult(true, null), ingress, envelope);
        var full = InvokeRoute(route, new AckResult(true, null), ingress, envelope);

        Assert.Equal(EnqueueStatus.Closed, rejected.Status);
        Assert.Equal("StaleConnectionGeneration", rejected.StableErrorId);
        Assert.Equal(EnqueueStatus.Accepted, accepted.Status);
        Assert.Equal(EnqueueStatus.Full, full.Status);
        Assert.Equal("QueueFull", full.StableErrorId);
    }

    [Fact]
    public void StaleConnectionGenerationCannotMatchTheCurrentConnection()
    {
        var current = new ConnectionEpoch(4);

        Assert.True(App.FullGraphComposition.IsCurrentConnectionGeneration(current, new ConnectionEpoch(4)));
        Assert.False(App.FullGraphComposition.IsCurrentConnectionGeneration(current, new ConnectionEpoch(3)));
        Assert.False(App.FullGraphComposition.IsCurrentConnectionGeneration(current, new ConnectionEpoch(5)));
    }

    [Fact]
    public void StaleIngressEventCannotDrainTheCurrentGeneration()
    {
        var current = new ConnectionEpoch(4);
        var stale = new ConnectionEpoch(3);

        Assert.True(App.FullGraphComposition.IsCurrentIngressGeneration(current, current, current));
        Assert.False(App.FullGraphComposition.IsCurrentIngressGeneration(current, current, stale));
        Assert.False(App.FullGraphComposition.IsCurrentIngressGeneration(current, stale, current));
    }

    [Fact]
    public void SessionLocalFaultAdjudicationDoesNotEscalateToProcessFatal()
    {
        var method = typeof(App.Program).Assembly
            .GetType("Lumio.Server.MvpHost.App.FullGraphComposition", throwOnError: true)!
            .GetMethod("ShouldEscalateFault", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var local = new FaultAdjudication(HostFaultClass.SessionLocalProven, false, true);
        var slot = new FaultAdjudication(HostFaultClass.SlotStateUnproven, true, false);
        var process = new FaultAdjudication(HostFaultClass.ProcessFault, true, false);

        Assert.False((bool)method!.Invoke(null, new object[] { local })!);
        Assert.True((bool)method.Invoke(null, new object[] { slot })!);
        Assert.True((bool)method.Invoke(null, new object[] { process })!);
    }

    [Fact]
    public void FullGraphUsesTimerDrivenPacingInsteadOfItsTransportPump()
    {
        var composition = typeof(App.Program).Assembly.GetType(
            "Lumio.Server.MvpHost.App.FullGraphComposition",
            throwOnError: true)!;
        var pacing = typeof(App.Program).Assembly.GetType(
            "Lumio.Server.MvpHost.App.MvpPacingController",
            throwOnError: false);

        Assert.NotNull(pacing);
        Assert.Contains(
            composition.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == pacing);
        Assert.Null(composition.GetMethod(
            "QueueTickPermit",
            BindingFlags.Instance | BindingFlags.NonPublic));
    }

    private static EnqueueResult InvokeRoute(
        MethodInfo route,
        AckResult sessionResult,
        object ingress,
        ValidatedEnvelopeBytes envelope)
        => (EnqueueResult)route.Invoke(
            obj: null,
            parameters: new object[] { sessionResult, ingress, envelope })!;

    [Fact]
    public void ReadyLineIsSingleAndParsable()
    {
        var line = new App.HostReadyLine("ws://127.0.0.1:43123", "http://127.0.0.1:43124").ToString();

        Assert.True(App.HostReadyLine.TryParse(line, out var ready));
        Assert.Equal("ws://127.0.0.1:43123", ready.ListenUri);
        Assert.Equal("http://127.0.0.1:43124", ready.TestControlUri);
        Assert.False(App.HostReadyLine.TryParse(line + "\nextra", out _));
    }

    [Fact]
    public void ReadyLineUsesDashWhenTestControlIsAbsent()
    {
        var line = new App.HostReadyLine("ws://127.0.0.1:43123", "-").ToString();

        Assert.True(App.HostReadyLine.TryParse(line, out var ready));
        Assert.Equal("-", ready.TestControlUri);
    }

    [Fact]
    public void ServerTraceAlwaysWritesTheFixedSeventeenKeyShape()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lumio-server-trace-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var sink = new App.JsonLinesHostTraceSink(path))
            {
                sink.Ack("ReadGate", 1, 2, 3);
                sink.State("session-1", "Active", 4, 2, 5);
            }

            var expected = new HashSet<string>(StringComparer.Ordinal)
            {
                "seq", "kind", "eventId", "timestamp", "category", "severity", "scope",
                "releasePoolId", "sessionId", "reasonCode", "admissionAttemptId", "effect",
                "sessionState", "authorityRevision", "slotEpoch", "connectionEpoch", "grantEpoch",
            };
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            ulong previous = 0;
            var first = true;
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                var properties = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    properties.Add(property.Name);
                }

                Assert.Equal(expected, properties);
                var sequence = document.RootElement.GetProperty("seq").GetUInt64();
                if (!first)
                {
                    Assert.True(sequence > previous);
                }

                previous = sequence;
                first = false;
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SmokeTraceAlwaysWritesTheFixedSevenKeyShapeAndMonotonicSteps()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lumio-smoke-trace-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var trace = new SmokeClient.SmokeTraceWriter(path))
            {
                trace.Record("out", "Handshake", "sent", true);
                trace.Record("in", null, "closed", false, "detail");
            }

            var expected = new HashSet<string>(StringComparer.Ordinal)
            {
                "step", "direction", "messageType", "assertion", "passed", "detail",
            };
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Equal(1, JsonDocument.Parse(lines[0]).RootElement.GetProperty("step").GetInt32());
            Assert.Equal(2, JsonDocument.Parse(lines[1]).RootElement.GetProperty("step").GetInt32());
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                var properties = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    properties.Add(property.Name);
                }

                Assert.Equal(expected, properties);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
