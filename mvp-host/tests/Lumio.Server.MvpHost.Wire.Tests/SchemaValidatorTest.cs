using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Xunit;

namespace Lumio.Server.MvpHost.Wire.Tests
{
    /// <summary>
    /// 结构层校验器的构造边界。两个方向都要证：**认识的必须真的执行**，
    /// **不认识的必须炸而不是跳过**——静默跳过一条约束等于本仓单方面放宽了公共契约。
    /// </summary>
    public sealed class SchemaValidatorTest
    {
        /// <summary>
        /// <c>if</c>/<c>then</c> **必须被支持**。ADR-045 之后镜像的
        /// <c>replication-envelope.schema.json</c> 用 9 条 <c>allOf</c> 的 <c>if</c>/<c>then</c>
        /// 表达 body 封闭性，不支持就等于整个 body 面失守。
        ///
        /// 卡面原先要求「遇 if/then 抛出」，那是 ADR-045 之前的口径，已由总调度裁决改正——
        /// 那条测试与镜像 schema 互斥，留着会让金标准门永远跑不通。
        /// </summary>
        [Fact]
        public void 校验器支持条件分支并真的按分支判定()
        {
            var schema = JsonNode.Parse("""
                {
                  "type": "object",
                  "properties": { "kind": { "type": "string" }, "payload": { "type": "object" } },
                  "if": { "properties": { "kind": { "const": "closed" } } },
                  "then": { "properties": { "payload": { "additionalProperties": false } } }
                }
                """)!;

            // 条件成立且 then 不满足 → 判死。
            var violating = JsonNode.Parse("""{"kind":"closed","payload":{"extra":1}}""")!;
            Assert.NotNull(JsonSchemaValidator.Validate(violating, schema, "inline"));

            // 条件不成立 → 整条约束不适用，不是失败。
            var bypassing = JsonNode.Parse("""{"kind":"open","payload":{"extra":1}}""")!;
            Assert.Null(JsonSchemaValidator.Validate(bypassing, schema, "inline"));
        }

        /// <summary>
        /// 镜像 schema 里**确实**有 <c>if</c>/<c>then</c>——这条把上一条从「校验器能做」
        /// 提升为「校验器必须做」：哪天上游把 if/then 拿掉，这条会红，提醒来重看结论。
        /// </summary>
        [Fact]
        public void 镜像的信封schema确实用条件分支表达body封闭性()
        {
            var schema = JsonNode.Parse(MirrorFixtures.ReadText("schemas", "replication-envelope.schema.json"))!;
            var branches = (JsonArray)schema["allOf"]!;

            var withIfThen = branches.OfType<JsonObject>()
                .Where(b => b.ContainsKey("if") && b.ContainsKey("then"))
                .ToList();

            Assert.NotEmpty(withIfThen);

            foreach (var branch in withIfThen)
            {
                var body = (branch["then"] as JsonObject)?["properties"]?["body"] as JsonObject;
                Assert.NotNull(body);
                Assert.False(body["additionalProperties"]!.GetValue<bool>());
            }
        }

        /// <summary>
        /// 遇到白名单外的构造必须**抛出**。用 <c>dependentRequired</c> 作断言对象——
        /// 它是 JSON Schema 2020-12 的真实关键字、镜像 schema 里没有、且语义正好是
        /// 「gapDetected 出现则 resyncReason 必填」那类约束：万一上游哪天改用它表达，
        /// 这条会立刻炸，逼人来实现它，而不是让本仓静默漏掉一条公共约束。
        /// </summary>
        [Fact]
        public void 遇到白名单外的构造必须抛出而不是静默跳过()
        {
            var schema = JsonNode.Parse("""
                {
                  "type": "object",
                  "dependentRequired": { "gapDetected": ["resyncReason"] }
                }
                """)!;

            var instance = JsonNode.Parse("""{"gapDetected":true}""")!;

            var thrown = Assert.Throws<SchemaConstructNotSupportedException>(
                () => JsonSchemaValidator.Validate(instance, schema, "inline"));

            Assert.Contains("dependentRequired", thrown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 未镜像的schema被ref到时抛出而不是当作通过()
        {
            var schema = JsonNode.Parse("""{"$ref":"voxel-chunk-page.schema.json#/$defs/anything"}""")!;

            Assert.Throws<SchemaConstructNotSupportedException>(
                () => JsonSchemaValidator.Validate(JsonNode.Parse("{}"), schema, "inline"));
        }

        /// <summary>
        /// 本工程返回的每个 <c>StableErrorId</c> 都必须在
        /// <c>Lumio.Gen.ContractTypes.Catalog.StableErrorIds</c> 里。**本卡不发明任何新错误码。**
        /// 用源码扫描收集：反射看不到方法体里的字面量，而错误码正是写在方法体里的。
        /// 不断言个数（计数会随上游 additive 增补腐烂）。
        /// </summary>
        [Fact]
        public void 本工程用到的每个错误码都已在生成物注册()
        {
            var registered = Lumio.Gen.ContractTypes.Catalog.StableErrorIds.ToHashSet(StringComparer.Ordinal);
            var sourceDir = Path.Combine(MirrorFixtures.MvpHostRoot, "src", "Lumio.Server.MvpHost.Wire");

            var used = new List<string>();
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(
                             text,
                             @"EnvelopeParseResult\(\s*EnvelopeParseStatus\.\w+\s*,\s*""([A-Za-z]+)""",
                             RegexOptions.Singleline,
                             TimeSpan.FromSeconds(5)))
                {
                    used.Add(m.Groups[1].Value);
                }
            }

            Assert.NotEmpty(used);

            var unregistered = used.Distinct(StringComparer.Ordinal)
                .Where(id => !registered.Contains(id))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            Assert.Empty(unregistered);
        }

        /// <summary>
        /// 反向印证同一件事：结构层与语义层实际吐出的错误码也都在册。
        /// 上一条扫源码、这一条跑真实路径，两条覆盖的是同一条纪律的静态面与动态面。
        /// </summary>
        [Fact]
        public void 实际拒绝路径吐出的错误码都已注册()
        {
            var registered = Lumio.Gen.ContractTypes.Catalog.StableErrorIds.ToHashSet(StringComparer.Ordinal);
            var observed = new List<string>();

            foreach (var bucket in new[] { "valid", "invalid" })
            {
                foreach (var name in MirrorFixtures.FixtureNames(bucket, "replication-"))
                {
                    var result = MvpEnvelopeReader.Validate(MirrorFixtures.ReadBytes("fixtures", bucket, name));
                    if (result.StableErrorId is not null)
                    {
                        observed.Add(result.StableErrorId);
                    }
                }
            }

            Assert.NotEmpty(observed);

            var unregistered = observed.Distinct(StringComparer.Ordinal)
                .Where(id => !registered.Contains(id))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            Assert.Empty(unregistered);
        }
    }
}
