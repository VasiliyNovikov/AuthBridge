namespace AuthBridge.Host;

internal sealed class TrayMenu(Action openLog, Func<bool> canUpdate, Action update, Action about, Action exit)
{
    /// <summary>Bind the popup's one-based selection to the actions it displayed. Zero means it was dismissed.</summary>
    public void Show(Func<IReadOnlyList<string>, int> select)
    {
        List<(string Label, Action Invoke)> items = [("Open log", openLog)];
        if (canUpdate())
            items.Add(("Update", UpdateIfAvailable));
        items.Add(("About", about));
        items.Add(("Exit", exit));

        var command = select([.. items.Select(item => item.Label)]);
        if (command > 0 && command <= items.Count)
            items[command - 1].Invoke();
    }

    private void UpdateIfAvailable()
    {
        if (canUpdate())
            update();
    }
}
