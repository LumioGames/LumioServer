using System.Text.Json.Nodes;

namespace Lumio.Server.MvpHost.Wire;

/// <summary>
/// 镜像 schema 的**唯一**结构层校验入口，公开给上层工程复用。
///
/// 存在的理由是防止出现第二个校验器：Observability 也需要校验它产出的
/// <c>logging-event</c>，若各写一份，两者迟早在某条构造上分歧，
/// 而分歧的表现是「一边放行一边拦」，排查成本远高于在这里多开一个入口。
///
/// 校验器本身（<c>JsonSchemaValidator</c>）与镜像索引（<c>MirroredSchemas</c>）
/// 仍是 internal —— 公开的只有「拿一份已镜像的 schema 校验一个实例」这一件事，
/// 调用方无从传入自造 schema。
/// </summary>
public static class MvpSchemaGate
{
    /// <summary>
    /// 用镜像的 <c>logging-event.schema.json</c> 校验一条日志事件。
    /// 通过返回 <c>null</c>，否则返回首条失败说明（含 JSON 路径）。
    /// </summary>
    public static string? ValidateLoggingEvent(JsonNode candidate)
        => JsonSchemaValidator.Validate(
            candidate,
            MirroredSchemas.All[MirroredSchemas.LoggingEventId],
            MirroredSchemas.LoggingEventId);
}
