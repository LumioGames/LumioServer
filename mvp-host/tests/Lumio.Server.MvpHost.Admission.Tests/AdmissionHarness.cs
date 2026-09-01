using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lumio.Server.Account;
using Lumio.Server.MvpHost.Admission;
using Xunit;

namespace Lumio.Server.MvpHost.Admission.Tests;

internal sealed class MutableAdmissionClock : IAdmissionClock
{
    public MutableAdmissionClock(ulong unixSeconds)
    {
        UnixSeconds = unixSeconds;
    }

    public ulong UnixSeconds { get; set; }
}

internal sealed class AdmissionHarness
{
    public const byte KeyId = 1;
    public const string MainRoom = "room-main";

    private readonly Dictionary<string, string> accountIds = new(StringComparer.Ordinal);

    public AdmissionHarness()
    {
        Clock = new MutableAdmissionClock(1_700_000_000);
        var keys = Ed25519Keys.Generate();
        AdmissionSeed = keys.Seed;
        AdmissionPublicKey = keys.PublicKey;
        Registry = new RoomAdmissionRegistry(
            new AccountAdmissionVerifier(KeyId, AdmissionPublicKey, Clock),
            Clock);
    }

    public MutableAdmissionClock Clock { get; }

    public byte[] AdmissionSeed { get; }

    public byte[] AdmissionPublicKey { get; }

    public RoomAdmissionRegistry Registry { get; }

    public string AccountIdFor(string loginName)
    {
        if (accountIds.TryGetValue(loginName, out var existing))
        {
            return existing;
        }

        var hex = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(loginName)))
            .ToLowerInvariant();
        var accountId = "acct_" + hex[..32];
        accountIds[loginName] = accountId;
        return accountId;
    }

    public string Issue(string loginName, bool botToolContext, ulong? expiresAt = null)
    {
        var issuedAt = Clock.UnixSeconds;
        return AdmissionCredential.Issue(
            AdmissionSeed,
            KeyId,
            AccountIdFor(loginName),
            loginName,
            botToolContext,
            issuedAt,
            expiresAt ?? issuedAt + 300);
    }

    public RoomAdmitOutcome Admit(string roomId, string connectionId, string loginName, bool botToolContext)
    {
        return Registry.Admit(roomId, connectionId, Issue(loginName, botToolContext));
    }

    public RoomAdmitOutcome.Accepted AdmitAccepted(
        string roomId,
        string connectionId,
        string loginName,
        bool botToolContext)
    {
        var outcome = Admit(roomId, connectionId, loginName, botToolContext);
        return AssertAccepted(outcome, loginName);
    }

    public static RoomAdmitOutcome.Accepted AssertAccepted(RoomAdmitOutcome outcome, string loginName)
    {
        _ = loginName;
        return Assert.IsType<RoomAdmitOutcome.Accepted>(outcome);
    }
}

internal static class BotLoginNames
{
    public static string Format(int index)
        => string.Create(CultureInfo.InvariantCulture, $"Bot{index:D2}");
}
