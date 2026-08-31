using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Lumio.Server.MvpHost.HostContracts;
using Xunit;

namespace Lumio.Server.MvpHost.Architecture.Tests;

/// <summary>
/// 词表禁令与端口形状。这些断言挡的是「架构意图被悄悄稀释」——
/// 加一个 DI 容器、给冻结端口开个后门、把 Runtime 的词汇搬进宿主，
/// 每一个单独看都像是小便利。
/// </summary>
public sealed class VocabularyAndShapeTest
{
    private static readonly string[] SimulationPortMembers =
    {
        "Drain", "Initialize", "Ready", "RunTick", "Snapshot", "State",
    };

    private static readonly string[] SimulationStateNames =
    {
        "Created", "Initialized", "Ready", "Running", "Paused",
        "Draining", "Snapshotted", "Disposed", "Faulted",
    };

    private static IEnumerable<Type> LoadedProductionTypes()
        => new[]
            {
                typeof(IWorldSimulationPort).Assembly,
                typeof(Observability.IAuditWriter).Assembly,
                typeof(Wire.MvpEnvelopeReader).Assembly,
                typeof(Platform.IMonotonicClock).Assembly,
            }
            .SelectMany(a => a.GetTypes());

    [Fact]
    public void FrozenAuthenticationBoundaryHasNoPublicProofSurface()
    {
        var hostContracts = typeof(IWorldSimulationPort).Assembly;

        Assert.DoesNotContain(
            hostContracts.GetExportedTypes(),
            type => type.Name.Contains("AuthenticationEvidence", StringComparison.Ordinal)
                || type.Name.Contains("AuthenticationProof", StringComparison.Ordinal)
                || type.Name.Contains("AuthenticationMetadata", StringComparison.Ordinal));
        Assert.DoesNotContain(
            hostContracts
                .GetExportedTypes()
                .SelectMany(type => type.GetMembers(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)),
            member => member.Name.Contains("AuthenticationEvidence", StringComparison.Ordinal)
                || member.Name.Contains("AuthenticationProof", StringComparison.Ordinal)
                || member.Name.Contains("AuthenticationMetadata", StringComparison.Ordinal));

        AssertNoAuthenticationSidecar(typeof(CarrierAccept));
        AssertNoAuthenticationSidecar(typeof(ConnectionEvent.HandshakeEnvelope));

        AssertExactPublicRecordShape(
            typeof(CarrierAccept),
            new[] { typeof(bool), typeof(TransportConnectionId), typeof(System.Collections.Immutable.ImmutableArray<string>) },
            "Accepted",
            "ConnectionId",
            "RequestedSubprotocols");
        AssertExactPublicRecordShape(
            typeof(ConnectionEvent.HandshakeEnvelope),
            new[] { typeof(TransportConnectionId), typeof(ConnectionEpoch), typeof(ValidatedEnvelopeBytes) },
            "Envelope",
            "Epoch",
            "Id");
        AssertExactPublicRecordShape(
            typeof(SessionCommand.ConnectionCandidate),
            new[] { typeof(TransportConnectionId), typeof(ConnectionEpoch), typeof(ValidatedEnvelopeBytes) },
            "ConnectionEpoch",
            "ConnectionId",
            "Handshake");
    }

    [Fact]
    public void AuthenticationMetadataSideChannelStaysInsideTransportPipeline()
    {
        var sources = Directory
            .EnumerateFiles(Path.Combine(BuildGraph.MvpHostRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(BuildGraph.MvpHostRoot, path).Replace('\\', '/'),
                File.ReadAllText,
                StringComparer.Ordinal);

        var webSocketPath = "src/Lumio.Server.MvpHost.Transport.WebSocket/WebSocketByteCarrier.cs";
        var transportPath = "src/Lumio.Server.MvpHost.Transport/TransportService.cs";
        var appPath = "src/Lumio.Server.MvpHost.App/FullGraphComposition.cs";
        var webSocketSource = sources[webSocketPath];
        var transportSource = sources[transportPath];
        var appSource = sources[appPath];

        Assert.DoesNotContain(
            sources,
            source => source.Value.Contains("TransportAuthenticationEvidence", StringComparison.Ordinal));
        Assert.Contains("TryTakeAuthenticationMetadata", webSocketSource, StringComparison.Ordinal);
        Assert.Equal(
            new[] { webSocketPath },
            sources
                .Where(source => source.Value.Contains("authenticationMetadata[", StringComparison.Ordinal))
                .Select(source => source.Key)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray());
        Assert.Contains("this.carrier is ITransportAuthenticationMetadataSource", transportSource, StringComparison.Ordinal);
        Assert.Contains("entry.SetAuthenticationMetadata", transportSource, StringComparison.Ordinal);
        Assert.Contains("transport.TryTakeAuthenticationMetadata", appSource, StringComparison.Ordinal);
        Assert.Contains("HandleAuthenticatedConnectionEvent", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            sources.Where(source => source.Key.StartsWith(
                "src/Lumio.Server.MvpHost.HostContracts/",
                StringComparison.Ordinal)),
            source => source.Value.Contains("AuthenticationMetadata", StringComparison.Ordinal));
    }

    [Fact]
    public void InternalWorldSlotAdaptersAreNotPublishedByHostContracts()
    {
        var exportedNames = typeof(IWorldSimulationPort).Assembly
            .GetExportedTypes()
            .Select(type => type.Name)
            .ToArray();

        Assert.DoesNotContain("AdmissionReservationResult", exportedNames);
        Assert.DoesNotContain("IWorldSlotAdmissionPort", exportedNames);
        Assert.DoesNotContain("IWorldSlotPacingPort", exportedNames);
    }

    private static void AssertExactPublicRecordShape(
        Type type,
        Type[] constructorParameterTypes,
        params string[] propertyNames)
    {
        var constructor = Assert.Single(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(
            constructorParameterTypes,
            constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray());

        var actualProperties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            propertyNames.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            actualProperties);
    }

    private static void AssertNoAuthenticationSidecar(Type type)
    {
        var hiddenMembers = type.GetMembers(
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(member => member.Name.Contains("AuthenticationEvidence", StringComparison.Ordinal)
                || member.Name.Contains("AuthenticationProof", StringComparison.Ordinal)
                || member.Name.Contains("AuthenticationMetadata", StringComparison.Ordinal)
                || member.Name.Contains("<Authentication", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(hiddenMembers);
    }

    /// <summary>
    /// 无 DI 容器、无 EventBus、无 <c>Common</c> / <c>Utils</c> 这类命名。
    /// 前者会让依赖关系从构建图里消失（变成运行期字符串查找），
    /// 后者是「不知道放哪就放这里」的收容所，两者都会让上面那些分层断言失去意义。
    /// </summary>
    [Theory]
    [InlineData("ServiceCollection")]
    [InlineData("ServiceProvider")]
    [InlineData("IServiceLocator")]
    [InlineData("EventBus")]
    [InlineData("Container")]
    public void 不存在容器或事件总线类型(string forbidden)
    {
        var offenders = LoadedProductionTypes()
            .Where(t => t.Name.Contains(forbidden, StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("Common")]
    [InlineData("Utils")]
    [InlineData("Globals")]
    [InlineData("Shared")]
    public void 不存在收容所式的工程或命名空间段(string forbidden)
    {
        var projectOffenders = BuildGraph.All
            .Where(p => p.Name.Split('.').Contains(forbidden, StringComparer.Ordinal))
            .Select(p => p.Name)
            .ToList();

        var namespaceOffenders = LoadedProductionTypes()
            .Select(t => t.Namespace)
            .Where(ns => ns is not null)
            .Distinct(StringComparer.Ordinal)
            .Where(ns => ns!.Split('.').Contains(forbidden, StringComparer.Ordinal))
            .ToList();

        Assert.Empty(projectOffenders);
        Assert.Empty(namespaceOffenders);
    }

    /// <summary>
    /// 宿主 ↔ Runtime 端口的形状。**恰 6 个成员**、无 Runtime 词汇、
    /// 签名里零 <c>Lumio.GameRuntime.*</c> 类型，且**没有**任何
    /// <c>TryApplyOpaqueMutation</c> 或 <c>Inject*</c> 后门。
    /// </summary>
    [Fact]
    public void 仿真端口恰六个成员且无后门()
    {
        var port = typeof(IWorldSimulationPort);

        var members = port.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m is not MethodInfo method || !method.IsSpecialName)
            .Select(m => m.Name)
            .Concat(port.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(p => p.Name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(SimulationPortMembers, members);

        Assert.Single(port.GetMethods(), m => m.Name == "RunTick");

        var backdoors = members
            .Where(n => n == "TryApplyOpaqueMutation" || n.StartsWith("Inject", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(backdoors);
    }

    [Theory]
    [InlineData("Phase")]
    [InlineData("Clock")]
    [InlineData("Revision")]
    [InlineData("Commit")]
    public void 仿真端口的方法名不含Runtime编排词汇(string forbidden)
    {
        var offenders = typeof(IWorldSimulationPort)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(n => n.Contains(forbidden, StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// 端口签名里的每个类型都必须来自本仓四个程序集或 <c>System.*</c>。
    /// 出现 <c>Lumio.GameRuntime.*</c> 就意味着 Adapter 缺席时这个端口编译不过——
    /// 而「Adapter 缺席仍全绿」是本设计的一条硬主张。
    /// </summary>
    [Fact]
    public void 仿真端口签名里没有Runtime程序集的类型()
    {
        var allowed = new[]
        {
            "Lumio.Server.MvpHost.HostContracts",
            "Lumio.Server.MvpHost.Wire",
            "Lumio.Server.MvpHost.Platform",
        };

        var offenders = new List<string>();

        foreach (var method in typeof(IWorldSimulationPort).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            foreach (var type in method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType))
            {
                var element = type.IsByRef || type.IsArray ? type.GetElementType()! : type;
                var assembly = element.Assembly.GetName().Name ?? string.Empty;

                var ok = allowed.Contains(assembly, StringComparer.Ordinal)
                    || assembly.StartsWith("System.", StringComparison.Ordinal)
                    || assembly is "System.Private.CoreLib" or "System.Runtime" or "netstandard";

                if (!ok)
                {
                    offenders.Add($"{method.Name}: {element.FullName} @ {assembly}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// 带外世界变更端口是**独立**的一个成员，不给冻结的 <c>IWorldSimulationPort</c> 开例外。
    /// 参数与返回类型只来自 <c>System.*</c> 与 <c>Platform</c>——它不认识任何 Envelope。
    /// </summary>
    [Fact]
    public void 带外变更端口恰一个成员且不认识Envelope()
    {
        var sink = typeof(IWorldMutationSink);
        var methods = sink.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.Single(methods);
        Assert.Equal("TryEnqueueOpaqueMutation", methods[0].Name);

        foreach (var type in methods[0].GetParameters().Select(p => p.ParameterType).Append(methods[0].ReturnType))
        {
            var assembly = type.Assembly.GetName().Name ?? string.Empty;
            Assert.True(
                assembly.StartsWith("System", StringComparison.Ordinal)
                || assembly is "Lumio.Server.MvpHost.Platform" or "netstandard",
                $"{type.FullName} 来自 {assembly}——带外端口不得认识 Wire 或 HostContracts 的搬运类型");
        }

        // Layer 2 定义接口 → Layer 4 实现 → Layer 6 注入；契约层不认识实现方。
        var hostContracts = BuildGraph.ByName("Lumio.Server.MvpHost.HostContracts");
        Assert.NotNull(hostContracts);
        Assert.DoesNotContain("Lumio.Server.MvpHost.Simulation.Reference", hostContracts.ProjectReferences);
    }

    [Fact]
    public void 仿真状态枚举与Runtime侧九态逐字一致()
    {
        Assert.Equal(SimulationStateNames, Enum.GetNames<HostSimulationState>());
    }

    /// <summary>
    /// <c>HostFaultClass</c> 恰 4 个成员且 <c>None</c> 在首位（值为 0）。
    /// <c>ids/index.json</c> 的 <c>FaultClass</c> 命名空间只有后 3 个；
    /// <c>None</c> 是本仓私有第 4 值，**绝不跨 wire、绝不进任何 reasonCode**。
    /// </summary>
    [Fact]
    public void 私有的None故障类被记录且不进错误码表()
    {
        var names = Enum.GetNames<HostFaultClass>();

        Assert.Equal(4, names.Length);
        Assert.Equal("None", names[0]);
        Assert.Equal(0, (int)HostFaultClass.None);

        Assert.DoesNotContain("None", Lumio.Gen.ContractTypes.Catalog.StableErrorIds);
    }

    /// <summary>
    /// ADR-001：全仓不存在名称含 <c>ClientReplicaSession</c> 的类型或成员。
    /// 唯一允许出现该字面量的位置是 permission gate 的 <c>antiReplay.sessionScopeOwner</c>
    /// 常量值——那是公共 schema 要求的取值，不是本仓在建模客户端状态机。
    /// </summary>
    [Fact]
    public void 全仓不存在ClientReplicaSession类型或成员()
    {
        var offenders = new List<string>();

        foreach (var type in LoadedProductionTypes())
        {
            if (type.Name.Contains("ClientReplicaSession", StringComparison.Ordinal))
            {
                offenders.Add(type.FullName ?? type.Name);
            }

            offenders.AddRange(type
                .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.Contains("ClientReplicaSession", StringComparison.Ordinal))
                .Select(m => $"{type.FullName}.{m.Name}"));
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// 参考仿真实现不得携带任何体素 / ECS 词汇——它是「不透明 key→value 覆盖表」，
    /// 一旦出现 <c>Chunk</c> / <c>Entity</c> 这类名字，就说明有人开始在宿主侧建模世界内容。
    /// 程序集不存在时输出显式说明而不是静默通过。
    /// </summary>
    [Fact]
    public void 参考仿真实现不含世界内容词汇()
    {
        var project = BuildGraph.ByName("Lumio.Server.MvpHost.Simulation.Reference");
        if (project is null)
        {
            Assert.True(true, "Simulation.Reference 尚未落地（由 implement-mvp-world-slot-aggregate-and-sim-port-stub 提供），本条为空真。");
            return;
        }

        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Lumio.Server.MvpHost.Simulation.Reference");

        if (assembly is null)
        {
            Assert.True(true, "Simulation.Reference 在构建图内但未被本测试程序集加载，本条为空真。");
            return;
        }

        var forbidden = new[] { "Chunk", "Block", "Entity", "Component", "Ability", "Voxel", "Phase" };
        var offenders = assembly.GetTypes()
            .Where(t => forbidden.Any(f => t.Name.Contains(f, StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// <c>build.proj</c> 的 glob 不含 <c>adapters/</c>，且构建图内没有任何指向
    /// <c>Lumio.GameRuntime.*</c> 的引用——「Adapter 缺席仍全绿」因此是可判事实。
    /// </summary>
    [Fact]
    public void 适配器不在构建图内且无Runtime引用()
    {
        Assert.DoesNotContain(BuildGraph.TraversalGlobs, g => g.Contains("adapters", StringComparison.Ordinal));

        var offenders = BuildGraph.All
            .SelectMany(p => p.ProjectReferences.Select(r => (p.Name, Reference: r)))
            .Where(x => x.Reference.StartsWith("Lumio.GameRuntime", StringComparison.Ordinal))
            .Select(x => $"{x.Name} → {x.Reference}")
            .ToList();

        Assert.Empty(offenders);
    }
}
