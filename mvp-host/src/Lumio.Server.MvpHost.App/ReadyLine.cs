using System;

namespace Lumio.Server.MvpHost.App;

public readonly record struct HostReadyLine(string ListenUri, string TestControlUri)
{
    public const string Prefix = "MVP_HOST_READY ";

    public override string ToString()
        => $"{Prefix}listen={ListenUri} testControl={TestControlUri}";

    public static bool TryParse(string? line, out HostReadyLine ready)
    {
        ready = default;
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var payload = line[Prefix.Length..];
        var listenMarker = "listen=";
        var controlMarker = " testControl=";
        if (!payload.StartsWith(listenMarker, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = payload.IndexOf(controlMarker, StringComparison.Ordinal);
        if (separator <= listenMarker.Length)
        {
            return false;
        }

        var listen = payload[listenMarker.Length..separator];
        var control = payload[(separator + controlMarker.Length)..];
        if (!Uri.TryCreate(listen, UriKind.Absolute, out var listenUri)
            || (listenUri.Scheme != "ws" && listenUri.Scheme != "wss")
            || string.IsNullOrWhiteSpace(control)
            || (control != "-" && !Uri.TryCreate(control, UriKind.Absolute, out _))
            || control.IndexOfAny([' ', '\t', '\r', '\n']) >= 0)
        {
            return false;
        }

        ready = new HostReadyLine(listen, control);
        return true;
    }
}
