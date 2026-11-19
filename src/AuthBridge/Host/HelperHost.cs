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
        using var tunnels = new TunnelSet(Protocol.ControlPort);
        await using var helper = new Helper(OpenBrowser, tunnels.Statuses, IdleSeconds);
        try
        {
            helper.Start(Protocol.ControlPort);
        }
        catch (SocketException e)
        {
            Log.Info($"127.0.0.1:{Protocol.ControlPort} is unavailable ({e.Message}); another helper is probably running");
            return 1;
        }
        InstalledPackage.CurrentUser.DeleteReplacedFiles();
        var updates = new UpdateCheck(GitHubReleases.LatestAsync, AppVersion.Release);
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
            using var tray = TrayIcon.Start(tunnels.Statuses, () => updates.Available, RequestStop);
            using var stopping = new CancellationTokenSource();
            Log.Info($"helper listening on 127.0.0.1:{helper.Port} (pid {Environment.ProcessId})");
            var maintenance = MaintainAsync(helper, tunnels, updates, stopping.Token);
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
        Log.Info("helper stopping");
        return 0;
    }

    private static async Task MaintainAsync(Helper helper, TunnelSet tunnels, UpdateCheck updates, CancellationToken stopping)
    {
        var lastConfigWrite = DateTime.MinValue;
        while (!stopping.IsCancellationRequested)
        {
            _ = updates.RefreshAsync(DateTime.UtcNow, stopping);
            var configWrite = File.GetLastWriteTimeUtc(HostConfig.FilePath);
            if (configWrite != lastConfigWrite)
            {
                tunnels.Update(HostConfig.Load().Targets);
                lastConfigWrite = configWrite;
            }
            await helper.ExpireAsync();
            await Task.Delay(TimeSpan.FromSeconds(5), stopping);
        }
    }

    private static void OpenBrowser(string url)
    {
        var info = HostConfig.Load().BrowserCommand is [var browser, .. var arguments]
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
