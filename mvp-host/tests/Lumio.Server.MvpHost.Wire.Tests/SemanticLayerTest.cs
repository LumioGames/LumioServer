using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Lumio.Server.MvpHost.GeneratedContracts;

namespace Lumio.Server.MvpHost.Wire.Tests
{
    /// <summary>
    /// 语义层单测：schema 表达不了、必须另写的那些约束，每条用**自造用例**验证，
    /// 不新增任何 fixture（fixture 归架构仓）。
    /// </summary>
    public sealed class SemanticLayerTest
    {
        /// <summary>
        /// <c>FullSnapshot ⟹ Reliable</c>。语义层必须**独立**覆盖这条：
        /// 镜像里虽有 <c>replication-unreliable-full-snapshot</c> 反例，
        /// 但 fixture 回归门与语义层单测是两道独立的保险，谁都不能替谁。
        /// </summary>
        [Fact]
        public void FullSnapshot用Unreliable被语义层拒绝()
        {
            var envelope = ValidFullSnapshot();
            envelope["reliability"] = "Unreliable";

            var result = MvpEnvelopeReader.Validate(Utf8(envelope));

            Assert.Equal(EnvelopeParseStatus.SemanticReject, result.Status);
        }

        [Fact]
        public void 镜像的正向fixture里FullSnapshot确实都是Reliable()
        {
            foreach (var name in MirrorFixtures.FixtureNames("valid", "replication-"))
            {
                var node = JsonNode.Parse(MirrorFixtures.ReadText("fixtures", "valid", name))!;
                if (node["messageType"]?.GetValue<string>() == "FullSnapshot")
                {
                    Assert.Equal("Reliable", node["reliability"]!.GetValue<string>());
                }
            }
        }

        /// <summary>
        /// canonical chunk key。四个用例的期望值取自
        /// <c>common.schema.json#/$defs/voxelChunkRevisionSet</c> 的 patternProperties 正则
        /// （与 <c>tools/lumio_contract.py</c> 的 <c>_CHUNK_KEY</c> 逐字一致）：
        /// 前导零与 11 位分量都不合法，<c>0</c> 与负数合法。
        ///
        /// 正则**不是手抄进本仓的**——语义层直接跑镜像 schema，所以这里测的是
        /// 「校验器确实把那条正则用上了」，而不是「我抄对了没有」。
        /// </summary>
        [Theory]
        [InlineData("c:0:0:0", true)]
        [InlineData("c:-1:2:3", true)]
        [InlineData("c:00:0:0", false)]
        [InlineData("c:12345678901:0:0", false)]
        public void 分块键必须是canonical形态(string chunkKey, bool shouldPass)
        {
            var envelope = ValidFullSnapshot();
            envelope["body"]!["sessionRevisionVector"]!["chunkRevisionSet"] = new JsonObject { [chunkKey] = 9 };

            var result = MvpEnvelopeReader.Validate(Utf8(envelope));

            Assert.Equal(
                shouldPass ? EnvelopeParseStatus.Ok : EnvelopeParseStatus.SemanticReject,
                result.Status);
        }

        [Fact]
        public void 修订向量缺任一字段即被语义层拒绝()
        {
            foreach (var field in new[]
                     {
                         "tickId", "gameRevision", "voxelWorldRevision", "chunkRevisionSet",
                         "replicationRevision", "configRevision", "schemaEpoch",
                     })
            {
                var envelope = ValidFullSnapshot();
                ((JsonObject)envelope["body"]!["sessionRevisionVector"]!).Remove(field);

                var result = MvpEnvelopeReader.Validate(Utf8(envelope));

                Assert.Equal(EnvelopeParseStatus.SemanticReject, result.Status);
            }
        }

        /// <summary>
        /// <c>length</c> 是**声明上界**，不是字节数。两条一起断言，因为它们互为对方的反面：
        /// 超上界必须拒（公共判定），而与实际字节数不符**必须不拒**（ADR-045 §3 明文
        /// 拒绝把 length 定义为任何字节数；镜像的 8 条正向 fixture 一律写死 256，
        /// 真做了交叉核对它们会全红）。
        /// </summary>
        [Fact]
        public void length超过本信封的maxMessageBytes被拒()
        {
            var envelope = ValidFullSnapshot();
            envelope["length"] = 999999999;

            var result = MvpEnvelopeReader.Validate(Utf8(envelope));

            Assert.Equal(EnvelopeParseStatus.SemanticReject, result.Status);
            Assert.Equal("BudgetExceeded", result.StableErrorId);
        }

        [Fact]
        public void length与实际字节数不符不作为拒绝理由()
        {
            var envelope = ValidFullSnapshot();
            envelope["length"] = 1; // 远小于实际字节数，且未超 maxMessageBytes

            var result = MvpEnvelopeReader.Validate(Utf8(envelope));

            Assert.Equal(EnvelopeParseStatus.Ok, result.Status);
        }

        [Fact]
        public void Delta的toRevision小于fromRevision被拒()
        {
            var envelope = ValidDelta();
            envelope["body"]!["fromRevision"] = 20;
            envelope["body"]!["toRevision"] = 19;

            var result = MvpEnvelopeReader.Validate(Utf8(envelope));

            Assert.Equal(EnvelopeParseStatus.SemanticReject, result.Status);
        }

        /// <summary>
        /// 入站 Delta 的合法集 = required + <c>{gapDetected, resyncReason}</c>。
        /// 两个额外成员**必须被接受**——它们在 schema 的 Delta 分支 properties 里，
        /// 只是不在 required 里；因「不在 required」而拒收会拒掉完全合法的报文。
        /// </summary>
        [Fact]
        public void Delta带gapDetected与resyncReason的合法报文被接受()
        {
            var envelope = ValidDelta();
            envelope["body"]!["gapDetected"] = true;
            envelope["body"]!["resyncReason"] = "GapDetected";

            var result = MvpEnvelopeReader.Validate(Utf8(envelope));

            Assert.Equal(EnvelopeParseStatus.Ok, result.Status);
        }

        [Fact]
        public void Delta的gapDetected为真而缺resyncReason被拒()
        {
            var envelope = ValidDelta();
            envelope["body"]!["gapDetected"] = true;

            var result = MvpEnvelopeReader.Validate(Utf8(envelope));

            Assert.Equal(EnvelopeParseStatus.SemanticReject, result.Status);
        }

        /// <summary>
        /// <c>Handshake</c> 的 body 必须**恰好等于** <c>{role}</c>。这条挡住 D-011 最容易踩的
        /// 违规点——把 token / nonce / ticket / credential 塞进 handshake body。
        /// ADR-045 之后 schema 的 <c>additionalProperties: false</c> 已经会拦，
        /// 本条保留是因为它测的是**意图**：凭据不走 body，走子协议位序
        /// （<c>absences.json</c> 的 <c>ABS-AUTH-CREDENTIAL-CARRIAGE</c>）。
        /// </summary>
        [Theory]
        [InlineData("token")]
        [InlineData("nonce")]
        [InlineData("ticket")]
        [InlineData("credential")]
        public void Handshake的body多出凭据字段即被拒(string smuggled)
        {
            var envelope = ValidHandshake();
            envelope["body"]![smuggled] = "should-not-be-here";

            var result = MvpEnvelopeReader.Validate(Utf8(envelope));

            Assert.NotEqual(EnvelopeParseStatus.Ok, result.Status);
        }

        /// <summary>
        /// <c>mappingSetHash</c> 常量不是抄来的字面量：从镜像的 canonical digest profile 里
        /// 取 <c>replication-mapping-set-empty</c> 的 golden 比对，并自算 sha256 复核
        /// canonicalBytes，三方一致才算数。
        /// </summary>
        [Fact]
        public void MappingSetHash等于镜像golden且可自算复核()
        {
            var profile = JsonNode.Parse(MirrorFixtures.ReadText("canonical", "canonical-digest-profile.json"))!;

            var golden = FindGolden(profile, "replication-mapping-set-empty");
            Assert.NotNull(golden);

            var canonicalBytes = golden!["canonicalBytes"]!.GetValue<string>();
            var published = golden["sha256"]!.GetValue<string>();

            Assert.Equal(MvpWireConstants.MappingSetHash, published);

            // 自算复核：canonicalBytes 是 {"digestDomain":"ReplicationMappingSetV1","mappings":[]}，
            // 它的 sha256 必须等于发布值——三方（常量 / golden / 自算）一致才算数。
            var recomputed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalBytes)))
                .ToLowerInvariant();
            Assert.Equal(published, recomputed);
        }

        [Fact]
        public void MappingSetHash不是被否决的sentinel()
        {
            // ADR-045 §2 的 Alternatives 逐一否决了三种 sentinel：空串、全零、省略成员。
            Assert.NotEqual(MvpWireConstants.MappingSetHash, new string('0', 64));
            Assert.NotEqual(MvpWireConstants.MappingSetHash, string.Empty);
            Assert.Matches("^[0-9a-f]{64}$", MvpWireConstants.MappingSetHash);
        }

        private static JsonNode? FindGolden(JsonNode node, string id)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj["id"]?.GetValue<string>() == id || obj["name"]?.GetValue<string>() == id)
                    {
                        return obj;
                    }

                    return obj.Select(p => p.Value is null ? null : FindGolden(p.Value, id)).FirstOrDefault(x => x is not null);
                case JsonArray array:
                    return array.Select(x => x is null ? null : FindGolden(x, id)).FirstOrDefault(x => x is not null);
                default:
                    return null;
            }
        }

        internal static byte[] Utf8(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

        internal static JsonObject ValidFullSnapshot()
            => (JsonObject)JsonNode.Parse(MirrorFixtures.ReadText("fixtures", "valid", "replication-full-snapshot.json"))!;

        internal static JsonObject ValidDelta()
            => (JsonObject)JsonNode.Parse(MirrorFixtures.ReadText("fixtures", "valid", "replication-delta.json"))!;

        internal static JsonObject ValidHandshake()
            => (JsonObject)JsonNode.Parse(MirrorFixtures.ReadText("fixtures", "valid", "replication-handshake.json"))!;
    }
}
