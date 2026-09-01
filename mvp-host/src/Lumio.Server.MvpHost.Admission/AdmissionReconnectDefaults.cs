namespace Lumio.Server.MvpHost.Admission;

/// <summary>
/// Host-layer reconnect retention (C-4). The deadline is process-local
/// monotonic time delivered by <c>ITimerService</c>; Native Tick/Frame is not
/// this window. The 10s value is a labeled test override only.
/// </summary>
public static class AdmissionReconnectDefaults
{
    public const int ReconnectWindowSeconds = 300;

    public const int TestReconnectWindowSeconds = 10;
}
