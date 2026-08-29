using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Lumio.Server.MvpHost.Wire
{
    /// <summary>
    /// 语义层：镜像 schema **表达不了**的那些约束。
    ///
    /// 真值来源一律是**镜像 schema 运行期读取**（总调度修订 ⑥），
    /// <c>tools/lumio_contract.py</c> 只作交叉印证、不作真值来源——
    /// 镜像 schema 是机器可读且被 sha256 锁住的，手抄 python 源码是纸面纪律，会静默腐烂。
    /// 因此本文件里**没有一张手抄的字段表、没有一条手抄的正则**：
    /// body required 取自 schema 的 <c>allOf/if/then</c>，chunk key 与 7 字段取自
    /// <c>session-revision-vector.schema.json</c>（它 <c>$ref</c> 到
    /// <c>common.schema.json#/$defs/sessionRevisionVector</c>）。
    ///
    /// 符号锚点（架构源 <c>origin/main</c>，测量时刻 2026-08-29，commit <c>664ccd6</c>；
    /// 验收不锁行号，用 <c>grep -n '&lt;符号&gt;' tools/lumio_contract.py</c> 复现）：
    /// <c>_REPLICATION_BODY_REQUIRED</c> / <c>_SESSION_REVISION_FIELDS</c> /
    /// <c>_CHUNK_KEY</c> / <c>_INTEGRITY_VALUE_RULES</c> / <c>FullSnapshot must use Reliable</c>。
    /// </summary>
    internal static class EnvelopeSemantics
    {
        /// <summary>
        /// <c>Delta</c> 的合法 body 成员 = schema 的 <c>required</c> + 这两个可选成员。
        ///
        /// **入站必须接受它们**（已定裁决）：schema 的 <c>Delta</c> 分支把它们列在
        /// <c>properties</c> 里、不列在 <c>required</c> 里，因「不在 required」而拒收
        /// 就会拒掉完全合法的报文。这两个名字不是本仓发明的，来自镜像 schema 的 properties。
        /// </summary>
        private const string GapDetected = "gapDetected";
        private const string ResyncReason = "resyncReason";

        internal static EnvelopeParseResult Validate(JsonObject envelope)
        {
            var messageType = envelope["messageType"]?.GetValue<string>();
            var body = envelope["body"] as JsonObject;

            if (messageType is null || body is null)
            {
                // 结构层已经拦过；走到这里说明调用序错了。
                return new EnvelopeParseResult(EnvelopeParseStatus.SemanticReject, "MessagePermissionDenied",
                    "缺 messageType 或 body");
            }

            // ① length 是**声明上界**，不是任何字节数（ADR-045 §3 明文拒绝把它定义为字节数）。
            //    公共判定只有一条：length MUST NOT exceed 本信封自己的 transportPolicy.maxMessageBytes。
            //    schema 表达不了跨字段比较，所以这条只能在语义层。
            //    与实际 wire 字节数**永不交叉核对**——镜像的 8 条正向 fixture 一律写死 length=256，
            //    真交叉核对了它们会全红。
            var declaredLength = envelope["length"]?.GetValue<long>();
            var maxMessageBytes = (envelope["transportPolicy"] as JsonObject)?["maxMessageBytes"]?.GetValue<long>();
            if (declaredLength is not null && maxMessageBytes is not null && declaredLength > maxMessageBytes)
            {
                // BudgetExceeded 在 A1 期是多义码（超长消息 / 队列背压预算共用）：
                // 可正向使用、不可反向断言——收到该码推不出成因。
                return new EnvelopeParseResult(EnvelopeParseStatus.SemanticReject, "BudgetExceeded",
                    $"length {declaredLength} 超过本信封声明的 maxMessageBytes {maxMessageBytes}");
            }

            // ② FullSnapshot ⟹ Reliable。公共契约里唯一一条 messageType × reliability 交叉约束
            //    （符号锚点 `FullSnapshot must use Reliable`）。schema 的 if/then 只按 messageType
            //    分支约束 body，不碰 reliability，所以这条也只能在语义层。
            if (messageType == "FullSnapshot" && envelope["reliability"]?.GetValue<string>() != "Reliable")
            {
                return new EnvelopeParseResult(EnvelopeParseStatus.SemanticReject, "MessagePermissionDenied",
                    "FullSnapshot 必须使用 Reliable");
            }

            // ③ sessionRevisionVector 的 7 字段与 chunk key 的 canonical 形态。
            //    信封 schema 里它只是 {"type":"object"} 空壳，结构层碰不到；
            //    真值在 session-revision-vector.schema.json（$ref 到 common 的 $defs）。
            //    正则因此**不是手抄的**，随镜像一起被哈希锁住。
            if (messageType == "FullSnapshot" && body["sessionRevisionVector"] is JsonNode vector)
            {
                var failure = JsonSchemaValidator.Validate(
                    vector,
                    MirroredSchemas.All[MirroredSchemas.SessionRevisionVectorId],
                    MirroredSchemas.SessionRevisionVectorId,
                    "$.body.sessionRevisionVector");

                if (failure is not null)
                {
                    return new EnvelopeParseResult(EnvelopeParseStatus.SemanticReject, "MessagePermissionDenied", failure);
                }
            }

            // ④ Delta 的修订序与 gap/resync 配对。
            if (messageType == "Delta")
            {
                var from = body["fromRevision"]?.GetValue<long>();
                var to = body["toRevision"]?.GetValue<long>();
                if (from is not null && to is not null && to < from)
                {
                    return new EnvelopeParseResult(EnvelopeParseStatus.SemanticReject, "RevisionConflict",
                        $"toRevision {to} < fromRevision {from}");
                }

                var gapDetected = body[GapDetected] is JsonValue g && g.TryGetValue<bool>(out var flag) && flag;
                if (gapDetected && body[ResyncReason] is null)
                {
                    return new EnvelopeParseResult(EnvelopeParseStatus.SemanticReject, "TargetRevisionUnavailable",
                        "gapDetected 为真时必须同时带 resyncReason");
                }
            }

            return new EnvelopeParseResult(EnvelopeParseStatus.Ok, null, null);
        }

        /// <summary>
        /// 某个 <c>messageType</c> 的 body **合法成员全集**，从镜像 schema 的
        /// <c>allOf</c> / <c>if</c> / <c>then</c> 运行期读出——不是手抄的常量表。
        ///
        /// 出站 exact-set 断言用它；入站不用（入站按公共语义只查缺失、不查多余，
        /// 多余成员由 schema 自己的 <c>additionalProperties: false</c> 判死）。
        /// </summary>
        internal static IReadOnlyCollection<string> BodyMembersOf(string messageType)
        {
            var schema = MirroredSchemas.ReplicationEnvelope;
            if (schema["allOf"] is not JsonArray branches)
            {
                throw new InvalidOperationException("镜像 envelope schema 没有 allOf——ADR-045 的 body 封闭性丢了？");
            }

            foreach (var branch in branches.OfType<JsonObject>())
            {
                var expected = (branch["if"] as JsonObject)?["properties"]?["messageType"]?["const"]?.GetValue<string>();
                if (expected != messageType)
                {
                    continue;
                }

                var bodySchema = (branch["then"] as JsonObject)?["properties"]?["body"] as JsonObject;
                var declared = (bodySchema?["properties"] as JsonObject)?.Select(p => p.Key);
                if (declared is null)
                {
                    break;
                }

                return declared.ToHashSet(StringComparer.Ordinal);
            }

            throw new InvalidOperationException($"镜像 envelope schema 里没有 messageType={messageType} 的分支");
        }

        /// <summary>某个 <c>messageType</c> 的 body **必填成员**，同样从镜像 schema 读出。</summary>
        internal static IReadOnlyCollection<string> BodyRequiredOf(string messageType)
        {
            var schema = MirroredSchemas.ReplicationEnvelope;
            foreach (var branch in (schema["allOf"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var expected = (branch["if"] as JsonObject)?["properties"]?["messageType"]?["const"]?.GetValue<string>();
                if (expected != messageType)
                {
                    continue;
                }

                var required = (branch["then"] as JsonObject)?["properties"]?["body"]?["required"] as JsonArray;
                if (required is null)
                {
                    break;
                }

                return required.Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            }

            throw new InvalidOperationException($"镜像 envelope schema 里没有 messageType={messageType} 的 body required");
        }
    }
}
