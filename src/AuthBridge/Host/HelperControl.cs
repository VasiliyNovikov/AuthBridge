using System.ComponentModel;
using System.Diagnostics;
using AuthBridge.Common;

namespace AuthBridge.Host;

/// <summary>The start and status commands use a live ping, not the mere existence of a process.</summary>
internal sealed class HelperControl(Func<Task<PingResult?>> ping, TextWriter output, TextWriter error)
{
    public static bool StopProcess(int pid, Func<int, bool> requestExit, TimeSpan timeout, bool requireApproval = false)
    {
        using var process = FindProcess(pid);
        if (process is null || process.HasExited)
            return false;
        var approved = false;
        try
        {
            approved = requestExit(pid);
            if (!approved && requireApproval)
                return false;
            if (!approved || !process.WaitForExit(timeout))
                ForceStop(process, timeout);
            return true;
        }
        catch (Exception e) when ((e is InvalidOperationException or Win32Exception) && process.HasExited)
        {
            return approved; // An approved shutdown can finish while we are waiting.
        }
    }

    private static Process? FindProcess(int pid)
    {
        try
        {
            return Process.GetProcessById(pid);
        }
        catch (ArgumentException) when (pid > 0)
        {
            return null; // It exited between the ping and opening the process.
        }
    }

    private static void ForceStop(Process process, TimeSpan timeout)
    {
        _ = process.SafeHandle; // Pin the Windows process identity across the image check and termination.
        var image = process.MainModule?.FileName;
        if (process.Id == Environment.ProcessId || !(InstalledPackage.CurrentUser.ContainsExecutable(image)
            || new InstalledPackage(AppContext.BaseDirectory).ContainsExecutable(image)))
            throw new IOException($"refusing to terminate unrecognized helper process {process.Id}");
        // The tray updater is a child of the old helper and must survive its shutdown.
        process.Kill();
        if (!process.WaitForExit(timeout))
            throw new IOException($"helper process {process.Id} did not exit");
    }

    public async Task<int> StartAsync(Action launch, string logPath, TimeSpan pollInterval)
    {
        if (await ping() is { } running)
        {
            output.WriteLine($"authbridge helper is running (pid {running.Pid})");
            return 0;
        }
        launch();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(pollInterval);
            if (await ping() is not { } started)
                continue;
            output.WriteLine($"authbridge helper started (pid {started.Pid})");
            return 0;
        }
        error.WriteLine($"authbridge helper did not start; see {logPath}");
        return 1;
    }

    public async Task<int> StatusAsync(int port)
    {
        if (await ping() is not { } info)
        {
            error.WriteLine("authbridge helper: not running");
            return 1;
        }
        output.WriteLine($"authbridge helper: running (pid {info.Pid}, 127.0.0.1:{port}, {info.Sessions} relay session(s))");
        foreach (var tunnel in info.Tunnels)
        {
            var detail = tunnel.Detail.Length > 0 ? $" - {tunnel.Detail}" : "";
            var client = tunnel.Client.Length > 0 ? $", client {tunnel.Client}" : "";
            output.WriteLine($"  tunnel {tunnel.Name}: {tunnel.State} since {tunnel.Since.LocalDateTime:yyyy-MM-dd HH:mm:ss}{client}{detail}");
        }
        return 0;
    }
}
