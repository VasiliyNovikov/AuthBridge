namespace AuthBridge.Host;

/// <summary>Remembers what Explorer has accepted, not merely the last status we tried to show.</summary>
internal sealed class TrayNotification(Func<string, bool> modify, Func<string, bool> add)
{
    private (string Tip, uint Color)? _published;

    public void Invalidate() => _published = null;

    public void Publish((string Tip, uint Color) status)
    {
        if (_published == status)
            return;
        // Windows remembers the initial tooltip as the icon name. Set the status only after adding the icon.
        _published = modify(status.Tip) || (add("AuthBridge") && modify(status.Tip)) ? status : null;
    }
}
