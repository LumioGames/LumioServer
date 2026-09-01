namespace Lumio.Server.MvpHost.Admission;

public interface IAdmissionClock
{
    ulong UnixSeconds { get; }
}
