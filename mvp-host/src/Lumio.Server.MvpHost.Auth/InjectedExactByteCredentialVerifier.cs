using System;
using Lumio.Server.MvpHost.HostContracts;

namespace Lumio.Server.MvpHost.Auth;

/// <summary>骨架（TDD 先失败证据用），实现随后落地。</summary>
public sealed class InjectedExactByteCredentialVerifier : ICredentialVerifier
{
    public static InjectedExactByteCredentialVerifier FromSecretFile(string path)
        => throw new NotImplementedException();

    public CredentialVerification Verify(OpaqueCredentialInput credential, in VerificationContext context)
        => throw new NotImplementedException();
}
