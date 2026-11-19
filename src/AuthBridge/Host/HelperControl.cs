using AuthBridge.Common;

namespace AuthBridge.Host;

/// <summary>The start and status commands use a live ping, not the mere existence of a process.</summary>
internal sealed class HelperControl(Func<Task<PingResult?>> ping, TextWriter output, TextWriter error)
{
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
