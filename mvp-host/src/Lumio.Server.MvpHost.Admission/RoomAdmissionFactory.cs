using System;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.Admission;

public static class RoomAdmissionFactory
{
    public static RoomAdmissionRegistry Create(
        byte admissionKeyId,
        ReadOnlyMemory<byte> admissionPublicKey,
        IAdmissionClock clock,
        IMonotonicClock monotonic,
        ITimerService timers,
        int reconnectWindowSeconds)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(monotonic);
        ArgumentNullException.ThrowIfNull(timers);
        var expiryInbox = PlatformModule.CreateInbox<ReconnectExpiryCommand>(
            new QueueBudget(EntityBindingPort.MaxBindingsPerRoom, 256L * 1024L));
        return new RoomAdmissionRegistry(
            new AccountAdmissionVerifier(admissionKeyId, admissionPublicKey, clock),
            clock,
            monotonic,
            timers,
            expiryInbox,
            reconnectWindowSeconds);
    }
}
