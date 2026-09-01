using System;

namespace Lumio.Server.MvpHost.Admission;

public static class RoomAdmissionFactory
{
    public static RoomAdmissionRegistry Create(
        byte admissionKeyId,
        ReadOnlyMemory<byte> admissionPublicKey,
        IAdmissionClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new RoomAdmissionRegistry(
            new AccountAdmissionVerifier(admissionKeyId, admissionPublicKey, clock),
            clock);
    }
}
