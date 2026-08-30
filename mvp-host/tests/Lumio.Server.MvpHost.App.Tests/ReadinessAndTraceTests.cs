using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Platform;
using Xunit;

namespace Lumio.Server.MvpHost.App.Tests;

public sealed class ReadinessAndTraceTests
{
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
