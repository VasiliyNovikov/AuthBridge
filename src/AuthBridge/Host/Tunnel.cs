using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using AuthBridge.Common;

namespace AuthBridge.Host;

/// <summary>Keeps a reverse SSH tunnel from a Unix socket in the remote ~/.authbridge to the control port.</summary>
internal sealed class Tunnel(Target target, int controlPort) : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private bool _clientChecked;

    public TunnelStatus Status { get; private set; } = new(target.Name, "starting", "", DateTimeOffset.Now);

    public void Start() => _ = RunAsync();

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

    private async Task RunAsync()
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!_stopping.IsCancellationRequested)
        {
            var socket = $"{target.RemoteDir}/bridge-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}.sock";
            var arguments = Ssh.Arguments(target.Ssh, $"cat >/dev/null; rm -f {Ssh.QuoteArgument(socket)}",
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
        using var ssh = Process.Start(Processes.StartInfo("ssh", arguments))!;
        // Register on this specific process, including when shutdown raced with Process.Start.
        using var stop = _stopping.Token.Register(() => StopSsh(ssh));
        var stderr = ssh.StandardError.ReadToEndAsync();
        var exited = ssh.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(5))) != exited && !_stopping.IsCancellationRequested)
        {
            SetStatus("connected");
            if (!_clientChecked)
                _ = UpdateClientAsync();
        }
        await exited;
        return await stderr;
    }

    private async Task PrepareRemoteDirectoryAsync()
    {
        var directory = Ssh.QuoteArgument(target.RemoteDir);
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
    private async Task UpdateClientAsync()
    {
        string client;
        try
        {
            var machine = await RemoteClient.ProbeAsync(target.Ssh);
            client = machine.Version ?? "not installed";
            if (machine.Version is { } version && AppVersion.IsNewer(AppVersion.Release, version))
            {
                await RemoteClient.UploadAsync(target.Ssh, machine.Architecture);
                Log.Info($"tunnel {target.Name}: updated the client from {version} to {AppVersion.Release}");
                client = AppVersion.Release;
            }
            _clientChecked = true;
        }
        catch (Exception e)
        {
            Log.Warn($"tunnel {target.Name}: cannot check the client: {e.Message}");
            client = "unknown";
        }
        lock (_gate)
            Status = Status with { Client = client };
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
                tunnel.Start();
            }
        }
    }

    public void Dispose() => Update([]);

    private static string Key(Target target) => string.Join('\n', [target.Name, target.RemoteDir, .. target.Ssh]);
}
