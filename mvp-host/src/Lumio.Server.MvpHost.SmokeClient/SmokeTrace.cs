using System;
using System.IO;
using System.Text;

namespace Lumio.Server.MvpHost.SmokeClient;

/// <summary>Fixed seven-field JSON-lines trace used by process-level assertions.</summary>
public sealed class SmokeTraceWriter : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter? writer;
    private int step;
    private bool disposed;

    public SmokeTraceWriter(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        writer = new StreamWriter(
            new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    public void Record(
        string direction,
        string? messageType,
        string assertion,
        bool passed,
        string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentException.ThrowIfNullOrWhiteSpace(assertion);

        lock (gate)
        {
            if (disposed || writer is null)
            {
                return;
            }

            writer.WriteLine(
                $"{{\"step\":{++step},\"direction\":{Quote(direction)},\"messageType\":{NullableQuote(messageType)},\"assertion\":{Quote(assertion)},\"passed\":{(passed ? "true" : "false")},\"detail\":{NullableQuote(detail)}}}");
        }
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
            writer?.Dispose();
        }
    }

    private static string NullableQuote(string? value) => value is null ? "null" : Quote(value);

    private static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < ' ')
                    {
                        builder.Append("\\u");
                        builder.Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
