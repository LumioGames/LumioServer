using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Lumio.Server.MvpHost.Wire.Tests
{
    /// <summary>
    /// 出站 writer 的验收。核心一条是 **exact-set**：每个 writer 写出的 body
    /// key 集合恰好等于镜像 schema 该 messageType 分支的 required，不多不少。
    ///
    /// ADR-028 的 Alternatives 明文否决 free-form payload——
    /// 「two implementations can pass the gate and disagree on Snapshot identity」。
    /// 公共 required 判定「只查缺失不查多余」是机器门的能力边界，不是设计许可；
    /// ADR-045 之后 <c>additionalProperties: false</c> 把多余成员也变成公共规则，
    /// 本仓出站仍保留 exact-set 自检，因为它测的是「本仓有没有想往里加东西」。
    /// </summary>
    public sealed class OutboundWriterTest
    {
        private static EnvelopeWriteContext Context() => new(
            SessionId: "session-001",
            ProductId: "A",
            GameReleaseId: "A-1.1.0",
            Sequence: 100,
            TraceId: "trace-outbound-100",
            Reliability: MvpWireConstants.Reliability,
            MaxMessageBytes: 65536,
            MaxFragmentBytes: 4096,
            AntiReplayWindow: 1024,
            AuthBinding: "SessionAdmission",
            ErrorClass: "Rejectable");

        public static TheoryData<string, ReadOnlyMemory<byte>> AllWriters()
        {
            var ctx = Context();
            return new TheoryData<string, ReadOnlyMemory<byte>>
            {
                { "Handshake", MvpEnvelopeWriter.WriteServerHandshake(ctx) },
                { "FullSnapshot", MvpEnvelopeWriter.WriteFullSnapshot(ctx, "snapshot-100", 42, 18) },
                { "Delta", MvpEnvelopeWriter.WriteDelta(ctx, "snapshot-100", 12, 13, 7) },
                { "MaintenanceKick", MvpEnvelopeWriter.WriteMaintenanceKick(ctx, "MaintenanceKick") },
                { "Error", MvpEnvelopeWriter.WriteError(ctx, "Rejectable", "BudgetExceeded") },
                { "Handshake", MvpEnvelopeWriter.WriteClientHandshake(ctx) },
                { "BaselineAck", MvpEnvelopeWriter.WriteBaselineAck(ctx, "snapshot-100", 18) },
                { "DeltaAck", MvpEnvelopeWriter.WriteDeltaAck(ctx, 7, 13) },
                { "ResyncRequest", MvpEnvelopeWriter.WriteResyncRequest(ctx, "GapDetected") },
            };
        }

        [Theory]
        [MemberData(nameof(AllWriters))]
        public void 每个writer写出的信封都能通过本工程自己的双层校验(string messageType, ReadOnlyMemory<byte> bytes)
        {
            var result = MvpEnvelopeReader.Validate(bytes.Span);

            Assert.Equal(EnvelopeParseStatus.Ok, result.Status);

            MvpEnvelopeReader.TryReadHeader(bytes.Span, out var header);
            Assert.Equal(messageType, header.MessageType);
        }

        [Theory]
        [MemberData(nameof(AllWriters))]
        public void 出站body的成员集恰好等于镜像schema的required(string messageType, ReadOnlyMemory<byte> bytes)
        {
            var body = (JsonObject)JsonNode.Parse(bytes.ToArray())!["body"]!;
            var actual = body.Select(p => p.Key).OrderBy(x => x, StringComparer.Ordinal);

            // 期望值从镜像 schema 运行期读出，不是本仓手抄的常量表。
            var expected = EnvelopeSemantics.BodyRequiredOf(messageType).OrderBy(x => x, StringComparer.Ordinal);

            Assert.Equal(expected, actual);
        }

        /// <summary>
        /// 本工程内不存在任何私有 body 字段名常量。源码扫描而不是反射——
        /// 反射看不到方法体里的字符串字面量，而「偷偷加一个字段」恰恰发生在方法体里。
        /// </summary>
        [Fact]
        public void 源码里不存在以mvp开头的body字段名字面量()
        {
            var sourceDir = Path.Combine(MirrorFixtures.MvpHostRoot, "src", "Lumio.Server.MvpHost.Wire");
            var offenders = new List<string>();

            foreach (var file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                foreach (Match match in Regex.Matches(text, "\"(mvp[A-Za-z0-9_]*)\"", RegexOptions.None, TimeSpan.FromSeconds(5)))
                {
                    offenders.Add($"{Path.GetFileName(file)}: \"{match.Groups[1].Value}\"");
                }
            }

            Assert.Empty(offenders);
        }

        /// <summary>
        /// <c>WriteFullSnapshot</c> 忽略 ctx 的 reliability、恒写 <c>Reliable</c>：
        /// 公共硬约束，调用方传什么都不能把它写成 Unreliable。
        /// </summary>
        [Fact]
        public void FullSnapshot忽略上下文的reliability恒写Reliable()
        {
            var ctx = Context() with { Reliability = "Unreliable" };

            var bytes = MvpEnvelopeWriter.WriteFullSnapshot(ctx, "snapshot-100", 42, 18);
            var node = JsonNode.Parse(bytes.ToArray())!;

            Assert.Equal("Reliable", node["reliability"]!.GetValue<string>());
            Assert.Equal(EnvelopeParseStatus.Ok, MvpEnvelopeReader.Validate(bytes.Span).Status);
        }

        /// <summary>
        /// <c>WriteDelta</c> 不收 tombstones 入参、恒写空数组：MVP 参考存根的世界模型是
        /// 不透明 key→value 覆盖表，没有实体生命周期概念，因此没有墓碑可发。
        /// 字段仍必须在场（它是 Delta 的必填之一）。
        /// </summary>
        [Fact]
        public void Delta的tombstones恒为空数组且字段在场()
        {
            var bytes = MvpEnvelopeWriter.WriteDelta(Context(), "snapshot-100", 12, 13, 7);
            var body = (JsonObject)JsonNode.Parse(bytes.ToArray())!["body"]!;

            Assert.True(body.ContainsKey("tombstones"));
            Assert.Empty((JsonArray)body["tombstones"]!);
        }

        [Theory]
        [MemberData(nameof(AllWriters))]
        public void 出站integrity恒为None与none(string messageType, ReadOnlyMemory<byte> bytes)
        {
            _ = messageType;
            var integrity = (JsonObject)JsonNode.Parse(bytes.ToArray())!["integrity"]!;

            Assert.Equal("None", integrity["algorithm"]!.GetValue<string>());
            Assert.Equal("none", integrity["value"]!.GetValue<string>());
        }

        /// <summary>
        /// <c>sessionRevisionVector</c> 的 7 字段按单一 authorityRevision 机械填充，
        /// chunk key 是 canonical 形态，mappingSetHash 恒为单点常量，且整条能过自家语义层。
        /// </summary>
        [Fact]
        public void 快照的修订向量被机械填充且能过自家语义层()
        {
            var bytes = MvpEnvelopeWriter.WriteFullSnapshot(Context(), "snapshot-100", 42, 18);
            var body = (JsonObject)JsonNode.Parse(bytes.ToArray())!["body"]!;
            var vector = (JsonObject)body["sessionRevisionVector"]!;

            foreach (var field in new[]
                     {
                         "tickId", "gameRevision", "voxelWorldRevision", "chunkRevisionSet",
                         "replicationRevision", "configRevision", "schemaEpoch",
                     })
            {
                Assert.True(vector.ContainsKey(field), $"缺字段 {field}");
            }

            Assert.Equal(42UL, vector["tickId"]!.GetValue<ulong>());
            Assert.Equal(18UL, vector["gameRevision"]!.GetValue<ulong>());
            Assert.Equal(18UL, vector["voxelWorldRevision"]!.GetValue<ulong>());
            Assert.Equal(18UL, vector["replicationRevision"]!.GetValue<ulong>());
            Assert.Equal(0UL, vector["configRevision"]!.GetValue<ulong>());
            Assert.Equal(
                GeneratedContracts.GeneratedContractManifest.SchemaEpoch,
                vector["schemaEpoch"]!.GetValue<int>());

            var chunkKeys = ((JsonObject)vector["chunkRevisionSet"]!).Select(p => p.Key).ToList();
            Assert.Single(chunkKeys);
            Assert.Matches("^c:(0|-?[1-9][0-9]{0,9}):(0|-?[1-9][0-9]{0,9}):(0|-?[1-9][0-9]{0,9})$", chunkKeys[0]);

            Assert.Equal(MvpWireConstants.MappingSetHash, body["mappingSetHash"]!.GetValue<string>());
            Assert.Equal(EnvelopeParseStatus.Ok, MvpEnvelopeReader.Validate(bytes.Span).Status);
        }

        /// <summary>
        /// 全工程只有一个 <c>mappingSetHash</c> 取值来源。多一个来源就多一条独立漂移的路径。
        /// </summary>
        [Fact]
        public void 全工程只有一个mappingSetHash取值来源()
        {
            var sourceDir = Path.Combine(MirrorFixtures.MvpHostRoot, "src", "Lumio.Server.MvpHost.Wire");
            var literals = new List<string>();

            foreach (var file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
            {
                foreach (Match match in Regex.Matches(
                             File.ReadAllText(file), "\"([0-9a-f]{64})\"", RegexOptions.None, TimeSpan.FromSeconds(5)))
                {
                    literals.Add($"{Path.GetFileName(file)}: {match.Groups[1].Value}");
                }
            }

            Assert.Single(literals);
            Assert.Contains(MvpWireConstants.MappingSetHash, literals[0], StringComparison.Ordinal);
        }

        /// <summary>
        /// 出站 <c>length</c> 是**声明上界**（等于本信封的 maxMessageBytes），
        /// **不是** body 或任何东西的字节数。ADR-045 §3 的 Alternatives 专门否决了
        /// 「CanonicalJsonV1 byte count」——那会让文本编码由副作用变成规范 wire 形态，
        /// 抢先决定尚未冻结的 §4。
        /// </summary>
        [Fact]
        public void 出站length是声明上界而不是字节数()
        {
            var ctx = Context();
            var bytes = MvpEnvelopeWriter.WriteFullSnapshot(ctx, "snapshot-100", 42, 18);
            var node = JsonNode.Parse(bytes.ToArray())!;

            var declared = node["length"]!.GetValue<long>();
            var bodyByteCount = System.Text.Encoding.UTF8.GetByteCount(node["body"]!.ToJsonString());

            Assert.Equal(ctx.MaxMessageBytes, declared);
            Assert.NotEqual(bodyByteCount, declared);
            Assert.True(declared <= ctx.MaxMessageBytes, "length 不得超过本信封的 maxMessageBytes");
        }
    }
}
