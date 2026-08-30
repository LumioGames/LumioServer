using System;
using System.IO;
using System.Text.Json.Nodes;
using Lumio.Server.MvpHost.Observability;

namespace Lumio.Server.MvpHost.App;

/// <summary>
/// The cross-process server trace is deliberately a write-only sink. Every line
/// has the same fixed field set so integration checks never need to parse log text.
/// </summary>
public sealed class JsonLinesHostTraceSink : IHostTraceSink, IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter writer;
    private ulong sequence;
    private bool disposed;

    public JsonLinesHostTraceSink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        writer = new StreamWriter(
            new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    public void Audit(in AuditRecord record)
    {
        var correlation = record.Correlation;
        Write(new JsonObject
        {
            ["kind"] = "audit",
            ["eventId"] = record.EventId,
            ["timestamp"] = record.Timestamp,
            ["category"] = record.Category,
            ["severity"] = record.Severity,
            ["scope"] = correlation.Scope,
            ["releasePoolId"] = correlation.ReleasePoolId,
            ["sessionId"] = correlation.SessionId,
            ["reasonCode"] = record.ReasonCode,
        });
    }

    public void Ack(string effect, ulong? admissionAttemptId, ulong? slotEpoch, ulong? connectionEpoch)
    {
        Write(new JsonObject
        {
            ["kind"] = "ack",
            ["effect"] = effect,
            ["admissionAttemptId"] = ToNode(admissionAttemptId),
            ["slotEpoch"] = ToNode(slotEpoch),
            ["connectionEpoch"] = ToNode(connectionEpoch),
        });
    }

    public void State(
        string? sessionId,
        string? sessionState,
        ulong? authorityRevision,
        ulong? slotEpoch,
        ulong? grantEpoch)
    {
        Write(new JsonObject
        {
            ["kind"] = "state",
            ["sessionId"] = sessionId,
            ["sessionState"] = sessionState,
            ["authorityRevision"] = ToNode(authorityRevision),
            ["slotEpoch"] = ToNode(slotEpoch),
            ["grantEpoch"] = ToNode(grantEpoch),
        });
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            writer.Dispose();
        }
    }

    private void Write(JsonObject values)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            var line = new JsonObject
            {
                ["seq"] = sequence++,
                ["kind"] = values["kind"]?.DeepClone(),
                ["eventId"] = values["eventId"]?.DeepClone(),
                ["timestamp"] = values["timestamp"]?.DeepClone(),
                ["category"] = values["category"]?.DeepClone(),
                ["severity"] = values["severity"]?.DeepClone(),
                ["scope"] = values["scope"]?.DeepClone(),
                ["releasePoolId"] = values["releasePoolId"]?.DeepClone(),
                ["sessionId"] = values["sessionId"]?.DeepClone(),
                ["reasonCode"] = values["reasonCode"]?.DeepClone(),
                ["admissionAttemptId"] = values["admissionAttemptId"]?.DeepClone(),
                ["effect"] = values["effect"]?.DeepClone(),
                ["sessionState"] = values["sessionState"]?.DeepClone(),
                ["authorityRevision"] = values["authorityRevision"]?.DeepClone(),
                ["slotEpoch"] = values["slotEpoch"]?.DeepClone(),
                ["connectionEpoch"] = values["connectionEpoch"]?.DeepClone(),
                ["grantEpoch"] = values["grantEpoch"]?.DeepClone(),
            };

            writer.WriteLine(line.ToJsonString());
        }
    }

    private static JsonValue? ToNode(ulong? value)
        => value is { } number
            ? JsonValue.Create(number)
            : null;
}
