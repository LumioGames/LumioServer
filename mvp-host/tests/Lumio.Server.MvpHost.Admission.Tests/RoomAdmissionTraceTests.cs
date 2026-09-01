using System.Linq;
using Lumio.Server.MvpHost.Admission;
using Xunit;

namespace Lumio.Server.MvpHost.Admission.Tests;

public sealed class RoomAdmissionTraceTests
{
    [Fact]
    public void MainAdmissionTraceCreatesOneHundredBotEntitiesAndOnePlayerEntity()
    {
        var harness = new AdmissionHarness();

        for (var i = 1; i <= 100; i++)
        {
            var loginName = BotLoginNames.Format(i);
            var accepted = harness.AdmitAccepted(
                AdmissionHarness.MainRoom,
                "conn-" + loginName,
                loginName,
                botToolContext: true);
            Assert.Equal(BoundEntityKind.Bot, accepted.Binding.EntityType);
            Assert.Equal("bot", accepted.Binding.EntityType.ToContractValue());
            Assert.Equal(AdmissionHarness.MainRoom, accepted.Binding.RoomId);
            Assert.Equal(harness.AccountIdFor(loginName), accepted.Binding.AccountId);
            Assert.Equal(1UL, accepted.Binding.ConnectionGeneration);
            Assert.Null(accepted.TerminationNotice);
        }

        var player = harness.AdmitAccepted(
            AdmissionHarness.MainRoom,
            "conn-Browser",
            "Browser",
            botToolContext: false);
        Assert.Equal(BoundEntityKind.Player, player.Binding.EntityType);
        Assert.Equal("player", player.Binding.EntityType.ToContractValue());
        Assert.Equal(harness.AccountIdFor("Browser"), player.Binding.AccountId);
        Assert.Equal(1UL, player.Binding.ConnectionGeneration);

        var bindings = harness.Registry.ListBindings(AdmissionHarness.MainRoom);
        Assert.Equal(101, bindings.Count);
        Assert.Equal(100, harness.Registry.CountEntities(AdmissionHarness.MainRoom, BoundEntityKind.Bot));
        Assert.Equal(1, harness.Registry.CountEntities(AdmissionHarness.MainRoom, BoundEntityKind.Player));
        Assert.Equal(100, bindings.Count(binding => binding.EntityType == BoundEntityKind.Bot));
        Assert.Equal(1, bindings.Count(binding => binding.EntityType == BoundEntityKind.Player));
        Assert.Equal(101, bindings.Select(binding => binding.NetEntityId).Distinct().Count());
        Assert.All(bindings, binding => Assert.StartsWith("nent_", binding.NetEntityId));
        Assert.All(bindings, binding => Assert.StartsWith("acct_", binding.AccountId));
    }
}
