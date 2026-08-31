using Lumio.Server.MvpHost.HostContracts;

namespace Lumio.Server.MvpHost.Session;

/// <summary>
/// Session-owned cleanup capability implemented only by the composition root.
/// It is internal so committed release cannot expand the frozen HostContracts API.
/// </summary>
internal interface ICommittedReservationReleasePort
{
    AckResult ReleaseCommittedReservation(
        SlotReservationId reservation,
        ServerSessionId session,
        SlotEpoch epoch);
}

/// <summary>
/// Session-owned bridge for private admission operations. Keeping this
/// capability in Session avoids production HostContracts friend access.
/// </summary>
internal interface ISessionWorldSlotPort : IWorldSlotHost, ICommittedReservationReleasePort
{
    SessionReservationResult ReserveAdmission(
        AdmissionAttemptId attempt,
        ServerSessionId session);

    AckResult AbortAdmission(SlotReservationId reservation, SlotEpoch epoch);
}

internal readonly record struct SessionReservationResult(
    bool Reserved,
    SlotReservationId Reservation,
    SlotEpoch Epoch,
    WorldSlotId SlotId,
    string? StableErrorId);
