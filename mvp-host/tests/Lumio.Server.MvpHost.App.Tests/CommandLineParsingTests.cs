using System;
using System.Collections.Generic;
using Xunit;

namespace Lumio.Server.MvpHost.App.Tests;

public sealed class CommandLineParsingTests
{
    [Fact]
    public void HostParserAcceptsTheDocumentedLocalConfiguration()
    {
        var result = App.HostCommandLineParser.Parse(new List<string>
        {
            "--listen", "ws://127.0.0.1:0",
            "--allow-insecure-loopback",
            "--host-profile", "LocalSplitProcess",
            "--product-id", "A",
            "--game-release-id", "A-1.1.0",
            "--shared-secret-file", "secret.bin",
            "--reconnect-window-seconds", "10",
            "--enable-test-control",
            "--test-control-listen", "http://127.0.0.1:0",
            "--audit-trace-file", "trace.jsonl",
        });

        Assert.True(result.IsValid, result.Error);
        Assert.NotNull(result.Options);
        Assert.Equal("LocalSplitProcess", result.Options!.HostProfile);
        Assert.Equal(10, result.Options.ReconnectWindowSeconds);
        Assert.True(result.Options.EnableTestControl);
    }

    [Fact]
    public void HostParserRejectsInsecureWsWithoutTheExplicitSwitch()
    {
        var result = App.HostCommandLineParser.Parse(new List<string>
        {
            "--listen", "ws://127.0.0.1:0",
            "--shared-secret-file", "secret.bin",
        });

        Assert.False(result.IsValid);
        Assert.Contains("allow-insecure-loopback", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void HostParserRejectsTestControlOutsideLoopback()
    {
        var result = App.HostCommandLineParser.Parse(new List<string>
        {
            "--listen", "ws://127.0.0.1:0",
            "--allow-insecure-loopback",
            "--shared-secret-file", "secret.bin",
            "--enable-test-control",
            "--test-control-listen", "http://192.0.2.10:0",
        });

        Assert.False(result.IsValid);
        Assert.Contains("loopback", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostParserRequiresTheTraceGate()
    {
        var result = App.HostCommandLineParser.Parse(new List<string>
        {
            "--listen", "ws://127.0.0.1:0",
            "--allow-insecure-loopback",
            "--shared-secret-file", "secret.bin",
            "--audit-trace-file", "trace.jsonl",
        });

        Assert.False(result.IsValid);
        Assert.Contains("enable-test-control", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void SmokeParserAcceptsEveryRegisteredScenario()
    {
        var scenarios = new[]
        {
            "a1-alpha", "bad-token", "replay-nonce", "oversize-message",
            "stale-generation", "release-mismatch", "gap-resync", "reconnect",
        };

        foreach (var scenario in scenarios)
        {
            var result = SmokeClient.SmokeClientCommandLineParser.Parse(new List<string>
            {
                "--endpoint", "ws://127.0.0.1:12345",
                "--token-file", "token.bin",
                "--nonce", "nonce-1",
                "--scenario", scenario,
            });

            Assert.True(result.IsValid, $"{scenario}: {result.Error}");
            Assert.Equal(scenario, result.Options!.Scenario);
        }
    }

    [Fact]
    public void SmokeParserRejectsUnknownScenario()
    {
        var result = SmokeClient.SmokeClientCommandLineParser.Parse(new List<string>
        {
            "--endpoint", "ws://127.0.0.1:12345",
            "--token-file", "token.bin",
            "--nonce", "nonce-1",
            "--scenario", "not-registered",
        });

        Assert.False(result.IsValid);
        Assert.Contains("unknown scenario", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
