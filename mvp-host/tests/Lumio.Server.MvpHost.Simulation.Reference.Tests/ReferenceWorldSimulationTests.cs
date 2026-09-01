using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using ArchUnitNET.Domain.Extensions;
using ArchUnitNET.Loader;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Platform;
using Xunit;

namespace Lumio.Server.MvpHost.Simulation.Reference.Tests;

public sealed class ReferenceWorldSimulationTests
{
    private static readonly ArchUnitNET.Domain.Architecture SimulationArchitecture = new ArchLoader()
        .LoadAssemblies(typeof(ReferenceWorldSimulation).Assembly)
        .Build();

    private static readonly string[] ExpectedProjectReferences =
        { "Lumio.Server.MvpHost.HostContracts" };

    private static readonly string[] QueueContractFields =
        { "owner", "producer", "consumer", "ordering", "budget", "onFull", "onClose" };

    private static readonly string[] ForbiddenVocabulary =
        { "Chunk", "Block", "Entity", "Component", "Ability", "Voxel", "Phase", "Tick" };

    [Fact]
    public void MutationSinkOnlyEnqueuesAndOwnerAppliesAtNextRunStart()
    {
        using var simulation = ReadySimulation(seed: 17);
        var command = Encode("alpha", "one");
        EnqueueResult result = default;

        var producer = new Thread(() => result = simulation.TryEnqueueOpaqueMutation(command));
        producer.Start();
        producer.Join();

        Assert.Equal(EnqueueStatus.Accepted, result.Status);
        Assert.Equal(0UL, simulation.AuthorityRevision);
        Assert.Equal(1, simulation.MutationInboxCount);

        var outcome = Run(simulation, logicalCall: 1, seed: 31);

        Assert.Equal(HostTickStatus.Completed, outcome.Status);
        Assert.Equal(HostFaultClass.None, outcome.FaultClass);
        Assert.Equal(1UL, outcome.AuthorityRevision);
        Assert.Equal(0, simulation.MutationInboxCount);
    }

    [Fact]
    public void AcceptedMutationOwnsItsBytes()
    {
        using var simulation = ReadySimulation();
        var command = Encode("alpha", "one");

        Assert.Equal(EnqueueStatus.Accepted, simulation.TryEnqueueOpaqueMutation(command).Status);
        Array.Fill(command, (byte)0xff);

        var first = Run(simulation, 1);
        var snapshot = TakeSnapshot(simulation);

        using var comparison = ReadySimulation();
        Assert.Equal(EnqueueStatus.Accepted, comparison.TryEnqueueOpaqueMutation(Encode("alpha", "one")).Status);
        var expected = Run(comparison, 1);
        var expectedSnapshot = TakeSnapshot(comparison);

        Assert.Equal(expected.StateHash.ToArray(), first.StateHash.ToArray());
        Assert.Equal(expectedSnapshot, snapshot);
    }

    [Fact]
    public void InboxIsBoundedAtThirtyTwoAndPreservesAcceptedFifoOrder()
    {
        using var simulation = ReadySimulation();
        var accepted = new List<byte[]>();

        for (var index = 0; index < ReferenceWorldSimulation.MutationInboxCapacity; index++)
        {
            var mutation = Encode("same-key", $"value-{index:D2}");
            accepted.Add(mutation);
            Assert.Equal(EnqueueStatus.Accepted, simulation.TryEnqueueOpaqueMutation(mutation).Status);
        }

        var rejected = simulation.TryEnqueueOpaqueMutation(Encode("same-key", "not-applied"));
        Assert.Equal(EnqueueStatus.Full, rejected.Status);
        Assert.Equal("QueueFull", rejected.StableErrorId);
        Assert.Equal(32, simulation.MutationInboxBudget.MaxItems);

        var outcome = Run(simulation, 1);

        Assert.Equal(32UL, outcome.AuthorityRevision);
        var snapshot = TakeSnapshot(simulation);

        using var expected = ReadySimulation();
        foreach (var mutation in accepted)
        {
            Assert.Equal(EnqueueStatus.Accepted, expected.TryEnqueueOpaqueMutation(mutation).Status);
        }

        _ = Run(expected, 1);
        Assert.Equal(TakeSnapshot(expected), snapshot);
    }

    [Fact]
    public void OverwriteOnlyAdvancesRevisionWhenStoredBytesChange()
    {
        using var simulation = ReadySimulation();

        Assert.Equal(EnqueueStatus.Accepted, simulation.TryEnqueueOpaqueMutation(Encode("k", "v1")).Status);
        Assert.Equal(1UL, Run(simulation, 1).AuthorityRevision);

        Assert.Equal(EnqueueStatus.Accepted, simulation.TryEnqueueOpaqueMutation(Encode("k", "v1")).Status);
        Assert.Equal(1UL, Run(simulation, 2).AuthorityRevision);

        Assert.Equal(EnqueueStatus.Accepted, simulation.TryEnqueueOpaqueMutation(Encode("k", "v2")).Status);
        Assert.Equal(2UL, Run(simulation, 3).AuthorityRevision);

        Assert.Equal(2UL, Run(simulation, 4).AuthorityRevision);
    }

    [Fact]
    public void EqualInputsProduceEqualHashesAndSnapshotsWithoutWallClock()
    {
        using var left = ReadySimulation(seed: 101, configuration: new byte[] { 4, 5, 6 });
        using var right = ReadySimulation(seed: 101, configuration: new byte[] { 4, 5, 6 });

        Assert.Equal(EnqueueStatus.Accepted, left.TryEnqueueOpaqueMutation(Encode("b", "2")).Status);
        Assert.Equal(EnqueueStatus.Accepted, left.TryEnqueueOpaqueMutation(Encode("a", "1")).Status);
        Assert.Equal(EnqueueStatus.Accepted, right.TryEnqueueOpaqueMutation(Encode("b", "2")).Status);
        Assert.Equal(EnqueueStatus.Accepted, right.TryEnqueueOpaqueMutation(Encode("a", "1")).Status);

        var leftOutcome = Run(left, 9, seed: 202);
        Thread.Yield();
        var rightOutcome = Run(right, 9, seed: 202);

        Assert.Equal(leftOutcome.StateHash.ToArray(), rightOutcome.StateHash.ToArray());
        Assert.Equal(TakeSnapshot(left), TakeSnapshot(right));
    }

    [Fact]
    public void LifecycleAcceptsOnlyTheFrozenForwardPath()
    {
        using var simulation = ReferenceWorldSimulation.Create(7);
        Assert.Equal(HostSimulationState.Created, simulation.State);

        AssertRejected(simulation.Ready(), HostSimulationState.Created, "WrongContext");
        AssertRejected(simulation.Drain(), HostSimulationState.Created, "WrongContext");
        AssertRejected(simulation.Snapshot(out var beforeInitialize), HostSimulationState.Created, "WrongContext");
        Assert.True(beforeInitialize.IsEmpty);
        Assert.Equal(HostTickStatus.Rejected, Run(simulation, 1).Status);

        var invalid = new HostSessionInit(new HostSessionId(string.Empty), new HostWorldSlotId(1), ReadOnlyMemory<byte>.Empty, 1);
        AssertRejected(simulation.Initialize(in invalid), HostSimulationState.Created, "InvalidArgument");

        var init = ValidInit();
        AssertAccepted(simulation.Initialize(in init), HostSimulationState.Initialized);
        AssertRejected(simulation.Initialize(in init), HostSimulationState.Initialized, "WrongContext");
        AssertAccepted(simulation.Ready(), HostSimulationState.Ready);
        AssertRejected(simulation.Ready(), HostSimulationState.Ready, "WrongContext");

        Assert.Equal(HostTickStatus.Completed, Run(simulation, 2).Status);
        Assert.Equal(HostSimulationState.Running, simulation.State);
        AssertAccepted(simulation.Drain(), HostSimulationState.Draining);
        AssertRejected(simulation.Drain(), HostSimulationState.Draining, "WrongContext");

        var enqueue = simulation.TryEnqueueOpaqueMutation(Encode("k", "v"));
        Assert.Equal(EnqueueStatus.Closed, enqueue.Status);
        Assert.Equal("ContextClosing", enqueue.StableErrorId);
        Assert.Equal(HostTickStatus.Rejected, Run(simulation, 3).Status);

        AssertAccepted(simulation.Snapshot(out var snapshot), HostSimulationState.Snapshotted);
        Assert.False(snapshot.IsEmpty);
        AssertRejected(simulation.Snapshot(out var duplicateSnapshot), HostSimulationState.Snapshotted, "WrongContext");
        Assert.True(duplicateSnapshot.IsEmpty);

        simulation.Dispose();
        Assert.Equal(HostSimulationState.Disposed, simulation.State);
        AssertRejected(simulation.Ready(), HostSimulationState.Disposed, "ContextDestroyed");
        AssertRejected(simulation.Drain(), HostSimulationState.Disposed, "ContextDestroyed");
        Assert.Equal(HostTickStatus.Rejected, Run(simulation, 4).Status);
        Assert.Equal("ContextDestroyed", Run(simulation, 4).StableErrorId);

        var afterDispose = simulation.TryEnqueueOpaqueMutation(Encode("k", "v"));
        Assert.Equal(EnqueueStatus.Closed, afterDispose.Status);
        Assert.Equal("ContextDestroyed", afterDispose.StableErrorId);
        simulation.Dispose();
    }

    [Fact]
    public void QueueRegistryAndProjectEdgeMatchProductionContract()
    {
        var directory = LocateProjectDirectory();
        var project = System.Xml.Linq.XDocument.Load(
            Path.Combine(directory, "Lumio.Server.MvpHost.Simulation.Reference.csproj"));
        var references = project.Descendants("ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension(element.Attribute("Include")!.Value))
            .ToArray();

        Assert.Equal(ExpectedProjectReferences, references);

        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "queues.json")))!;
        var entry = ((JsonArray)document["queues"]!).OfType<JsonObject>().Single();
        Assert.Equal("MvpWorldMutationInbox", entry["name"]!.GetValue<string>());
        Assert.Equal("Lumio.Server.MvpHost.Simulation.Reference", entry["owner"]!.GetValue<string>());
        Assert.Contains("32", entry["budget"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("provisional", entry["budget"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("QueueFull", entry["onFull"]!.GetValue<string>(), StringComparison.Ordinal);

        foreach (var field in QueueContractFields)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry[field]!.GetValue<string>()));
        }
    }

    [Fact]
    public void SimulationVocabularyIsEmptyTest()
    {
        var assembly = typeof(ReferenceWorldSimulation).Assembly;
        Assert.Equal("Lumio.Server.MvpHost.Simulation.Reference", assembly.GetName().Name);

        var offenders = assembly.GetTypes()
            .Where(type => ForbiddenVocabulary.Any(token =>
                type.Name.Contains(token, StringComparison.Ordinal)))
            .Select(type => type.FullName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void WorldMutationSinkIsOutOfBandTest()
    {
        var referenced = typeof(ReferenceWorldSimulation).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToArray();
        Assert.DoesNotContain("Lumio.Server.MvpHost.Wire", referenced);
        Assert.DoesNotContain("Lumio.Server.MvpHost.Transport", referenced);

        var envelopeNames = new[] { "MvpEnvelopeWriter", "MvpEnvelopeReader" };
        var named = SimulationArchitecture.Types
            .SelectMany(type => type.Members.Select(member => member.FullName).Prepend(type.FullName))
            .Where(name => name is not null && envelopeNames.Any(envelope =>
                name.Contains(envelope, StringComparison.Ordinal)))
            .ToList();
        Assert.Empty(named);

        var calls = SimulationArchitecture.Types
            .SelectMany(type => type.Members)
            .SelectMany(member => member.GetMethodCallDependencies())
            .Where(dependency => envelopeNames.Any(envelope =>
                (dependency.Target.FullName ?? string.Empty).Contains(envelope, StringComparison.Ordinal)
                || (dependency.TargetMember.FullName ?? string.Empty).Contains(envelope, StringComparison.Ordinal)))
            .Select(dependency => dependency.TargetMember.FullName)
            .ToList();
        Assert.Empty(calls);

        var typeDependencies = SimulationArchitecture.Types
            .SelectMany(type => type.Dependencies)
            .Where(dependency => envelopeNames.Any(envelope =>
                (dependency.Target.FullName ?? string.Empty).Contains(envelope, StringComparison.Ordinal)))
            .Select(dependency => dependency.Target.FullName)
            .ToList();
        Assert.Empty(typeDependencies);
    }

    private static ReferenceWorldSimulation ReadySimulation(
        ulong seed = 11,
        ReadOnlyMemory<byte> configuration = default)
    {
        var simulation = ReferenceWorldSimulation.Create(seed);
        var init = ValidInit(configuration, seed);
        AssertAccepted(simulation.Initialize(in init), HostSimulationState.Initialized);
        AssertAccepted(simulation.Ready(), HostSimulationState.Ready);
        return simulation;
    }

    private static HostSessionInit ValidInit(ReadOnlyMemory<byte> configuration = default, ulong seed = 11)
        => new(new HostSessionId("session"), new HostWorldSlotId(7), configuration, seed);

    private static HostTickOutcome Run(ReferenceWorldSimulation simulation, ulong logicalCall, ulong seed = 19)
    {
        var request = new HostTickRequest(
            new LogicalTickToken(logicalCall),
            ReadOnlyMemory<WireFrame>.Empty,
            seed);
        return simulation.RunTick(in request);
    }

    private static byte[] TakeSnapshot(ReferenceWorldSimulation simulation)
    {
        AssertAccepted(simulation.Drain(), HostSimulationState.Draining);
        AssertAccepted(simulation.Snapshot(out var snapshot), HostSimulationState.Snapshotted);
        return snapshot.ToArray();
    }

    private static byte[] Encode(string key, string value)
    {
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        var valueBytes = System.Text.Encoding.UTF8.GetBytes(value);
        var result = new byte[8 + keyBytes.Length + valueBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)keyBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)valueBytes.Length);
        keyBytes.CopyTo(result.AsSpan(8));
        valueBytes.CopyTo(result.AsSpan(8 + keyBytes.Length));
        return result;
    }

    private static void AssertAccepted(HostLifecycleResult result, HostSimulationState state)
    {
        Assert.True(result.Accepted);
        Assert.Equal(state, result.State);
        Assert.Null(result.StableErrorId);
    }

    private static void AssertRejected(
        HostLifecycleResult result,
        HostSimulationState state,
        string stableErrorId)
    {
        Assert.False(result.Accepted);
        Assert.Equal(state, result.State);
        Assert.Equal(stableErrorId, result.StableErrorId);
    }

    private static string LocateProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "Lumio.Server.MvpHost.Simulation.Reference");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the Simulation.Reference project directory.");
    }
}
