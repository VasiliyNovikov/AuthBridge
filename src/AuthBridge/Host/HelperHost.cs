using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AuthBridge.Common;

namespace AuthBridge.Host;

/// <summary>Runs the Windows helper, its tray icon, and its configured tunnels until shutdown.</summary>
[SupportedOSPlatform("windows")]
internal static partial class HelperHost
{
    public static async Task<int> RunAsync()
    {
        Log.Init("host.log", echo: !Console.IsErrorRedirected);
        var settings = new HostSettings(HostConfig.FilePath, Log.Warn);
        var initial = settings.Reload();
        await using var tunnels = new TunnelSet(Protocol.ControlPort);
        await using var helper = new Helper(url => OpenBrowser(url, settings.Current), tunnels.Statuses, IdleSeconds);
        if (!TryStart(helper))
            return 1;
        if (initial is not null)
            await tunnels.UpdateAsync(initial.Targets);
        InstalledPackage.CurrentUser.DeleteReplacedFiles();
        await RunUntilStoppedAsync(helper, settings, tunnels);
        Log.Info("helper stopping");
        return 0;
    }

    private static async Task RunUntilStoppedAsync(Helper helper, HostSettings settings, TunnelSet tunnels)
    {
        using var stopping = new CancellationTokenSource();
        var updates = new UpdateCheck(GitHubReleases.LatestAsync, AppVersion.Release, stopping.Token);
        // A queued tray callback can safely signal even after the host has shut down.
        var stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void RequestStop() => stopRequested.TrySetResult();
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            RequestStop();
        }
        Console.CancelKeyPress += OnCancel;
        try
        {
            using var tray = TrayIcon.Start(helper, new TrayUpdater(settings, updates, helper), RequestStop);
            Log.Info($"helper listening on 127.0.0.1:{helper.Port} (pid {Environment.ProcessId})");
            var maintenance = MaintainAsync(helper, settings, tunnels, updates, stopping.Token);
            await Task.WhenAny(stopRequested.Task, maintenance);
            await stopping.CancelAsync();
            await maintenance;
        }
        catch (OperationCanceledException) when (stopRequested.Task.IsCompleted)
        {
            // The tray closed or the console received Ctrl+C.
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }
    }

    private static bool TryStart(Helper helper)
    {
        try
        {
            helper.Start(Protocol.ControlPort);
            return true;
        }
        catch (SocketException e)
        {
            Log.Info($"127.0.0.1:{Protocol.ControlPort} is unavailable ({e.Message}); another helper is probably running");
            return false;
        }
    }

    private static async Task MaintainAsync(Helper helper, HostSettings settings, TunnelSet tunnels, UpdateCheck updates, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            _ = updates.RefreshAsync(DateTime.UtcNow);
            if (settings.Reload() is { } config)
                await tunnels.UpdateAsync(config.Targets);
            await helper.ExpireAsync();
            await Task.Delay(TimeSpan.FromSeconds(5), stopping);
        }
    }

    private static void OpenBrowser(string url, HostConfig config)
    {
        var info = config.BrowserCommand is [var browser, .. var arguments]
            ? new ProcessStartInfo(browser, arguments.Select(argument => argument.Replace("{url}", url)))
            : new ProcessStartInfo(url) { UseShellExecute = true };
        Process.Start(info)?.Dispose();
    }

    private static int? IdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? (int)(unchecked((uint)Environment.TickCount - info.Time) / 1000) : null;
    }

    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);
}
