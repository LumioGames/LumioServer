using Lumio.Server.Account;
using Lumio.Server.MvpHost.Admission;
using Xunit;

namespace Lumio.Server.MvpHost.Admission.Tests;

public sealed class BotNamespaceAdmissionTests
{
    [Fact]
    public void OrdinaryClientCannotEnterAsBotEntityWithoutBotToolContext()
    {
        var harness = new AdmissionHarness();
        var credential = harness.Issue("Bot07", botToolContext: false);

        var verified = AdmissionCredential.Verify(
            credential,
            AdmissionHarness.KeyId,
            harness.AdmissionPublicKey,
            new AccountClockAdapter(harness.Clock));
        var verifyRejected = Assert.IsType<AdmissionVerifyOutcome.Rejected>(verified);
        Assert.Equal(AccountErrorCode.BotNamespaceAdmissionForbidden, verifyRejected.Code);

        var admitted = harness.Registry.Admit(AdmissionHarness.MainRoom, "conn-browser", credential);
        var rejected = Assert.IsType<RoomAdmitOutcome.Rejected>(admitted);
        Assert.Equal(EntityBindingPort.BotNamespaceAdmissionForbidden, rejected.Code);
        Assert.Empty(harness.Registry.ListBindings(AdmissionHarness.MainRoom));
    }

    [Fact]
    public void BotToolContextClassifiesBotNamespaceAsBotEntity()
    {
        var harness = new AdmissionHarness();
        var accepted = harness.AdmitAccepted(AdmissionHarness.MainRoom, "conn-Bot07", "Bot07", botToolContext: true);
        Assert.Equal(BoundEntityKind.Bot, accepted.Binding.EntityType);
        Assert.Equal(harness.AccountIdFor("Bot07"), accepted.Binding.AccountId);
        Assert.Equal("bot", accepted.Binding.EntityType.ToContractValue());
    }
}

internal sealed class AccountClockAdapter : IAccountClock
{
    private readonly IAdmissionClock clock;

    public AccountClockAdapter(IAdmissionClock clock)
    {
        this.clock = clock;
    }

    public ulong UnixSeconds => clock.UnixSeconds;
}
