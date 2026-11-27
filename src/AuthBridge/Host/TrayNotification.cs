using AuthBridge.Common;

namespace AuthBridge.Host;

internal static class TrayNotification
{
    private const uint Green = 0x2EA043, Yellow = 0xD29922, Gray = 0x8B949E;

    /// <summary>How many of the configured machines are connected, as the tooltip and the Status dialog show it.</summary>
    public static string Connections(TunnelStatus[] tunnels) => $"{Connected(tunnels)}/{tunnels.Length} connected";

    public static (string Tip, uint Color) Describe(TunnelStatus[] tunnels)
    {
        if (tunnels.Length == 0)
            return ("AuthBridge: no machines configured", Gray);
        var connected = Connected(tunnels);
        var color = connected == tunnels.Length ? Green : connected > 0 ? Yellow : Gray;
        var tip = $"AuthBridge: {Connections(tunnels)}" + string.Concat(tunnels.Select(tunnel => $"\n{tunnel.Name}: {tunnel.State}"));
        // NOTIFYICONDATA holds 127 UTF-16 units plus a terminator. Keep whole characters before the ellipsis.
        if (tip.Length > 127)
            tip = tip[..(char.IsSurrogatePair(tip, 123) ? 123 : 124)] + "...";
        return (tip, color);
    }

    private static int Connected(TunnelStatus[] tunnels) => tunnels.Count(tunnel => tunnel.State == TunnelStatus.Connected);
}
