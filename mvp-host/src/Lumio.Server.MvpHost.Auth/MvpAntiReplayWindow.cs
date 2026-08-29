using System;
using Lumio.Server.MvpHost.HostContracts;
using Lumio.Server.MvpHost.Platform;

namespace Lumio.Server.MvpHost.Auth;

/// <summary>骨架（TDD 先失败证据用），实现随后落地。</summary>
public sealed class MvpAntiReplayWindow : IAntiReplayWindow
{
    public static MvpAntiReplayWindow Create(IMonotonicClock clock, int windowSeconds, int stormThreshold)
        => throw new NotImplementedException();

    public AntiReplayVerdict Check(PrincipalId principal, string nonce, MonotonicInstant receivedAt)
        => throw new NotImplementedException();

    public bool TryDrainReplayStorm(out PrincipalId offender)
        => throw new NotImplementedException();

    public int QuotaFor(PrincipalId principal)
        => throw new NotImplementedException();
}
