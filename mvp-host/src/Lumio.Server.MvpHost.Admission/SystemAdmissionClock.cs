using System;

namespace Lumio.Server.MvpHost.Admission;

/// <summary>
/// Wall-clock seconds for C-3 credential expiry. Reconnect retention stays on
/// <c>IMonotonicClock</c>; this type must not be used for windows or timeouts.
/// DateTimeOffset is otherwise banned outside Platform's logging timestamp exit.
/// </summary>
public sealed class SystemAdmissionClock : IAdmissionClock
{
#pragma warning disable RS0030
    public ulong UnixSeconds => (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
#pragma warning restore RS0030
}
