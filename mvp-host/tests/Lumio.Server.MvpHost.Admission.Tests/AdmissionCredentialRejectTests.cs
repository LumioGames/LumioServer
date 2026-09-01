using System;
using System.Linq;
using System.Reflection;
using Lumio.Server.Account;
using Lumio.Server.MvpHost.Admission;
using Xunit;

namespace Lumio.Server.MvpHost.Admission.Tests;

public sealed class AdmissionCredentialRejectTests
{
    private static readonly string[] ForbiddenAdmitParameterNames =
    {
        "password", "username", "loginName", "entityType", "EntityType",
    };

    private static readonly string[] ExpectedAdmitParameterNames =
    {
        "roomId", "connectionId", "admissionCredential",
    };

    private static readonly string[] BindingFiveTupleNames =
    {
        "AccountId", "ConnectionGeneration", "EntityType", "NetEntityId", "RoomId",
    };

    [Fact]
    public void MalformedAdmissionCredentialIsRejected()
    {
        var harness = new AdmissionHarness();
        var outcome = harness.Registry.Admit(AdmissionHarness.MainRoom, "conn-1", "not-a-credential");
        var rejected = Assert.IsType<RoomAdmitOutcome.Rejected>(outcome);
        Assert.Equal(EntityBindingPort.AdmissionCredentialMalformed, rejected.Code);
    }

    [Fact]
    public void ExpiredAdmissionCredentialIsRejected()
    {
        var harness = new AdmissionHarness();
        var credential = harness.Issue("alice", false, expiresAt: harness.Clock.UnixSeconds + 10);
        harness.Clock.UnixSeconds += 20;

        var outcome = harness.Registry.Admit(AdmissionHarness.MainRoom, "conn-1", credential);
        var rejected = Assert.IsType<RoomAdmitOutcome.Rejected>(outcome);
        Assert.Equal(EntityBindingPort.AdmissionCredentialExpired, rejected.Code);
    }

    [Fact]
    public void TamperedAdmissionCredentialFailsSignature()
    {
        var harness = new AdmissionHarness();
        var credential = harness.Issue("alice", false);
        Assert.True(TestBase64Url.TryDecode(credential, out var framed));
        framed[framed.Length - Ed25519Keys.SignatureLength - 1] ^= 0xFF;

        var outcome = harness.Registry.Admit(AdmissionHarness.MainRoom, "conn-1", TestBase64Url.Encode(framed));
        var rejected = Assert.IsType<RoomAdmitOutcome.Rejected>(outcome);
        Assert.Equal(EntityBindingPort.AdmissionCredentialInvalidSignature, rejected.Code);
    }

    [Fact]
    public void AdmitSurfaceDoesNotAcceptUsernamePasswordOrClientEntityType()
    {
        var methods = typeof(RoomAdmissionRegistry)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(
            methods,
            method => method.Name.Contains("Password", System.StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("LoginOrRegister", System.StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("CreateAccount", System.StringComparison.OrdinalIgnoreCase));

        var parameterNames = methods
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.Name)
            .Where(name => name is not null)
            .ToArray();

        Assert.DoesNotContain(parameterNames, name => ForbiddenAdmitParameterNames.Contains(name, StringComparer.Ordinal));
        Assert.Contains(methods, method => method.Name == "Admit");
        Assert.Equal(
            ExpectedAdmitParameterNames,
            methods.Single(method => method.Name == "Admit").GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public void BindingRecordIsExactlyTheFiveTupleAndCarriesNoAccountEntityReference()
    {
        var names = typeof(ConnectionBinding)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(property => property.Name)
            .OrderBy(name => name, System.StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(BindingFiveTupleNames, names);
        Assert.DoesNotContain(
            typeof(ConnectionBinding).GetMembers(),
            member => member.Name.Contains("AccountEntity", System.StringComparison.Ordinal));
    }
}
