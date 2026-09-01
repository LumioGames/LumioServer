using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Lumio.Server.Account;
using Xunit;

namespace Lumio.Server.Account.Tests;

public sealed class LoginOrRegisterAcceptanceTests
{
    private static readonly Regex AccountIdGrammar = new(AccountPort.AccountIdPattern, RegexOptions.CultureInvariant);

    [Fact]
    public void RegisterNewOrdinaryAccountCreatesEntityAndCredential()
    {
        using var harness = new AccountHarness();
        var first = harness.Runtime.LoginOrRegister("alice", AccountTestProfile.Password, null);

        Assert.True(first.Accepted, first.Code + " " + first.Detail);
        Assert.True(first.AccountNewlyCreated);
        Assert.Equal("alice", first.LoginName);
        Assert.Matches(AccountIdGrammar, first.AccountId);
        Assert.False(string.IsNullOrWhiteSpace(first.AdmissionCredential));
        Assert.True(first.AdmissionExpiresAt > AccountTestProfile.Now);
        Assert.Equal(1, harness.Runtime.EntityCount);
        var identity = harness.Runtime.FindByLoginName("alice");
        Assert.NotNull(identity);
        Assert.Equal(first.AccountId, identity!.AccountId);
        Assert.Equal("alice", identity.LoginName);
    }

    [Fact]
    public void RepeatLoginReturnsTheSameAccountId()
    {
        using var harness = new AccountHarness();
        var first = harness.Runtime.LoginOrRegister("alice", AccountTestProfile.Password, null);
        var second = harness.Runtime.LoginOrRegister("alice", AccountTestProfile.Password, null);

        Assert.True(first.Accepted);
        Assert.True(second.Accepted, second.Code + " " + second.Detail);
        Assert.False(second.AccountNewlyCreated);
        Assert.Equal(first.AccountId, second.AccountId);
        Assert.NotEqual(first.AdmissionCredential, second.AdmissionCredential);
        Assert.Equal(1, harness.Runtime.EntityCount);
    }

    [Fact]
    public void FirstBot01RequestCreatesStableAccountIdOnRepeat()
    {
        using var harness = new AccountHarness();
        var claim = harness.MintBotToolCredential();
        var first = harness.Runtime.LoginOrRegister("Bot01", AccountTestProfile.Password, claim);
        var second = harness.Runtime.LoginOrRegister("Bot01", AccountTestProfile.Password, claim);

        Assert.True(first.Accepted, first.Code + " " + first.Detail);
        Assert.True(first.AccountNewlyCreated);
        Assert.True(second.Accepted);
        Assert.False(second.AccountNewlyCreated);
        Assert.Equal(first.AccountId, second.AccountId);
        Assert.Equal(1, harness.Runtime.EntityCount);
        Assert.NotNull(harness.Runtime.FindByLoginName("Bot01"));
    }

    [Fact]
    public void WrongPasswordIsRejectedAndDoesNotOverwrite()
    {
        using var harness = new AccountHarness();
        var created = harness.Runtime.LoginOrRegister("alice", AccountTestProfile.Password, null);
        Assert.True(created.Accepted);
        var rejected = harness.Runtime.LoginOrRegister("alice", "654321", null);

        Assert.False(rejected.Accepted);
        Assert.Equal(AccountErrorCode.WrongPassword, rejected.Code);
        Assert.Null(rejected.AdmissionCredential);
        Assert.DoesNotContain(AccountTestProfile.Password, rejected.Detail ?? string.Empty, StringComparison.Ordinal);

        var retry = harness.Runtime.LoginOrRegister("alice", AccountTestProfile.Password, null);
        Assert.True(retry.Accepted, retry.Code + " " + retry.Detail);
        Assert.Equal(created.AccountId, retry.AccountId);
        Assert.Equal(1, harness.Runtime.EntityCount);
    }

    [Fact]
    public async Task ConcurrentFirstLoginsConvergeOnOneAccountEntity()
    {
        using var harness = new AccountHarness();
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => harness.Runtime.LoginOrRegister("bob", AccountTestProfile.Password, null)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, result => Assert.True(result.Accepted, result.Code + " " + result.Detail));
        var ids = results.Select(result => result.AccountId).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Single(ids);
        Assert.Equal(1, harness.Runtime.EntityCount);
        Assert.Equal(1, results.Count(result => result.AccountNewlyCreated));
    }

    [Fact]
    public void OneHundredGeneratedBotNamesAuthenticateThroughTheSameEndpoint()
    {
        using var harness = new AccountHarness();
        var claim = harness.MintBotToolCredential();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i <= 100; i++)
        {
            var name = "Bot" + i.ToString("00", CultureInfo.InvariantCulture);
            var result = harness.Runtime.LoginOrRegister(name, AccountTestProfile.Password, claim);
            Assert.True(result.Accepted, name + " " + result.Code + " " + result.Detail);
            Assert.True(result.AccountNewlyCreated);
            Assert.True(ids.Add(result.AccountId!));
        }

        Assert.Equal(100, harness.Runtime.EntityCount);
        Assert.Equal(100, ids.Count);
    }

    [Fact]
    public void InvalidUsernameGrammarIsRejected()
    {
        using var harness = new AccountHarness();
        var result = harness.Runtime.LoginOrRegister("x", AccountTestProfile.Password, null);
        Assert.False(result.Accepted);
        Assert.Equal(AccountErrorCode.InvalidUsername, result.Code);
        Assert.Equal(0, harness.Runtime.EntityCount);
    }

    [Fact]
    public void ShortPasswordIsRejectedAsInvalidPassword()
    {
        using var harness = new AccountHarness();
        var result = harness.Runtime.LoginOrRegister("alice", "12345", null);
        Assert.False(result.Accepted);
        Assert.Equal(AccountErrorCode.InvalidPassword, result.Code);
    }

    [Fact]
    public void SuccessfulLoginDoesNotEchoPassword()
    {
        using var harness = new AccountHarness();
        var result = harness.Runtime.LoginOrRegister("alice", AccountTestProfile.Password, null);
        Assert.True(result.Accepted);
        Assert.DoesNotContain(AccountTestProfile.Password, result.AccountId ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(AccountTestProfile.Password, result.AdmissionCredential ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(AccountTestProfile.Password, result.Detail ?? string.Empty, StringComparison.Ordinal);
    }
}
