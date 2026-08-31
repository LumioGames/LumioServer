using Lumio.Server.MvpHost.HostContracts;

namespace Lumio.Server.MvpHost.Transport;

/// <summary>
/// Internal carrier capability for transferring verified channel metadata without
/// putting credentials or proof state on a public HostContracts record.
/// </summary>
internal interface ITransportAuthenticationMetadataSource
{
    bool TryTakeAuthenticationMetadata(
        TransportConnectionId connectionId,
        ConnectionEpoch connectionEpoch,
        out PrincipalId principalId,
        out string productId,
        out string gameReleaseId);
}
