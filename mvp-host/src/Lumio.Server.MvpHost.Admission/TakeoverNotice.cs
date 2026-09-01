namespace Lumio.Server.MvpHost.Admission;

public readonly record struct TakeoverNotice(
    string ReasonCode,
    bool ReconnectEligible,
    ulong IssuedAt,
    string? Detail);
