using System;
using System.Text;
using System.Text.Json.Nodes;

namespace Lumio.Server.MvpHost.Wire
{
    /// <summary>
    /// 出站 writer，**按方向分组**：服务端 5 个 + 冒烟客户端 4 个 = 9 个。
    ///
    /// **不存在通用的 <c>Write(messageType, body)</c> 重载**——那会让「本仓向 body 加了什么字段」
    /// 变成运行期才知道的事，而 ADR-028 的 Alternatives 明文否决 free-form payload：
    /// 「two implementations can pass the gate and disagree on Snapshot identity」。
    /// 九个各自定死 body 形状的方法，才让「不多不少」成为编译期可读、测试可断言的事实。
    ///
    /// 后四个（<c>WriteClientHandshake</c> / <c>WriteBaselineAck</c> / <c>WriteDeltaAck</c> /
    /// <c>WriteResyncRequest</c>）**只供 SmokeClient 使用，服务端不调用**：
    /// <c>ResyncRequest</c> 在公共契约里由检测到 gap 的副本方发出，服务端只接收。
    ///
    /// 出站 body 的成员集恒**恰好等于**镜像 schema 该 messageType 分支的 required
    /// （ADR-045 之后 body 封闭性已是公共规则，本仓出站再收紧到 exact-set 作自检）。
    /// </summary>
    public static class MvpEnvelopeWriter
    {
        // —— 服务端出站 5 个 ——

        public static ReadOnlyMemory<byte> WriteServerHandshake(in EnvelopeWriteContext ctx)
            => Compose(ctx, "Handshake", ctx.Reliability, new JsonObject { ["role"] = "Server" });

        /// <summary>
        /// <c>reliability</c> **忽略 ctx 的取值、恒写 Reliable**：公共契约里唯一一条
        /// messageType × reliability 交叉约束（符号锚点 <c>FullSnapshot must use Reliable</c>）。
        /// </summary>
        public static ReadOnlyMemory<byte> WriteFullSnapshot(in EnvelopeWriteContext ctx, string snapshotId, ulong tickId, ulong authorityRevision)
            => Compose(ctx, "FullSnapshot", MvpWireConstants.Reliability, new JsonObject
            {
                ["snapshotId"] = snapshotId,
                ["tickId"] = tickId,
                ["sessionRevisionVector"] = RevisionVector(tickId, authorityRevision),
                ["schemaEpoch"] = GeneratedContracts.GeneratedContractManifest.SchemaEpoch,
                ["mappingSetHash"] = MvpWireConstants.MappingSetHash,
            });

        /// <summary>
        /// 不收 <c>tombstones</c> 入参、恒写空数组：MVP 参考存根的世界模型是不透明
        /// key→value 覆盖表，**不存在实体生命周期概念**，因此没有墓碑可发。
        /// 该字段仍必须在场（它是 <c>Delta</c> 的必填之一），但内容只能是空的。
        /// </summary>
        public static ReadOnlyMemory<byte> WriteDelta(in EnvelopeWriteContext ctx, string baseSnapshotId, ulong fromRevision, ulong toRevision, ulong confirmationSequence)
            => Compose(ctx, "Delta", ctx.Reliability, new JsonObject
            {
                ["baseSnapshotId"] = baseSnapshotId,
                ["fromRevision"] = fromRevision,
                ["toRevision"] = toRevision,
                ["mappingSetHash"] = MvpWireConstants.MappingSetHash,
                ["confirmationSequence"] = confirmationSequence,
                ["tombstones"] = new JsonArray(),
            });

        public static ReadOnlyMemory<byte> WriteMaintenanceKick(in EnvelopeWriteContext ctx, string reasonCode)
            => Compose(ctx, "MaintenanceKick", ctx.Reliability, new JsonObject { ["reasonCode"] = reasonCode });

        public static ReadOnlyMemory<byte> WriteError(in EnvelopeWriteContext ctx, string errorClass, string reasonCode)
            => Compose(ctx, "Error", ctx.Reliability, new JsonObject
            {
                ["errorClass"] = errorClass,
                ["reasonCode"] = reasonCode,
            });

        // —— 冒烟客户端出站 4 个 ——

        public static ReadOnlyMemory<byte> WriteClientHandshake(in EnvelopeWriteContext ctx)
            => Compose(ctx, "Handshake", ctx.Reliability, new JsonObject { ["role"] = "Client" });

        public static ReadOnlyMemory<byte> WriteBaselineAck(in EnvelopeWriteContext ctx, string snapshotId, ulong confirmedRevision)
            => Compose(ctx, "BaselineAck", ctx.Reliability, new JsonObject
            {
                ["snapshotId"] = snapshotId,
                ["confirmedRevision"] = confirmedRevision,
            });

        public static ReadOnlyMemory<byte> WriteDeltaAck(in EnvelopeWriteContext ctx, ulong confirmationSequence, ulong toRevision)
            => Compose(ctx, "DeltaAck", ctx.Reliability, new JsonObject
            {
                ["confirmationSequence"] = confirmationSequence,
                ["toRevision"] = toRevision,
            });

        public static ReadOnlyMemory<byte> WriteResyncRequest(in EnvelopeWriteContext ctx, string resyncReason)
            => Compose(ctx, "ResyncRequest", ctx.Reliability, new JsonObject { ["resyncReason"] = resyncReason });

        /// <summary>
        /// <c>sessionRevisionVector</c> 的 7 个字段按单一 <paramref name="authorityRevision"/>
        /// **机械填充**。这里**不引入任何体素、chunk、ECS 概念**——这 7 个字段是冻结 schema
        /// 强制的信封字段，本仓照填，不代表本仓拥有对应的权威状态。
        /// <c>chunkRevisionSet</c> 的单键 <c>c:0:0:0</c> 匹配 canonical chunk key 正则
        /// （真值在 <c>common.schema.json#/$defs/voxelChunkRevisionSet</c> 的 patternProperties）。
        /// </summary>
        private static JsonObject RevisionVector(ulong tickId, ulong authorityRevision) => new()
        {
            ["tickId"] = tickId,
            ["gameRevision"] = authorityRevision,
            ["voxelWorldRevision"] = authorityRevision,
            ["chunkRevisionSet"] = new JsonObject { ["c:0:0:0"] = authorityRevision },
            ["replicationRevision"] = authorityRevision,
            ["configRevision"] = 0,
            ["schemaEpoch"] = GeneratedContracts.GeneratedContractManifest.SchemaEpoch,
        };

        private static ReadOnlyMemory<byte> Compose(in EnvelopeWriteContext ctx, string messageType, string reliability, JsonObject body)
        {
            var envelope = new JsonObject
            {
                ["protocolVersion"] = MvpWireConstants.ProtocolVersion,

                // length 是**声明上界**，不是任何字节数（ADR-045 §3 明文拒绝把它定义为字节数，
                // 其 Alternatives 专门否决了 CanonicalJsonV1 byte count——那会让文本编码
                // 由副作用变成规范 wire 形态，抢先决定尚未冻结的 §4）。
                // 公共判定只有一条：不得超过本信封自己的 maxMessageBytes。本仓照上界写。
                ["length"] = ctx.MaxMessageBytes,

                ["sequence"] = ctx.Sequence,
                ["sessionId"] = ctx.SessionId,
                ["productId"] = ctx.ProductId,
                ["gameReleaseId"] = ctx.GameReleaseId,
                ["messageType"] = messageType,
                ["reliability"] = reliability,

                // 本仓不产出任何校验和，出站恒 None/none（schema 的 None 分支定死 ^none$）。
                ["integrity"] = new JsonObject
                {
                    ["algorithm"] = MvpWireConstants.IntegrityAlgorithmNone,
                    ["value"] = MvpWireConstants.IntegrityValueNone,
                },

                ["traceId"] = ctx.TraceId,
                ["transportPolicy"] = new JsonObject
                {
                    ["maxMessageBytes"] = ctx.MaxMessageBytes,
                    ["maxFragmentBytes"] = ctx.MaxFragmentBytes,
                    ["antiReplayWindow"] = ctx.AntiReplayWindow,
                    ["authBinding"] = ctx.AuthBinding,
                    ["errorClass"] = ctx.ErrorClass,
                },
                ["body"] = body,
            };

            return Encoding.UTF8.GetBytes(envelope.ToJsonString());
        }
    }
}
