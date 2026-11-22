namespace AuthBridge.Host;

internal sealed class TrayMenu(Action openLog, Func<bool> canUpdate, Action update, Func<bool> updateAutomatically,
    Action<bool> setUpdateAutomatically, Action about, Action exit)
{
    public sealed record Item(string Label, Action Invoke, bool Checked = false);

    /// <summary>Bind the popup's one-based selection to the actions it displayed. Zero means it was dismissed.</summary>
    public void Show(Func<IReadOnlyList<Item>, int> select)
    {
        List<Item> items = [new("Open log", openLog)];
        if (canUpdate())
            items.Add(new("Update", UpdateIfAvailable));
        var enabled = updateAutomatically();
        items.Add(new("Auto-update", () => setUpdateAutomatically(!enabled), enabled));
        items.Add(new("About", about));
        items.Add(new("Exit", exit));

        var command = select(items);
        if (command > 0 && command <= items.Count)
            items[command - 1].Invoke();
    }

    private void UpdateIfAvailable()
    {
        if (canUpdate())
            update();
    }
}
