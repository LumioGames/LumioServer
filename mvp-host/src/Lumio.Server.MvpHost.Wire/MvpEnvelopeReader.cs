using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lumio.Server.MvpHost.Wire
{
    /// <summary>
    /// 架构源 <c>replication-envelope.schema.json</c> 的 **reader**，不是 definition。
    ///
    /// 字段集与取值域的唯一真值是镜像 schema（嵌入程序集、运行期读取）；
    /// 信封本体用架构源生成的 <c>Lumio.Gen.ContractTypes.ReplicationEnvelope</c>，
    /// 本工程**不自造 DTO**——ADR-048 (D-3) 发布该类型后，再手写一份就是第二套定义、必然漂移。
    ///
    /// 两层校验分开报告：结构层是镜像 schema 判死的，语义层是 schema 表达不了、
    /// 必须另写的那部分。合成一个 Reject 会让「实现退化成只做 schema 校验」无法被测出来。
    /// </summary>
    public static class MvpEnvelopeReader
    {
        /// <summary>
        /// 只读头部，不做语义层校验。结构层不过时返回 <see cref="EnvelopeParseStatus.StructuralReject"/>，
        /// <paramref name="header"/> 为默认值。
        /// </summary>
        public static EnvelopeParseResult TryReadHeader(ReadOnlySpan<byte> utf8, out EnvelopeHeaderView header)
        {
            header = default;

            var parsed = ParseAndValidateStructure(utf8, out var envelope);
            if (parsed.Status != EnvelopeParseStatus.Ok)
            {
                return parsed;
            }

            header = ReadHeaderUnchecked(envelope!, utf8.Length);
            return new EnvelopeParseResult(EnvelopeParseStatus.Ok, null, null);
        }

        /// <summary>结构层 + 语义层全量校验。</summary>
        public static EnvelopeParseResult Validate(ReadOnlySpan<byte> utf8)
        {
            var structural = ParseAndValidateStructure(utf8, out var envelope);
            return structural.Status != EnvelopeParseStatus.Ok
                ? structural
                : EnvelopeSemantics.Validate(envelope!);
        }

        /// <summary>
        /// 解析成生成类型 <c>ReplicationEnvelope</c>。<c>Body</c> 保持 <c>OpaqueJson</c>——
        /// **换成具体类型即是发明 D-009 尚未裁决的公共状态载荷**（A1-β 仍 BLOCKED）。这是硬红线。
        /// </summary>
        internal static Lumio.Gen.ContractTypes.ReplicationEnvelope ToGeneratedEnvelope(JsonObject envelope)
        {
            var integrity = (JsonObject)envelope["integrity"]!;
            var policy = (JsonObject)envelope["transportPolicy"]!;

            return new Lumio.Gen.ContractTypes.ReplicationEnvelope(
                sessionId: envelope["sessionId"]!.GetValue<string>(),
                productId: envelope["productId"]!.GetValue<string>(),
                gameReleaseId: envelope["gameReleaseId"]!.GetValue<string>(),
                protocolVersion: envelope["protocolVersion"]!.GetValue<ulong>(),
                length: envelope["length"]!.GetValue<ulong>(),
                sequence: envelope["sequence"]!.GetValue<ulong>(),
                messageType: ParseEnum<Lumio.Gen.ContractTypes.ReplicationEnvelopeMessageType>(
                    envelope["messageType"]!.GetValue<string>()),
                reliability: ParseEnum<Lumio.Gen.ContractTypes.ReplicationEnvelopeReliability>(
                    envelope["reliability"]!.GetValue<string>()),
                integrity: new Lumio.Gen.ContractTypes.ReplicationEnvelopeIntegrity(
                    ParseEnum<Lumio.Gen.ContractTypes.ReplicationEnvelopeIntegrityAlgorithm>(
                        integrity["algorithm"]!.GetValue<string>()),
                    integrity["value"]!.GetValue<string>()),
                traceId: envelope["traceId"]!.GetValue<string>(),
                transportPolicy: new Lumio.Gen.ContractTypes.ReplicationEnvelopeTransportPolicy(
                    policy["maxMessageBytes"]!.GetValue<ulong>(),
                    policy["maxFragmentBytes"]!.GetValue<ulong>(),
                    policy["antiReplayWindow"]!.GetValue<ulong>(),
                    ParseEnum<Lumio.Gen.ContractTypes.ReplicationEnvelopeTransportPolicyAuthBinding>(
                        policy["authBinding"]!.GetValue<string>()),
                    ParseEnum<Lumio.Gen.ContractTypes.ReplicationEnvelopeTransportPolicyErrorClass>(
                        policy["errorClass"]!.GetValue<string>())),
                body: new Lumio.Gen.ContractTypes.OpaqueJson(envelope["body"]!.ToJsonString()));
        }

        internal static EnvelopeParseResult ParseAndValidateStructure(ReadOnlySpan<byte> utf8, out JsonObject? envelope)
        {
            envelope = null;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(utf8.ToArray());
            }
            catch (JsonException ex)
            {
                return new EnvelopeParseResult(EnvelopeParseStatus.StructuralReject, "ManifestMalformed", ex.Message);
            }

            if (node is not JsonObject obj)
            {
                return new EnvelopeParseResult(EnvelopeParseStatus.StructuralReject, "ManifestMalformed",
                    "信封顶层不是 JSON 对象");
            }

            var failure = JsonSchemaValidator.Validate(
                obj,
                MirroredSchemas.ReplicationEnvelope,
                MirroredSchemas.ReplicationEnvelopeId);

            if (failure is not null)
            {
                return new EnvelopeParseResult(EnvelopeParseStatus.StructuralReject, "ManifestMalformed", failure);
            }

            envelope = obj;
            return new EnvelopeParseResult(EnvelopeParseStatus.Ok, null, null);
        }

        private static EnvelopeHeaderView ReadHeaderUnchecked(JsonObject envelope, int wireByteLength)
            => new(
                ProtocolVersion: envelope["protocolVersion"]!.GetValue<int>(),
                Sequence: envelope["sequence"]!.GetValue<ulong>(),
                SessionId: envelope["sessionId"]!.GetValue<string>(),
                ProductId: envelope["productId"]!.GetValue<string>(),
                GameReleaseId: envelope["gameReleaseId"]!.GetValue<string>(),
                MessageType: envelope["messageType"]!.GetValue<string>(),
                Reliability: envelope["reliability"]!.GetValue<string>(),
                TraceId: envelope["traceId"]!.GetValue<string>(),
                WireByteLength: wireByteLength);

        private static T ParseEnum<T>(string wireValue) where T : struct, Enum
            => Enum.TryParse<T>(wireValue, ignoreCase: false, out var parsed)
                ? parsed
                : throw new InvalidOperationException(
                    $"结构层已放行但 {typeof(T).Name} 认不出线值 '{wireValue}'——镜像 schema 与生成 enum 不同源了");
    }
}
