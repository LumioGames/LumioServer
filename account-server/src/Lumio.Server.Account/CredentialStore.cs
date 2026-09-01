using System;
using System.Collections.Generic;

namespace Lumio.Server.Account;

internal sealed class CredentialStore
{
    private readonly Dictionary<string, string> hashesByAccountId = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (hashesByAccountId)
            {
                return hashesByAccountId.Count;
            }
        }
    }

    public void Put(string accountId, string encodedHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        ArgumentException.ThrowIfNullOrEmpty(encodedHash);
        lock (hashesByAccountId)
        {
            hashesByAccountId[accountId] = encodedHash;
        }
    }

    public bool TryGet(string accountId, out string encodedHash)
    {
        lock (hashesByAccountId)
        {
            return hashesByAccountId.TryGetValue(accountId, out encodedHash!);
        }
    }

    public IReadOnlyList<KeyValuePair<string, string>> Snapshot()
    {
        lock (hashesByAccountId)
        {
            return [.. hashesByAccountId];
        }
    }
}
