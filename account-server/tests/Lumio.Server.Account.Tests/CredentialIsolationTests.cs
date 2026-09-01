using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Lumio.Server.Account;
using Xunit;

namespace Lumio.Server.Account.Tests;

public sealed class CredentialIsolationTests
{
    [Fact]
    public void IdentityComponentHasNoCredentialFields()
    {
        var names = typeof(AccountIdentityComponent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(["EntityId", "AccountId", "LoginName", "CreatedAtUnixSeconds"], names);
        Assert.DoesNotContain(names, name => name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Hash", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Argon", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DurableIdentityFileDoesNotContainPasswordOrHash()
    {
        using var harness = new AccountHarness();
        var created = harness.Runtime.LoginOrRegister("alice", AccountTestProfile.Password, null);
        Assert.True(created.Accepted, created.Code + " " + created.Detail);
        harness.Runtime.Flush();

        var identityPath = Path.Combine(harness.StorePath, DurableAccountStore.IdentityFileName);
        var credentialPath = Path.Combine(harness.StorePath, DurableAccountStore.CredentialFileName);
        Assert.True(File.Exists(identityPath));
        Assert.True(File.Exists(credentialPath));

        var identity = File.ReadAllText(identityPath);
        var credentials = File.ReadAllText(credentialPath);
        Assert.Contains(created.AccountId!, identity, StringComparison.Ordinal);
        Assert.Contains("alice", identity, StringComparison.Ordinal);
        Assert.DoesNotContain(AccountTestProfile.Password, identity, StringComparison.Ordinal);
        Assert.DoesNotContain("argon2", identity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AccountTestProfile.Password, credentials, StringComparison.Ordinal);
        Assert.Contains("argon2id", credentials, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("m=19456", credentials, StringComparison.Ordinal);
        Assert.Contains("t=2", credentials, StringComparison.Ordinal);
        Assert.Contains(created.AccountId!, credentials, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", credentials, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditEventsDoNotCarrySecrets()
    {
        using var harness = new AccountHarness();
        Assert.True(harness.Runtime.LoginOrRegister("alice", AccountTestProfile.Password, null).Accepted);
        Assert.False(harness.Runtime.LoginOrRegister("alice", "654321", null).Accepted);
        Assert.NotEmpty(harness.Audit.Events);
        Assert.Contains(harness.Audit.Events, item => item.Kind == "account_created");
        Assert.Contains(harness.Audit.Events, item => item.Kind == "login_succeeded");
        Assert.Contains(harness.Audit.Events, item => item.Kind == "login_rejected");
        Assert.Contains(harness.Audit.Events, item => item.Kind == "admission_credential_issued");
    }
}
