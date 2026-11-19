using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using AuthBridge.Common;

namespace AuthBridge.Host;

/// <summary>Keeps a reverse SSH tunnel from a Unix socket in the remote ~/.authbridge to the control port.</summary>
internal sealed class Tunnel(Target target, int controlPort, Func<string[], Process> startSsh) : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private Task<bool> _clientCheck = Task.FromResult(false);

    public TunnelStatus Status { get; private set; } = new(target.Name, "starting", "", DateTimeOffset.Now);

    public Tunnel(Target target, int controlPort) : this(target, controlPort, Ssh.Start) { }

    public void Dispose() => _stopping.Cancel();

    private static void StopSsh(Process ssh)
    {
        try
        {
            // The remote command removes its socket once its input closes.
            ssh.StandardInput.Close();
            if (!ssh.WaitForExit(5000))
                ssh.Kill();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or Win32Exception)
        {
            // ssh has already exited.
        }
    }

    public async Task RunAsync()
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!_stopping.IsCancellationRequested)
        {
            var socket = $"{target.RemoteDir}/bridge-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}.sock";
            var arguments = Ssh.Arguments(target.Ssh, $"cat >/dev/null; rm -f {PosixShell.Quote(socket)}",
                "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3",
                "-R", $"{socket}:127.0.0.1:{controlPort}");
            var uptime = Stopwatch.StartNew();
            string errors;
            SetStatus("connecting");
            try
            {
                errors = await ConnectOnceAsync(arguments);
            }
            catch (Win32Exception e)
            {
                errors = e.Message;
            }
            if (_stopping.IsCancellationRequested)
                break;

            SetStatus("disconnected", errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "ssh exited");
            if (errors.Contains("remote port forwarding failed"))
                await PrepareRemoteDirectoryAsync();
            delay = NextRetryDelay(delay, uptime.Elapsed);
            try
            {
                await Task.Delay(delay, _stopping.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        SetStatus("stopped");
    }

    internal static TimeSpan NextRetryDelay(TimeSpan previous, TimeSpan uptime) =>
        uptime > TimeSpan.FromMinutes(1) ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(Math.Min(previous.TotalSeconds * 2, 60));

    private async Task<string> ConnectOnceAsync(string[] arguments)
    {
        using var ssh = startSsh(arguments);
        // Register on this specific process, including when shutdown raced with Process.Start.
        using var stop = _stopping.Token.Register(() => StopSsh(ssh));
        var stderr = ssh.StandardError.ReadToEndAsync();
        var exited = ssh.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(5))) != exited && !_stopping.IsCancellationRequested)
        {
            SetStatus("connected");
            // A reconnect must not start a second upload while the previous probe or upload is still running.
            if (_clientCheck.IsCompletedSuccessfully && !_clientCheck.Result)
                _clientCheck = UpdateClientAsync();
        }
        await exited;
        return await stderr;
    }

    private async Task PrepareRemoteDirectoryAsync()
    {
        var directory = PosixShell.Quote(target.RemoteDir);
        try
        {
            var result = await Ssh.RunAsync(target.Ssh, $"mkdir -p {directory} && chmod 700 {directory}");
            result.OutputOrThrow("cannot prepare the remote socket directory");
        }
        catch (Exception e) when (e is Win32Exception or IOException)
        {
            Log.Warn($"tunnel {target.Name}: {e.Message}");
        }
    }

    private void SetStatus(string state, string detail = "")
    {
        lock (_gate)
        {
            if (state == Status.State && detail == Status.Detail)
                return;
            Status = Status with { State = state, Detail = detail, Since = DateTimeOffset.Now };
        }
        Log.Info($"tunnel {target.Name}: {state}{(detail.Length > 0 ? $" ({detail})" : "")}");
    }

    /// <summary>Replaces an older client on the remote machine with the one that matches this helper; it never
    /// downgrades, so that helpers of different versions do not undo each other's updates.</summary>
    private async Task<bool> UpdateClientAsync()
    {
        try
        {
            var machine = await RemoteClient.ProbeAsync(target.Ssh);
            var client = await machine.UpgradeIfOlderAsync(AppVersion.Release, arch => RemoteClient.UploadAsync(target.Ssh, arch));
            if (client != machine.Version)
                Log.Info($"tunnel {target.Name}: updated the client from {machine.Version} to {client}");
            lock (_gate)
                Status = Status with { Client = client ?? "not installed" };
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"tunnel {target.Name}: cannot check the client: {e.Message}");
            lock (_gate)
                Status = Status with { Client = "unknown" };
            return false;
        }
    }
}

/// <summary>Runs one tunnel per configured target; a tunnel restarts when its settings change.</summary>
internal sealed class TunnelSet(int controlPort) : IDisposable
{
    private readonly Dictionary<string, Tunnel> _tunnels = [];

    public TunnelStatus[] Statuses()
    {
        lock (_tunnels)
            return [.. _tunnels.Values.Select(tunnel => tunnel.Status)];
    }

    public void Update(IEnumerable<Target> targets)
    {
        var wanted = targets.DistinctBy(Key).ToDictionary(Key);
        lock (_tunnels)
        {
            foreach (var key in _tunnels.Keys.Except(wanted.Keys).ToList())
            {
                _tunnels.Remove(key, out var tunnel);
                tunnel!.Dispose();
            }
            foreach (var (key, target) in wanted.Where(entry => !_tunnels.ContainsKey(entry.Key)))
            {
                var tunnel = new Tunnel(target, controlPort);
                _tunnels.Add(key, tunnel);
                _ = tunnel.RunAsync();
            }
        }
    }

    public void Dispose() => Update([]);

    private static string Key(Target target) => string.Join('\n', [target.Name, target.RemoteDir, .. target.Ssh]);
}
