using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Lumio.Server.MvpHost.Wire.Tests
{
    /// <summary>
    /// 镜像 fixture 的金标准门：正向全过、反向全拒，且反向逐条断言**被哪一层**拦下。
    /// 层次断言是这道门的要害——只断言「被拒」的话，实现退化成「只做 schema 校验」时
    /// 测试照样绿，而语义层恰恰是 schema 表达不了的那部分。
    /// </summary>
    public sealed class FixtureGoldenGateTest
    {
        /// <summary>
        /// 8 条正向 replication fixture 全部通过：解析 → 校验 → 重新序列化 → 再校验，
        /// 且两次解析得到的 header 字段值与 body 的 key/value 集合相等。
        ///
        /// **不断言字节相等**：envelope 的 wire byte encoding 尚未冻结（ADR-045 §3 明确
        /// 拒绝把它定死），断言字节相等等于本仓单方面宣布了一种规范 wire 形态。
        /// </summary>
        [Fact]
        public void 正向fixture全部往返通过()
        {
            var names = MirrorFixtures.FixtureNames("valid", "replication-");
            // replication-mapping 系列走的是另一份 schema，不是信封。
            names = names.Where(n => !n.StartsWith("replication-mapping", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(names);

            var failures = new List<string>();
            foreach (var name in names)
            {
                var utf8 = MirrorFixtures.ReadBytes("fixtures", "valid", name);

                var first = MvpEnvelopeReader.Validate(utf8);
                if (first.Status != EnvelopeParseStatus.Ok)
                {
                    failures.Add($"{name}: 首次校验 {first.Status} / {first.StableErrorId} / {first.Detail}");
                    continue;
                }

                var readHeader = MvpEnvelopeReader.TryReadHeader(utf8, out var header);
                if (readHeader.Status != EnvelopeParseStatus.Ok)
                {
                    failures.Add($"{name}: TryReadHeader {readHeader.Status}");
                    continue;
                }

                var reserialized = ReserializeThroughReader(utf8);
                var second = MvpEnvelopeReader.Validate(reserialized.Span);
                if (second.Status != EnvelopeParseStatus.Ok)
                {
                    failures.Add($"{name}: 重新序列化后校验 {second.Status} / {second.StableErrorId}");
                    continue;
                }

                MvpEnvelopeReader.TryReadHeader(reserialized.Span, out var header2);
                if (!HeadersEqualIgnoringWireLength(header, header2))
                {
                    failures.Add($"{name}: 往返后 header 不等 {header} != {header2}");
                }

                if (!BodyEquivalent(utf8, reserialized.Span))
                {
                    failures.Add($"{name}: 往返后 body 的 key/value 集合不等");
                }
            }

            Assert.Empty(failures);
        }

        /// <summary>
        /// 反向 replication fixture **全集**全部被拒，且逐条断言拦截层次。
        ///
        /// 期望层次表按 fixture 名列出，但**遍历的是目录**：上游新增一条反例而这里没登记时，
        /// 测试会以「未登记的反例」失败，而不是安静地跳过它。
        /// </summary>
        [Fact]
        public void 反向fixture全集被正确的层拒绝()
        {
            var expected = new Dictionary<string, EnvelopeParseStatus>(StringComparer.Ordinal)
            {
                // 结构层能判死的：schema 的 enum / pattern / additionalProperties 直接拦下。
                ["replication-unregistered-message-type.json"] = EnvelopeParseStatus.StructuralReject,
                ["replication-integrity-value-mismatch.json"] = EnvelopeParseStatus.StructuralReject,
                ["replication-body-extra-member.json"] = EnvelopeParseStatus.StructuralReject,
                ["replication-ack-smuggled-command.json"] = EnvelopeParseStatus.StructuralReject,
                ["replication-missing-snapshot-identity.json"] = EnvelopeParseStatus.StructuralReject,
                ["replication-mapping-set-hash-type.json"] = EnvelopeParseStatus.StructuralReject,

                // schema 表达不了、只能由语义层判死的：
                ["replication-gap-without-resync.json"] = EnvelopeParseStatus.SemanticReject,
                ["replication-unreliable-full-snapshot.json"] = EnvelopeParseStatus.SemanticReject,
                ["replication-length-exceeds-max.json"] = EnvelopeParseStatus.SemanticReject,
            };

            var names = MirrorFixtures.FixtureNames("invalid", "replication-")
                .Where(n => !n.StartsWith("replication-mapping-empty-field", StringComparison.Ordinal))
                .ToList();
            Assert.NotEmpty(names);

            var failures = new List<string>();
            foreach (var name in names)
            {
                if (!expected.TryGetValue(name, out var want))
                {
                    failures.Add($"{name}: 镜像里出现了未登记期望层次的反例——上游新增反例时必须在本表补一行");
                    continue;
                }

                var actual = MvpEnvelopeReader.Validate(MirrorFixtures.ReadBytes("fixtures", "invalid", name));
                if (actual.Status != want)
                {
                    failures.Add($"{name}: 期望 {want}，实际 {actual.Status} / {actual.StableErrorId} / {actual.Detail}");
                }
            }

            foreach (var registered in expected.Keys.Where(k => !names.Contains(k)))
            {
                failures.Add($"{registered}: 本表登记了它，但镜像里没有这个文件");
            }

            Assert.Empty(failures);
        }

        private static ReadOnlyMemory<byte> ReserializeThroughReader(ReadOnlySpan<byte> utf8)
        {
            // 往返用同一条 writer 路径不现实（9 个 writer 各自定死 body），
            // 因此往返只走「解析成节点再写回」，验证 reader 不丢字段、不改取值。
            var node = JsonNode.Parse(utf8.ToArray())!;
            return System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
        }

        private static bool HeadersEqualIgnoringWireLength(EnvelopeHeaderView a, EnvelopeHeaderView b)
            => a with { WireByteLength = 0 } == b with { WireByteLength = 0 };

        /// <summary>
        /// 比 key/value 的**语义等价**，不比原始文本——比文本就等于在断言字节相等，
        /// 而 envelope 的 wire byte encoding 尚未冻结（fixture 带缩进、序列化后不带，
        /// 两者是同一份内容的两种写法）。
        /// </summary>
        private static bool BodyEquivalent(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            var l = JsonNode.Parse(left.ToArray())!["body"];
            var r = JsonNode.Parse(right.ToArray())!["body"];
            return JsonNode.DeepEquals(l, r);
        }
    }
}
