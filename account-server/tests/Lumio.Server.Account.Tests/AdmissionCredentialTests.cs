using System.Security.Cryptography;
using Lumio.Server.Account;
using Xunit;

namespace Lumio.Server.Account.Tests;

public sealed class AdmissionCredentialTests
{
    [Fact]
    public void IssuedAdmissionCredentialVerifiesWithMatchingFields()
    {
        using var harness = new AccountHarness();
        var login = harness.Runtime.LoginOrRegister("alice", AccountTestProfile.Password, null);
        Assert.True(login.Accepted, login.Code + " " + login.Detail);

        var verified = harness.Runtime.VerifyAdmission(login.AdmissionCredential!);
        var accepted = Assert.IsType<AdmissionVerifyOutcome.Accepted>(verified);
        Assert.Equal(login.AccountId, accepted.Payload.AccountId);
        Assert.Equal("alice", accepted.Payload.LoginName);
        Assert.False(accepted.Payload.BotToolContext);
        Assert.Equal(login.AdmissionExpiresAt, accepted.Payload.ExpiresAt);
        Assert.Equal(1, accepted.Payload.KeyId);
    }

    [Fact]
    public void BotLoginSetsBotToolContextOnAdmissionCredential()
    {
        using var harness = new AccountHarness();
        var login = harness.Runtime.LoginOrRegister("Bot07", AccountTestProfile.Password, harness.MintBotToolCredential());
        Assert.True(login.Accepted, login.Code + " " + login.Detail);
        var verified = harness.Runtime.VerifyAdmission(login.AdmissionCredential!);
        var accepted = Assert.IsType<AdmissionVerifyOutcome.Accepted>(verified);
        Assert.Equal("Bot07", accepted.Payload.LoginName);
        Assert.True(accepted.Payload.BotToolContext);
    }

    [Fact]
    public void ExpiredAdmissionCredentialIsRejected()
    {
        using var harness = new AccountHarness();
        var nonce = new byte[16];
        RandomNumberGenerator.Fill(nonce);
        var wire = AdmissionCredential.IssueFromPayload(
            harness.AdmissionSeed,
            new AdmissionCredentialPayload(
                1,
                "acct_" + new string('a', 32),
                "alice",
                false,
                harness.Clock.UnixSeconds - 400,
                harness.Clock.UnixSeconds - 100,
                nonce));
        var verified = harness.Runtime.VerifyAdmission(wire);
        var rejected = Assert.IsType<AdmissionVerifyOutcome.Rejected>(verified);
        Assert.Equal(AccountErrorCode.AdmissionCredentialExpired, rejected.Code);
    }

    [Fact]
    public void TamperedAdmissionPayloadFailsSignature()
    {
        using var harness = new AccountHarness();
        var login = harness.Runtime.LoginOrRegister("alice", AccountTestProfile.Password, null);
        Assert.True(login.Accepted);
        Assert.True(Base64Url.TryDecode(login.AdmissionCredential!, out var framed));
        framed[framed.Length - Ed25519Keys.SignatureLength - 1] ^= 0xFF;
        var tampered = Base64Url.Encode(framed);
        var verified = harness.Runtime.VerifyAdmission(tampered);
        var rejected = Assert.IsType<AdmissionVerifyOutcome.Rejected>(verified);
        Assert.Equal(AccountErrorCode.AdmissionCredentialInvalidSignature, rejected.Code);
    }

    [Fact]
    public void BotAdmissionWithoutToolContextIsForbidden()
    {
        using var harness = new AccountHarness();
        var nonce = new byte[16];
        RandomNumberGenerator.Fill(nonce);
        var wire = AdmissionCredential.IssueFromPayload(
            harness.AdmissionSeed,
            new AdmissionCredentialPayload(
                1,
                "acct_" + new string('b', 32),
                "Bot07",
                false,
                harness.Clock.UnixSeconds,
                harness.Clock.UnixSeconds + 300,
                nonce));
        var verified = harness.Runtime.VerifyAdmission(wire);
        var rejected = Assert.IsType<AdmissionVerifyOutcome.Rejected>(verified);
        Assert.Equal(AccountErrorCode.BotNamespaceAdmissionForbidden, rejected.Code);
    }

    [Fact]
    public void MalformedAdmissionWireIsRejected()
    {
        using var harness = new AccountHarness();
        var verified = harness.Runtime.VerifyAdmission("not-a-credential");
        var rejected = Assert.IsType<AdmissionVerifyOutcome.Rejected>(verified);
        Assert.Equal(AccountErrorCode.AdmissionCredentialMalformed, rejected.Code);
    }
}
