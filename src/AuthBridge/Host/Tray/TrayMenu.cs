using System.Diagnostics;
using AuthBridge.Common;

namespace AuthBridge.Host;

internal static class TrayMenu
{
    public static ContextMenuStrip Create(TrayUpdater updates, Form about, TrayControlWindow window)
    {
        var menu = new ContextMenuStrip { RenderMode = ToolStripRenderMode.System };
        menu.Items.Add("Open log", null, (_, _) =>
        {
            if (Log.FilePath is { } log)
                Process.Start(new ProcessStartInfo(log) { UseShellExecute = true })?.Dispose();
        });
        var update = menu.Items.Add("Update", null, (_, _) => updates.StartManual());
        var automatic = new ToolStripMenuItem("Auto-update");
        automatic.Click += (_, _) => updates.SetUpdateAutomatically(!automatic.Checked);
        menu.Items.Add(automatic);
        menu.Items.Add("About", null, (_, _) => about.ShowDialog(window));
        menu.Items.Add("Exit", null, (_, _) => window.PostClose());
        menu.Opening += (_, e) =>
        {
            if (about.Visible)
            {
                e.Cancel = true;
                about.Activate();
            }
            update.Available = updates.CanUpdate;
            automatic.Checked = updates.UpdateAutomatically;
        };
        return menu;
    }
}
