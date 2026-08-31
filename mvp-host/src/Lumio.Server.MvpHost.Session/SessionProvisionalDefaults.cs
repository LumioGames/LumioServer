namespace Lumio.Server.MvpHost.Session;

/// <summary>Session queue and reconnect defaults; values remain provisional until SRV-D measurement.</summary>
public static class SessionProvisionalDefaults
{
    /// <summary>Provisional reconnect retention window in seconds.</summary>
    public const int ReconnectWindowSeconds = 120;

    /// <summary>Provisional test-profile override.</summary>
    public const int TestReconnectWindowSeconds = 10;

    /// <summary>Bounded session control inbox capacity.</summary>
    public const int ControlInboxMaxItems = 256;

    /// <summary>Bounded session event outbox capacity.</summary>
    public const int EventOutboxMaxItems = 256;

    /// <summary>Maximum admission attempts before a stable rejection.</summary>
    public const int AdmissionAttemptBudget = 3;
}
