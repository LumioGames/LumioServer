using Lumio.Server.MvpHost.HostContracts;

namespace Lumio.Server.MvpHost.Session;

/// <summary>
/// Pure admission state reducer. It emits one typed effect and performs no IO.
/// Dependency execution belongs to <see cref="SessionRegistry"/>.
/// </summary>
public sealed class MvpAdmissionReducer : IAdmissionReducer
{
    public AdmissionStep Advance(
        in ServerConnectionSessionState state,
        in SessionCommand input)
    {
        var attempt = input switch
        {
            SessionCommand.ConnectionCandidate candidate =>
                new AdmissionAttemptId(candidate.ConnectionId.Value == 0 ? 1 : candidate.ConnectionId.Value),
            SessionCommand.DependencyResult dependency => dependency.Attempt,
            _ => default,
        };

        if (input is SessionCommand.ConnectionCandidate)
        {
            return new AdmissionStep(
                AdmissionEffectKind.ReadGate,
                attempt,
                state,
                null);
        }

        if (input is not SessionCommand.DependencyResult result)
        {
            return new AdmissionStep(AdmissionEffectKind.None, attempt, state, "InvalidArgument");
        }

        if (!result.Accepted)
        {
            return new AdmissionStep(
                AdmissionEffectKind.Compensate,
                attempt,
                state,
                result.StableErrorId ?? "QueueFull");
        }

        if (result.Effect is AdmissionEffectKind.None
            or AdmissionEffectKind.Compensate
            or AdmissionEffectKind.Reject)
        {
            return new AdmissionStep(
                AdmissionEffectKind.Reject,
                attempt,
                state,
                "InvalidArgument");
        }

        var next = result.Effect switch
        {
            AdmissionEffectKind.ReadGate => AdmissionEffectKind.Authenticate,
            AdmissionEffectKind.Authenticate => AdmissionEffectKind.MatchExactRelease,
            AdmissionEffectKind.MatchExactRelease => AdmissionEffectKind.ReserveSlot,
            AdmissionEffectKind.ReserveSlot => AdmissionEffectKind.CommitSlot,
            AdmissionEffectKind.CommitSlot => AdmissionEffectKind.CreateSession,
            AdmissionEffectKind.CreateSession => AdmissionEffectKind.BindConnection,
            AdmissionEffectKind.BindConnection => AdmissionEffectKind.StartReplication,
            AdmissionEffectKind.StartReplication => AdmissionEffectKind.None,
            _ => AdmissionEffectKind.None,
        };

        // StartReplication only queues FullSnapshot.  The client-side
        // BaselineAck is the sole event that may advance Syncing to Active.
        var nextState = result.Effect == AdmissionEffectKind.StartReplication
            ? ServerConnectionSessionState.Syncing
            : state;

        return new AdmissionStep(next, attempt, nextState, null);
    }
}
