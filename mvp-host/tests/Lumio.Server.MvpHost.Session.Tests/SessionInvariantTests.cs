using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ArchUnitNET.Domain;
using ArchUnitNET.Domain.Extensions;
using ArchUnitNET.Loader;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Session;
using Lumio.Server.MvpHost.Wire;
using Xunit;

namespace Lumio.Server.MvpHost.Session.Tests;

public sealed class SessionInvariantTests
{
    private static readonly ArchUnitNET.Domain.Architecture SessionArchitecture = new ArchLoader()
        .LoadAssemblies(typeof(SessionRegistry).Assembly, typeof(MvpEnvelopeWriter).Assembly)
        .Build();

    private static readonly ServerConnectionSessionState[] AllStates =
        System.Enum.GetValues<ServerConnectionSessionState>();

    private static readonly HashSet<(ServerConnectionSessionState From, ServerConnectionSessionState To)> LegalTransitions =
    [
        (ServerConnectionSessionState.Admitted, ServerConnectionSessionState.Syncing),
        (ServerConnectionSessionState.Syncing, ServerConnectionSessionState.Active),
        (ServerConnectionSessionState.Syncing, ServerConnectionSessionState.ReconnectWindow),
        (ServerConnectionSessionState.Active, ServerConnectionSessionState.ReconnectWindow),
        (ServerConnectionSessionState.ReconnectWindow, ServerConnectionSessionState.Syncing),
        (ServerConnectionSessionState.ReconnectWindow, ServerConnectionSessionState.Expired),
    ];

    [Fact]
    public void InConnectionResyncDoesNotRehandshakeTest()
    {
        var resync = CallClosure("HandleInboundCore");
        var reconnect = CallClosure("Reconnect").Concat(CallClosure("CompleteReconnect")).ToHashSet(StringComparer.Ordinal);
        var handshakeHints = new[]
        {
            "Authenticate",
            "Authorize",
            "WriteClientHandshake",
            "WriteServerHandshake",
        };

        bool IsHandshake(string name) => handshakeHints.Any(hint => name.Contains(hint, StringComparison.Ordinal));

        var resyncHandshake = resync.Where(IsHandshake).ToHashSet(StringComparer.Ordinal);
        var reconnectHandshake = reconnect.Where(IsHandshake).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(reconnectHandshake);
        Assert.Empty(resyncHandshake.Intersect(reconnectHandshake, StringComparer.Ordinal));
    }

    [Fact]
    public void InjectDoesNotTouchWireTest()
    {
        var inject = CallClosure("InjectWorldMutation");
        Assert.DoesNotContain(
            inject,
            name => name.Contains("MvpEnvelopeWriter", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionCallsNoClientWriterTest()
    {
        var targets = AllMethodCallTargets().ToArray();
        Assert.DoesNotContain(targets, name => name.Contains("WriteResyncRequest", StringComparison.Ordinal));
        Assert.DoesNotContain(targets, name => name.Contains("WriteClientHandshake", StringComparison.Ordinal));
        Assert.DoesNotContain(targets, name => name.Contains("WriteBaselineAck", StringComparison.Ordinal));
        Assert.DoesNotContain(targets, name => name.Contains("WriteDeltaAck", StringComparison.Ordinal));
    }

    [Fact]
    public void FaultedIsModeledButUnreachableTest()
    {
        Assert.True(System.Enum.IsDefined(ServerConnectionSessionState.Faulted));
        foreach (var from in AllStates)
        {
            Assert.False(ServerConnectionSession.IsAllowed(from, ServerConnectionSessionState.Faulted));
        }

        var sessionSources = SessionSources().ToArray();
        Assert.Contains(
            sessionSources,
            source => source.Text.Contains("ABS-SESSION-FAULTED-UNREACHABLE", StringComparison.Ordinal));

        Assert.DoesNotContain(
            sessionSources,
            source => source.Text.Contains("SetState(session, ServerConnectionSessionState.Faulted)", StringComparison.Ordinal)
                || source.Text.Contains("TryTransition(ServerConnectionSessionState.Faulted)", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionStateMachineTransitionsTest()
    {
        foreach (var from in AllStates)
        {
            foreach (var to in AllStates)
            {
                if (from == to)
                {
                    continue;
                }

                var expected = IsLegalTransition(from, to);
                Assert.Equal(expected, ServerConnectionSession.IsAllowed(from, to));
            }
        }

        var session = new ServerConnectionSession(
            new ServerSessionId("session-state"),
            new SessionEpoch(0),
            "A",
            "A-1.1.0");
        Assert.True(session.TryTransition(ServerConnectionSessionState.Syncing));
        Assert.True(session.TryTransition(ServerConnectionSessionState.Active));
        Assert.False(session.TryTransition(ServerConnectionSessionState.Admitted));
        Assert.False(session.TryTransition(ServerConnectionSessionState.Faulted));
        Assert.True(session.TryTransition(ServerConnectionSessionState.Kicked));
        Assert.False(session.TryTransition(ServerConnectionSessionState.Closed));
    }

    [Fact]
    public void NoResumeTokenTest()
    {
        var names = AllNames().ToArray();
        Assert.DoesNotContain(names, name => name.Contains("ResumeToken", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains("SessionResume", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains("ReattachSession", StringComparison.Ordinal));
        Assert.DoesNotContain(
            SessionSources(),
            source => source.Text.Contains("ResumeToken", StringComparison.Ordinal)
                || source.Text.Contains("SessionResume", StringComparison.Ordinal)
                || source.Text.Contains("ReattachSession", StringComparison.Ordinal));
    }

    [Fact]
    public void MigrationsOnlyFromSessionTest()
    {
        var tryTransition = typeof(ServerConnectionSession).GetMethod(
            "TryTransition",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(tryTransition);
        Assert.True(tryTransition!.IsAssembly || tryTransition.IsPrivate);
        Assert.False(tryTransition.IsPublic);

        var setState = typeof(SessionRegistry).GetMethod(
            "SetState",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(setState);
        Assert.True(setState!.IsPrivate);

        foreach (var method in typeof(SessionRegistry).GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Concat(typeof(ServerConnectionSession).GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)))
        {
            Assert.DoesNotContain(
                method.GetParameters(),
                parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
        }
    }

    [Fact]
    public void SessionWritesNoRegistryTest()
    {
        var names = AllNames().ToArray();
        Assert.DoesNotContain(names, name => name.Contains("ConnectionRegistry", StringComparison.Ordinal));
        Assert.DoesNotContain(
            SessionSources(),
            source => source.Text.Contains("slot.Gate =", StringComparison.Ordinal)
                || source.Text.Contains(".Gate = AdmissionGateState", StringComparison.Ordinal));

        var targets = AllMethodCallTargets().ToArray();
        Assert.Contains(targets, name => name.Contains("TrySend", StringComparison.Ordinal));
        Assert.DoesNotContain(targets, name => name.Contains("ConnectionRegistry", StringComparison.Ordinal));
    }

    private static bool IsLegalTransition(ServerConnectionSessionState from, ServerConnectionSessionState to)
    {
        if (to is ServerConnectionSessionState.Closed or ServerConnectionSessionState.Kicked
            && from is not ServerConnectionSessionState.Closed
                and not ServerConnectionSessionState.Expired
                and not ServerConnectionSessionState.Kicked)
        {
            return true;
        }

        return LegalTransitions.Contains((from, to));
    }

    private static HashSet<string> CallClosure(string methodHint)
    {
        var members = SessionArchitecture.Types
            .Where(type => type.FullName is not null
                && type.FullName.StartsWith("Lumio.Server.MvpHost.Session", StringComparison.Ordinal))
            .SelectMany(type => type.Members)
            .ToArray();
        var starts = members.Where(member => member.Name.Contains(methodHint, StringComparison.Ordinal)).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<IMember>(starts);
        while (queue.Count > 0)
        {
            var member = queue.Dequeue();
            if (!seen.Add(member.FullName))
            {
                continue;
            }

            foreach (var dependency in member.GetMethodCallDependencies())
            {
                var targetName = dependency.TargetMember.FullName;
                seen.Add(targetName);
                var next = members.FirstOrDefault(candidate =>
                    string.Equals(candidate.FullName, dependency.TargetMember.FullName, StringComparison.Ordinal));
                if (next is not null)
                {
                    queue.Enqueue(next);
                }
            }
        }

        return seen;
    }

    private static IEnumerable<string> AllMethodCallTargets()
        => SessionArchitecture.Types
            .Where(type => type.FullName is not null
                && type.FullName.StartsWith("Lumio.Server.MvpHost.Session", StringComparison.Ordinal))
            .SelectMany(type => type.Members)
            .SelectMany(member => member.GetMethodCallDependencies())
            .Select(dependency => dependency.TargetMember.FullName);

    private static IEnumerable<string> AllNames()
    {
        foreach (var type in typeof(SessionRegistry).Assembly.GetTypes())
        {
            yield return type.FullName ?? type.Name;
            foreach (var member in type.GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                yield return $"{type.FullName}.{member.Name}";
            }
        }
    }

    private static IEnumerable<(string File, string Text)> SessionSources()
    {
        var root = Path.Combine(LocateMvpHost(), "src", "Lumio.Server.MvpHost.Session");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (file, File.ReadAllText(file));
        }
    }

    private static string LocateMvpHost()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "eng", "verify-all.sh")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate mvp-host root from {AppContext.BaseDirectory}");
    }
}
