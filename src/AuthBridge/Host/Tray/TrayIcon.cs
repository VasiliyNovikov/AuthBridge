using AuthBridge.Common;

namespace AuthBridge.Host;

internal sealed class TrayIcon(Thread thread, TrayControlWindow window) : IDisposable
{
    public static TrayIcon Start(Helper helper, TrayUpdater updates, Action exit)
    {
        var ready = new TaskCompletionSource<TrayControlWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using (updates)
                    Run(helper, updates, ready);
            }
            catch (Exception e)
            {
                Log.Error(e, "tray icon failed");
                ready.TrySetException(e);
            }
            finally
            {
                exit();
            }
        }) { IsBackground = true, Name = "Tray icon" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return new TrayIcon(thread, ready.Task.GetAwaiter().GetResult());
    }

    private static void Run(Helper helper, TrayUpdater updates, TaskCompletionSource<TrayControlWindow> ready)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException, threadScope: true);
        Application.ThreadException += (_, e) => Log.Error(e.Exception, "tray action failed");
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetColorMode(SystemColorMode.System);

        using var resource = typeof(TrayIcon).Assembly.GetManifestResourceStream("AuthBridge.ico")!;
        using var icon = new Icon(resource);
        using var about = AboutDialog.Create(icon);
        using var status = new StatusDialog(icon, helper.Port, helper.GetStatus);
        using var window = new TrayControlWindow(updates);
        using var menu = TrayMenu.Create(updates, status.Window, about, window);
        using var notification = new StatusIcon(icon, menu, window);
        using var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        void Refresh()
        {
            updates.Refresh();
            var snapshot = helper.GetStatus();
            notification.Show(snapshot.Tunnels);
            if (status.Window.Visible)
                status.Update(snapshot);
        }
        timer.Tick += (_, _) => Refresh();
        Refresh();
        timer.Start();
        ready.SetResult(window);
        Application.Run();
    }

    public void Dispose()
    {
        window.PostClose();
        if (!thread.Join(TimeSpan.FromSeconds(5)))
            Log.Warn("tray thread did not stop within five seconds");
    }
}
