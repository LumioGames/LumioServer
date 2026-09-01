using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lumio.Server.Account;
using Lumio.Server.MvpHost.Admission;
using Lumio.Server.MvpHost.Platform;
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

internal sealed class MutableMonotonicClock : IMonotonicClock
{
    public MonotonicInstant Now { get; set; }

    public void Advance(TimeSpan duration)
        => Now = new MonotonicInstant(Now.Ticks + duration.Ticks);
}

internal sealed class RecordingTimerService : ITimerService
{
    private readonly List<ScheduledTimer> pending = new();
    private ulong next;

    internal IReadOnlyList<ScheduledTimer> Scheduled => pending;

    public TimerId Schedule<TCommand>(MonotonicInstant dueAt, IBoundedInbox<TCommand> target, in TCommand command)
    {
        ArgumentNullException.ThrowIfNull(target);
        var captured = command;
        var id = new TimerId(++next);
        pending.Add(new ScheduledTimer(id, dueAt, captured!, () => target.TryEnqueue(in captured)));
        return id;
    }

    public bool Cancel(TimerId id)
    {
        for (var index = 0; index < pending.Count; index++)
        {
            if (pending[index].Id != id)
            {
                continue;
            }

            pending.RemoveAt(index);
            return true;
        }

        return false;
    }

    public void DeliverDue(MonotonicInstant now)
    {
        for (var index = pending.Count - 1; index >= 0; index--)
        {
            var timer = pending[index];
            if (timer.DueAt.Ticks > now.Ticks)
            {
                continue;
            }

            _ = timer.Deliver();
            pending.RemoveAt(index);
        }
    }

    public void Dispose() => pending.Clear();
}

internal sealed class ScheduledTimer
{
    internal ScheduledTimer(TimerId id, MonotonicInstant dueAt, object command, Func<EnqueueResult> deliver)
    {
        Id = id;
        DueAt = dueAt;
        Command = command;
        Deliver = deliver;
    }

    internal TimerId Id { get; }

    internal MonotonicInstant DueAt { get; }

    internal object Command { get; }

    internal Func<EnqueueResult> Deliver { get; }
}

internal sealed class AdmissionHarness
{
    public const byte KeyId = 1;
    public const string MainRoom = "room-main";

    private readonly Dictionary<string, string> accountIds = new(StringComparer.Ordinal);

    public AdmissionHarness(int reconnectWindowSeconds = AdmissionReconnectDefaults.TestReconnectWindowSeconds)
    {
        Clock = new MutableAdmissionClock(1_700_000_000);
        Monotonic = new MutableMonotonicClock();
        Timers = new RecordingTimerService();
        ExpiryInbox = PlatformModule.CreateInbox<ReconnectExpiryCommand>(
            new QueueBudget(EntityBindingPort.MaxBindingsPerRoom, 256L * 1024L));
        var keys = Ed25519Keys.Generate();
        AdmissionSeed = keys.Seed;
        AdmissionPublicKey = keys.PublicKey;
        Registry = new RoomAdmissionRegistry(
            new AccountAdmissionVerifier(KeyId, AdmissionPublicKey, Clock),
            Clock,
            Monotonic,
            Timers,
            ExpiryInbox,
            reconnectWindowSeconds);
    }

    public MutableAdmissionClock Clock { get; }

    public MutableMonotonicClock Monotonic { get; }

    public RecordingTimerService Timers { get; }

    public IBoundedInbox<ReconnectExpiryCommand> ExpiryInbox { get; }

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

    public void FireDueReconnectTimers()
        => Timers.DeliverDue(Monotonic.Now);

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
