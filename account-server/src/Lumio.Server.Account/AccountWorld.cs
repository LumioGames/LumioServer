using System;
using System.Collections.Generic;

namespace Lumio.Server.Account;

internal sealed class AccountWorld
{
    private readonly Dictionary<ulong, AccountIdentityComponent> identities = new();
    private readonly Dictionary<string, ulong> byLoginName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> byAccountId = new(StringComparer.Ordinal);
    private ulong nextEntityId = 1;

    public int Count
    {
        get
        {
            lock (identities)
            {
                return identities.Count;
            }
        }
    }

    public AccountIdentityComponent Create(string accountId, string loginName, ulong createdAtUnixSeconds)
    {
        lock (identities)
        {
            if (byLoginName.ContainsKey(loginName))
            {
                throw new InvalidOperationException("loginName already mapped");
            }

            var entityId = nextEntityId++;
            var component = new AccountIdentityComponent(entityId, accountId, loginName, createdAtUnixSeconds);
            identities[entityId] = component;
            byLoginName[loginName] = entityId;
            byAccountId[accountId] = entityId;
            return component;
        }
    }

    public void Restore(AccountIdentityComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        lock (identities)
        {
            identities[component.EntityId] = component;
            byLoginName[component.LoginName] = component.EntityId;
            byAccountId[component.AccountId] = component.EntityId;
            if (component.EntityId >= nextEntityId)
            {
                nextEntityId = component.EntityId + 1;
            }
        }
    }

    public bool ContainsAccountId(string accountId)
    {
        lock (identities)
        {
            return byAccountId.ContainsKey(accountId);
        }
    }

    public bool TryGetByLoginName(string loginName, out AccountIdentityComponent component)
    {
        lock (identities)
        {
            if (byLoginName.TryGetValue(loginName, out var entityId)
                && identities.TryGetValue(entityId, out var found))
            {
                component = found;
                return true;
            }
        }

        component = null!;
        return false;
    }

    public IReadOnlyList<AccountIdentityComponent> Snapshot()
    {
        lock (identities)
        {
            return [.. identities.Values];
        }
    }
}
