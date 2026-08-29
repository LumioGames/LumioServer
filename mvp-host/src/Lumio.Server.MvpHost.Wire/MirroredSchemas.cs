using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Lumio.Server.MvpHost.Wire
{
    /// <summary>
    /// 镜像 schema 的装载与索引。schema 随程序集嵌入（见 Wire.csproj），
    /// 运行期**不依赖仓库目录布局**——生产代码一旦要找「自己在仓库树里的位置」，部署后就会坏。
    ///
    /// 嵌入副本与 <c>contract-mirror/</c> 下被 sha256 锁住的源文件字节一致，
    /// 由 <c>MirroredSchemaMatchesContractMirrorTest</c> 守住。
    /// </summary>
    internal static class MirroredSchemas
    {
        internal const string ReplicationEnvelopeId = "replication-envelope.schema.json";
        internal const string CommonId = "common.schema.json";
        internal const string ProtocolPermissionGateId = "protocol-permission-gate.schema.json";
        internal const string SessionRevisionVectorId = "session-revision-vector.schema.json";
        internal const string LoggingEventId = "logging-event.schema.json";

        private static readonly Dictionary<string, JsonObject> Loaded = Load();

        internal static JsonObject ReplicationEnvelope => Loaded[ReplicationEnvelopeId];

        internal static IReadOnlyDictionary<string, JsonObject> All => Loaded;

        /// <summary>
        /// 解析 <c>&lt;file&gt;#/$defs/&lt;name&gt;</c> 形式的 <c>$ref</c>。
        /// 镜像 schema 里的 <c>$ref</c> 一律是这种跨文件 + <c>$defs</c> 的形式（实测），
        /// 因此不实现通用的 JSON Pointer——支持用不到的形式只会让「不支持的构造」这条门变松。
        /// </summary>
        internal static JsonNode Resolve(string reference, string currentDocumentId)
        {
            var hash = reference.IndexOf('#', StringComparison.Ordinal);
            if (hash < 0)
            {
                throw new SchemaConstructNotSupportedException($"$ref 缺少片段部分：{reference}");
            }

            var documentId = hash == 0 ? currentDocumentId : reference[..hash];
            var pointer = reference[(hash + 1)..];

            if (!Loaded.TryGetValue(documentId, out var document))
            {
                throw new SchemaConstructNotSupportedException($"$ref 指向未镜像的 schema：{documentId}");
            }

            JsonNode node = document;
            foreach (var rawSegment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                node = node is JsonObject obj && obj.TryGetPropertyValue(segment, out var next) && next is not null
                    ? next
                    : throw new SchemaConstructNotSupportedException($"$ref 解析不到 {reference}（卡在 {segment}）");
            }

            return node;
        }

        private static Dictionary<string, JsonObject> Load()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

            foreach (var id in new[] { ReplicationEnvelopeId, CommonId, ProtocolPermissionGateId, SessionRevisionVectorId, LoggingEventId })
            {
                using var stream = assembly.GetManifestResourceStream("mirror/" + id)
                    ?? throw new InvalidOperationException($"嵌入资源缺失：mirror/{id}（检查 Wire.csproj 的 EmbeddedResource）。");
                using var reader = new StreamReader(stream);
                result[id] = JsonNode.Parse(reader.ReadToEnd()) as JsonObject
                    ?? throw new InvalidOperationException($"镜像 schema 顶层不是对象：{id}");
            }

            return result;
        }
    }

    /// <summary>
    /// 校验器遇到它不认识的 schema 构造时抛出。**刻意让它炸而不是忽略**：
    /// 静默跳过一条不认识的约束，等于本仓单方面放宽了一条公共契约，而且没人会发现。
    /// </summary>
    internal sealed class SchemaConstructNotSupportedException : Exception
    {
        internal SchemaConstructNotSupportedException(string message) : base(message)
        {
        }
    }
}
