using AuthBridge.Common;

namespace AuthBridge.Host;

/// <summary>Remembers what Explorer has accepted, not merely the last status we tried to show.</summary>
internal sealed class TrayNotification(Func<string, bool> modify, Func<string, bool> add)
{
    private const uint Green = 0x2EA043, Yellow = 0xD29922, Gray = 0x8B949E;
    private (string Tip, uint Color)? _published;

    public static (string Tip, uint Color) Describe(TunnelStatus[] tunnels)
    {
        if (tunnels.Length == 0)
            return ("AuthBridge: no machines configured", Gray);
        var connected = tunnels.Count(tunnel => tunnel.State == "connected");
        var color = connected == tunnels.Length ? Green : connected > 0 ? Yellow : Gray;
        var tip = $"AuthBridge: {connected}/{tunnels.Length} connected" + string.Concat(tunnels.Select(tunnel => $"\n{tunnel.Name}: {tunnel.State}"));
        // NOTIFYICONDATA holds 127 UTF-16 units plus a terminator. Keep whole characters before the ellipsis.
        if (tip.Length > 127)
            tip = tip[..(char.IsSurrogatePair(tip, 123) ? 123 : 124)] + "...";
        return (tip, color);
    }

    public void Invalidate() => _published = null;

    public void Publish((string Tip, uint Color) status)
    {
        if (_published == status)
            return;
        // Windows remembers the initial tooltip as the icon name. Set the status only after adding the icon.
        _published = modify(status.Tip) || (add("AuthBridge") && modify(status.Tip)) ? status : null;
    }
}
