using System.Text.Json.Nodes;

namespace Lumio.Server.MvpHost.Observability;

/// <summary>
/// 把两种记录序列化成 <c>logging-event.schema.json</c> 的线形态。
///
/// 该 schema 实测 <c>required</c> 恰 7 项且 <c>additionalProperties: false</c>——
/// 少写一个必填项或多写一个成员，产出的都不是合法事件。因此这里逐字段显式构造，
/// 不用反射式序列化：反射会把将来新增的 C# 属性自动带上线，而那正好会撞
/// <c>additionalProperties: false</c>，且撞的时机是运行期而不是编译期。
/// </summary>
public static class LoggingEventJson
{
    public static JsonObject From(in AuditRecord record)
    {
        var json = Base(
            record.EventId,
            record.Category,
            record.Severity,
            record.Timestamp,
            record.Correlation,
            record.Message,
            record.Durability);

        json["redaction"] = record.Redaction;

        if (record.ReasonCode is not null)
        {
            // fields 是 schema 允许的自由内容成员，本仓只放已注册的错误码。
            json["fields"] = new JsonObject { ["errorCode"] = record.ReasonCode };
        }

        return json;
    }

    public static JsonObject From(in DiagnosticRecord record)
        => Base(
            record.EventId,
            record.Category,
            record.Severity,
            record.Timestamp,
            record.Correlation,
            record.Message,
            durability: "BestEffort");

    private static JsonObject Base(
        string eventId,
        string category,
        string severity,
        string timestamp,
        in CorrelationView correlation,
        string message,
        string durability)
        => new()
        {
            ["eventId"] = eventId,
            ["category"] = category,
            ["severity"] = severity,
            ["timestamp"] = timestamp,
            ["correlation"] = CorrelationJson(correlation),
            ["message"] = message,
            ["durability"] = durability,
        };

    /// <summary>
    /// 可空字段**为空时整个成员不写出**，而不是写成 <c>null</c>：
    /// ADR-011 的 FORBIDDEN 表判的是「成员是否出现」，写 <c>null</c> 等于让被禁字段出现。
    /// </summary>
    private static JsonObject CorrelationJson(in CorrelationView c)
    {
        var json = new JsonObject
        {
            ["scope"] = c.Scope,
            ["productId"] = c.ProductId,
            ["gameReleaseId"] = c.GameReleaseId,
            ["traceId"] = c.TraceId,
            ["producerId"] = c.ProducerId,
            ["eventSeq"] = c.EventSeq,
        };

        if (c.ReleasePoolId is not null)
        {
            json["releasePoolId"] = c.ReleasePoolId;
        }

        if (c.SessionId is not null)
        {
            json["sessionId"] = c.SessionId;
        }

        if (c.WorldId is not null)
        {
            json["worldId"] = c.WorldId;
        }

        if (c.TickId is not null)
        {
            json["tickId"] = c.TickId.Value;
        }

        if (c.TxnId is not null)
        {
            json["txnId"] = c.TxnId;
        }

        return json;
    }
}
