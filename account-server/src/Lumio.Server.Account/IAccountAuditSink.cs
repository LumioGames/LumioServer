using System.Collections.Generic;

namespace Lumio.Server.Account;

public interface IAccountAuditSink
{
    void Write(string kind, IReadOnlyDictionary<string, string> fields);
}

internal sealed class NullAccountAuditSink : IAccountAuditSink
{
    public static readonly NullAccountAuditSink Instance = new();

    public void Write(string kind, IReadOnlyDictionary<string, string> fields)
    {
        _ = kind;
        _ = fields;
    }
}
