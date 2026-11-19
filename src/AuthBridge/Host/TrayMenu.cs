namespace AuthBridge.Host;

internal sealed class TrayMenu(Action openLog, Func<bool> canUpdate, Action update, Action about, Action exit)
{
    public sealed record Item(string Label, Action Invoke);

    /// <summary>A snapshot: each menu position continues to name the same action until this popup closes.</summary>
    public List<Item> Open()
    {
        List<Item> items = [new("Open log", openLog)];
        if (canUpdate())
            items.Add(new("Update", UpdateIfAvailable));
        items.Add(new("About", about));
        items.Add(new("Exit", exit));
        return items;
    }

    private void UpdateIfAvailable()
    {
        if (canUpdate())
            update();
    }
}
