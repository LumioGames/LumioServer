using System.Security.Cryptography;
using Lumio.Server.Account;
using Xunit;

namespace Lumio.Server.Account.Tests;

public sealed class BotNamespaceTests
{
    [Fact]
    public void OrdinaryClientCannotRegisterBotName()
    {
        using var harness = new AccountHarness();
        var result = harness.Runtime.LoginOrRegister("Bot77", AccountTestProfile.Password, null);
        Assert.False(result.Accepted);
        Assert.Equal(AccountErrorCode.BotNamespaceRegisterForbidden, result.Code);
        Assert.Equal(0, harness.Runtime.EntityCount);
        Assert.Null(harness.Runtime.FindByLoginName("Bot77"));
    }

    [Fact]
    public void BotToolCanRegisterGeneratedName()
    {
        using var harness = new AccountHarness();
        var result = harness.Runtime.LoginOrRegister("Bot07", AccountTestProfile.Password, harness.MintBotToolCredential());
        Assert.True(result.Accepted, result.Code + " " + result.Detail);
        Assert.True(result.AccountNewlyCreated);
        Assert.Equal("Bot07", result.LoginName);
    }

    [Fact]
    public void OrdinaryClientCannotLoginExistingBotEvenWithDefaultPassword()
    {
        using var harness = new AccountHarness();
        var created = harness.Runtime.LoginOrRegister("Bot07", AccountTestProfile.Password, harness.MintBotToolCredential());
        Assert.True(created.Accepted, created.Code + " " + created.Detail);

        var rejected = harness.Runtime.LoginOrRegister("Bot07", AccountTestProfile.Password, null);
        Assert.False(rejected.Accepted);
        Assert.Equal(AccountErrorCode.BotNamespaceLoginForbidden, rejected.Code);
        Assert.Equal(created.AccountId, harness.Runtime.FindByLoginName("Bot07")!.AccountId);
    }

    [Fact]
    public void MalformedBotToolCredentialIsRejected()
    {
        using var harness = new AccountHarness();
        var result = harness.Runtime.LoginOrRegister("Bot09", AccountTestProfile.Password, "%%%not-base64url%%%");
        Assert.False(result.Accepted);
        Assert.Equal(AccountErrorCode.BotToolCredentialMalformed, result.Code);
        Assert.Equal(0, harness.Runtime.EntityCount);
    }

    [Fact]
    public void BadBotToolSignatureIsRejected()
    {
        using var harness = new AccountHarness();
        var other = Ed25519Keys.Generate();
        var wire = AccountHarness.MintBotToolCredentialWithSeed(
            other.Seed,
            harness.Clock.UnixSeconds,
            harness.Clock.UnixSeconds + 3600,
            AccountPort.BotToolScope);
        var result = harness.Runtime.LoginOrRegister("Bot09", AccountTestProfile.Password, wire);
        Assert.False(result.Accepted);
        Assert.Equal(AccountErrorCode.BotToolCredentialInvalid, result.Code);
    }

    [Fact]
    public void WrongBotToolScopeIsRejectedAsInvalid()
    {
        using var harness = new AccountHarness();
        var wire = AccountHarness.MintBotToolCredentialWithSeed(
            harness.BotToolSeed,
            harness.Clock.UnixSeconds,
            harness.Clock.UnixSeconds + 3600,
            "not-bot-namespace");
        var result = harness.Runtime.LoginOrRegister("Bot09", AccountTestProfile.Password, wire);
        Assert.False(result.Accepted);
        Assert.Equal(AccountErrorCode.BotToolCredentialInvalid, result.Code);
    }

    [Fact]
    public void ExpiredBotToolCredentialIsRejected()
    {
        using var harness = new AccountHarness();
        var wire = harness.MintBotToolCredential(expiresAt: harness.Clock.UnixSeconds - 1);
        var result = harness.Runtime.LoginOrRegister("Bot09", AccountTestProfile.Password, wire);
        Assert.False(result.Accepted);
        Assert.Equal(AccountErrorCode.BotToolCredentialExpired, result.Code);
    }
}
