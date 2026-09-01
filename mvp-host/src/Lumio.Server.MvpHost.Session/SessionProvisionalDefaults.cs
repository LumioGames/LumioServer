namespace Lumio.Server.MvpHost.Session;

/// <summary>Session queue and reconnect defaults; values remain provisional until SRV-D measurement.</summary>
public static class SessionProvisionalDefaults
{
    /// <summary>Host monotonic reconnect window in seconds (C-4 five minutes).</summary>
    public const int ReconnectWindowSeconds = 300;

    /// <summary>Test-profile override; production remains five minutes.</summary>
    public const int TestReconnectWindowSeconds = 10;

    /// <summary>Bounded session control inbox capacity.</summary>
    public const int ControlInboxMaxItems = 256;

    /// <summary>Bounded session event outbox capacity.</summary>
    public const int EventOutboxMaxItems = 256;

    /// <summary>Maximum admission attempts before a stable rejection.</summary>
    public const int AdmissionAttemptBudget = 3;
}
