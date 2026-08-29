using System;
using System.Collections.Immutable;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Observability;
using Lumio.Server.MvpHost.Platform;
using Lumio.Server.MvpHost.Wire;

namespace Lumio.Server.MvpHost.Auth;

/// <summary>骨架（TDD 先失败证据用），实现随后落地。</summary>
public sealed class MvpAuthorizationService : IAuthorizationService
{
    public static MvpAuthorizationService Create(
        ICredentialVerifier verifier,
        IAntiReplayWindow antiReplay,
        IMonotonicClock clock,
        ObservabilityServices observability,
        string releasePoolId)
        => throw new NotImplementedException();

    public static ImmutableArray<string> ProducibleStableErrorIds => throw new NotImplementedException();

    public bool AdmissionMustStop => throw new NotImplementedException();

    public AuthenticateOutcome Authenticate(in AuthenticateCommand command)
        => throw new NotImplementedException();

    public PermissionGrant Authorize(PrincipalId principal, in SessionScope scope)
        => throw new NotImplementedException();

    public AckResult EvaluateMessagePermission(in MvpPermissionGateRequest request)
        => throw new NotImplementedException();

    public AuthQueueAdmission TryEnqueueRequest(in AuthenticateCommand command, out AckResult outward)
        => throw new NotImplementedException();

    public bool TryDequeueRequest(out AuthenticateCommand command)
        => throw new NotImplementedException();

    public void PublishOutcome(in AuthenticateOutcome outcome)
        => throw new NotImplementedException();

    public bool TryDequeueOutcome(out AuthenticateOutcome outcome)
        => throw new NotImplementedException();

    public void CloseQueues()
        => throw new NotImplementedException();
}
