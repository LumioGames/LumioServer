using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace Lumio.Server.MvpHost.Wire.Tests
{
    /// <summary>
    /// 本工程消费的是**架构源生成的**信封类型，不是自己造的第二套定义。
    /// 这组测试盯住「没有偷偷长出第二套」以及「嵌入的 schema 与被锁的那份同源」。
    /// </summary>
    public sealed class GeneratedTypeContractTest
    {
        /// <summary>
        /// 头部字段集与镜像 schema 的顶层 <c>required</c> **恰好相等**（多一个少一个即失败）。
        /// 断言的是生成类型 <c>ReplicationEnvelope</c> 的属性，不是本仓 DTO——
        /// 本仓已不再有 DTO（ADR-048 发布该类型后再手写一份就是第二套定义）。
        /// </summary>
        [Fact]
        public void 生成信封类型的字段集与镜像schema的required恰好相等()
        {
            var required = RequiredOfEnvelopeSchema();

            var properties = typeof(Lumio.Gen.ContractTypes.ReplicationEnvelope)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => char.ToLower(p.Name[0], CultureInfo.InvariantCulture) + p.Name[1..])
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(required.OrderBy(x => x, StringComparer.Ordinal), properties.OrderBy(x => x, StringComparer.Ordinal));
        }

        /// <summary>
        /// 五处取值域与 schema 的 <c>enum</c> **恰好相等**。生成物的 enum 与镜像 schema
        /// 是两条独立发布出来的东西，同源只是当下的事实、不是保证——这条把它变成门禁。
        /// </summary>
        [Theory]
        [InlineData("messageType", typeof(Lumio.Gen.ContractTypes.ReplicationEnvelopeMessageType))]
        [InlineData("reliability", typeof(Lumio.Gen.ContractTypes.ReplicationEnvelopeReliability))]
        public void 顶层枚举取值域与schema一致(string field, Type enumType)
        {
            var schema = EnvelopeSchemaNode();
            var allowed = ((JsonArray)schema["properties"]![field]!["enum"]!)
                .Select(x => x!.GetValue<string>())
                .OrderBy(x => x, StringComparer.Ordinal);

            var names = Enum.GetNames(enumType).OrderBy(x => x, StringComparer.Ordinal);

            Assert.Equal(allowed, names);
        }

        [Fact]
        public void 完整性算法与传输策略的取值域与schema一致()
        {
            var schema = EnvelopeSchemaNode();

            var algorithms = ((JsonArray)schema["properties"]!["integrity"]!["properties"]!["algorithm"]!["enum"]!)
                .Select(x => x!.GetValue<string>()).OrderBy(x => x, StringComparer.Ordinal);
            Assert.Equal(
                algorithms,
                Enum.GetNames<Lumio.Gen.ContractTypes.ReplicationEnvelopeIntegrityAlgorithm>()
                    .OrderBy(x => x, StringComparer.Ordinal));

            var policy = schema["properties"]!["transportPolicy"]!["properties"]!;

            var authBindings = ((JsonArray)policy["authBinding"]!["enum"]!)
                .Select(x => x!.GetValue<string>()).OrderBy(x => x, StringComparer.Ordinal);
            Assert.Equal(
                authBindings,
                Enum.GetNames<Lumio.Gen.ContractTypes.ReplicationEnvelopeTransportPolicyAuthBinding>()
                    .OrderBy(x => x, StringComparer.Ordinal));

            var errorClasses = ((JsonArray)policy["errorClass"]!["enum"]!)
                .Select(x => x!.GetValue<string>()).OrderBy(x => x, StringComparer.Ordinal);
            Assert.Equal(
                errorClasses,
                Enum.GetNames<Lumio.Gen.ContractTypes.ReplicationEnvelopeTransportPolicyErrorClass>()
                    .OrderBy(x => x, StringComparer.Ordinal));
        }

        /// <summary>
        /// **硬红线**：<c>Body</c> 必须保持 <c>OpaqueJson</c>。换成具体类型 = 发明 D-009
        /// 尚未裁决的公共状态载荷（A1-β 仍 BLOCKED）。这条不是风格偏好。
        /// 同时断言本工程内没有长出任何 <c>*Body</c> 类型。
        /// </summary>
        [Fact]
        public void body保持不透明JSON且本工程没有具体body类型()
        {
            var body = typeof(Lumio.Gen.ContractTypes.ReplicationEnvelope)
                .GetProperty("Body", BindingFlags.Public | BindingFlags.Instance);

            Assert.NotNull(body);
            Assert.Equal("Lumio.Gen.ContractTypes.OpaqueJson", body.PropertyType.FullName);

            var offenders = typeof(MvpEnvelopeReader).Assembly
                .GetTypes()
                .Where(t => t.Name.EndsWith("Body", StringComparison.Ordinal))
                .Select(t => t.FullName)
                .ToList();

            Assert.Empty(offenders);
        }

        /// <summary>
        /// 嵌入进 Wire 程序集的 schema 与 <c>contract-mirror/</c> 下被 sha256 锁住的那份**逐字节相同**。
        ///
        /// 没有这条，嵌入副本就是一份「构建时抓的快照」——有人改了镜像却没重建，
        /// 生产代码会拿着旧 schema 继续放行，而哈希锁只看磁盘上的文件、看不见程序集里的副本。
        /// </summary>
        [Theory]
        [InlineData("replication-envelope.schema.json")]
        [InlineData("common.schema.json")]
        [InlineData("protocol-permission-gate.schema.json")]
        [InlineData("session-revision-vector.schema.json")]
        public void 嵌入的schema与镜像文件逐字节相同(string schemaId)
        {
            using var stream = typeof(MvpEnvelopeReader).Assembly.GetManifestResourceStream("mirror/" + schemaId);
            Assert.NotNull(stream);

            using var buffer = new System.IO.MemoryStream();
            stream.CopyTo(buffer);

            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(MirrorFixtures.ReadBytes("schemas", schemaId))),
                Convert.ToHexString(SHA256.HashData(buffer.ToArray())));
        }

        /// <summary>
        /// 字段名表直接转发生成物，**不是本仓复制的一份字面量**——引用同一个数组实例，
        /// 复制一份就等于给了它独立漂移的机会。不断言长度（计数会随 additive 增补腐烂）。
        /// </summary>
        [Fact]
        public void 权限字段名表引用生成物同一实例()
        {
            Assert.Same(
                Lumio.Gen.ProtocolPermissionValidator.ActivePermissionFields.Names,
                MvpProtocolPermissionGate.ActiveFieldNames);
        }

        internal static JsonNode EnvelopeSchemaNode()
            => JsonNode.Parse(MirrorFixtures.ReadText("schemas", "replication-envelope.schema.json"))!;

        internal static IReadOnlyCollection<string> RequiredOfEnvelopeSchema()
            => ((JsonArray)EnvelopeSchemaNode()["required"]!)
                .Select(x => x!.GetValue<string>())
                .ToHashSet(StringComparer.Ordinal);
    }
}
