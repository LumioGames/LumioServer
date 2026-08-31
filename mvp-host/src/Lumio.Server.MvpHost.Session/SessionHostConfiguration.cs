using System;

namespace Lumio.Server.MvpHost.Session;

/// <summary>Immutable composition settings supplied by the application root.</summary>
public readonly record struct SessionHostConfiguration(
    string ProductId,
    string GameReleaseId,
    string ReleasePoolId,
    int ReconnectWindowSeconds,
    int AdmissionAttemptBudget,
    bool TestControlEnabled)
{
    public SessionHostConfiguration Normalize()
    {
        if (string.IsNullOrWhiteSpace(ProductId))
        {
            throw new ArgumentException("ProductId is required", nameof(ProductId));
        }

        if (string.IsNullOrWhiteSpace(GameReleaseId))
        {
            throw new ArgumentException("GameReleaseId is required", nameof(GameReleaseId));
        }

        if (string.IsNullOrWhiteSpace(ReleasePoolId))
        {
            throw new ArgumentException("ReleasePoolId is required", nameof(ReleasePoolId));
        }

        var window = ReconnectWindowSeconds > 0
            ? ReconnectWindowSeconds
            : TestControlEnabled
                ? SessionProvisionalDefaults.TestReconnectWindowSeconds
                : SessionProvisionalDefaults.ReconnectWindowSeconds;
        var budget = AdmissionAttemptBudget > 0
            ? AdmissionAttemptBudget
            : SessionProvisionalDefaults.AdmissionAttemptBudget;

        return this with
        {
            ReconnectWindowSeconds = window,
            AdmissionAttemptBudget = budget,
        };
    }
}
