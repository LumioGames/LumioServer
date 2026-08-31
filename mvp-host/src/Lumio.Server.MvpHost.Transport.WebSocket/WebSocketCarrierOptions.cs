using System;

namespace Lumio.Server.MvpHost.Transport.WebSocket;

/// <summary>
/// Configuration for the WebSocket carrier. Values are deliberately local to the
/// adapter; no transport capability or credential format is added to HostContracts.
/// </summary>
public readonly record struct WebSocketCarrierOptions(
    string ListenUri,
    bool RequireTls,
    bool AllowInsecureLoopback,
    string HostProfile,
    int MaxMessageBytes,
    int MaxConnections,
    int IdleTimeoutSeconds,
    string ProductId,
    string GameReleaseId,
    string ReleasePoolId);

public static class WebSocketCarrierConstants
{
    // The subprotocol carries opaque token/nonce values only for the MVP period.
    // This is the ABS-AUTH-CREDENTIAL-CARRIAGE private convention and follows the
    // same exit discipline as the provisional length handling: once the architecture
    // source freezes a public carriage, remove this positional convention. The mvp/v0
    // marker is intentional and must remain until that migration occurs.
    public const string Subprotocol = "lumio.mvp.v0";

    public const int CloseStatusPolicyViolation = 1008;

    // Host-profile declaration only; provisional, replace with registered ID after R-00258.
    public const string ProvisionalTransportCapability = "WebSocketTransport";
}
