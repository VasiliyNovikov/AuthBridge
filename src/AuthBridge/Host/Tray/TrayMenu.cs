using System.Diagnostics;
using AuthBridge.Common;

namespace AuthBridge.Host;

internal static class TrayMenu
{
    public static void Populate(ContextMenuStrip menu, TrayUpdater updates, Form status, Form about, TrayControlWindow window, Action<string> report)
    {
        menu.RenderMode = ToolStripRenderMode.System;
        menu.Items.Add("Status", null, (_, _) => window.ShowDialog(status));
        menu.Items.Add("Open log", null, (_, _) =>
        {
            if (Log.FilePath is { } log)
                Process.Start(new ProcessStartInfo(log) { UseShellExecute = true })?.Dispose();
        });
        AddUpdateItem(menu, updates, report);
        var automatic = new ToolStripMenuItem("Auto-update");
        automatic.Click += (_, _) => updates.SetUpdateAutomatically(!automatic.Checked);
        menu.Items.Add(automatic);
        menu.Items.Add("About", null, (_, _) => window.ShowDialog(about));
        menu.Items.Add("Exit", null, (_, _) => window.PostClose());
        menu.Opening += (_, e) =>
        {
            if (window.OpenDialog is { } dialog)
            {
                e.Cancel = true;
                dialog.Activate();
            }
            automatic.Checked = updates.UpdateAutomatically;
        };
    }

    private static void AddUpdateItem(ContextMenuStrip menu, TrayUpdater updates, Action<string> report)
    {
        var item = menu.Items.Add("Check...");
        string? displayedVersion = null;
        void Refresh()
        {
            displayedVersion = updates.AvailableVersion;
            item.Text = displayedVersion is null ? "Check..." : $"Update ... {displayedVersion}";
            item.Enabled = !updates.IsBusy;
        }
        menu.Opening += (_, _) => Refresh();
        item.Click += async (_, _) =>
        {
            var message = await updates.CheckOrUpdateAsync(displayedVersion);
            if (menu.IsDisposed)
                return;
            Refresh();
            if (message is not null)
                report(message);
        };
    }
}
