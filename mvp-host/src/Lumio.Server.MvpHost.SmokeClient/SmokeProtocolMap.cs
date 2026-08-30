using System;

namespace Lumio.Server.MvpHost.SmokeClient;

public enum SmokeInboundMessageKind
{
    Unknown,
    ServerHello,
    HandshakeReject,
    FullSnapshot,
    Delta,
}

/// <summary>
/// The fixed MVP inbound mapping. It deliberately examines only messageType;
/// no private wire field or new message kind is introduced here.
/// </summary>
public static class SmokeProtocolMap
{
    public static SmokeInboundMessageKind Classify(string? messageType)
        => messageType switch
        {
            "Handshake" => SmokeInboundMessageKind.ServerHello,
            "Error" => SmokeInboundMessageKind.HandshakeReject,
            "FullSnapshot" => SmokeInboundMessageKind.FullSnapshot,
            "Delta" => SmokeInboundMessageKind.Delta,
            "MaintenanceKick" => SmokeInboundMessageKind.Unknown,
            _ => SmokeInboundMessageKind.Unknown,
        };

    public static bool IsClientOutboundMessageType(string? messageType)
        => messageType is "Handshake" or "BaselineAck" or "DeltaAck" or "ResyncRequest";
}
