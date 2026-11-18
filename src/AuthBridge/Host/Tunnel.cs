using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using AuthBridge.Common;

namespace AuthBridge.Host;

internal static class Ssh
{
    public static string[] Arguments(IEnumerable<string> target, string command, params string[] options) =>
        ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=20", .. options, .. target, command];

    public static Task<(int ExitCode, string Output, string Error)> RunAsync(IEnumerable<string> target, string command, Stream? input = null) =>
        Processes.RunAsync("ssh", Arguments(target, command), input);
}

/// <summary>Keeps a reverse SSH tunnel from a Unix socket in the remote ~/.authbridge to the control port.</summary>
internal sealed class Tunnel(Target target, int controlPort) : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private Process? _ssh;
    private bool _clientChecked;

    public TunnelStatus Status { get; private set; } = new(target.Name, "starting", "", DateTimeOffset.Now);

    public void Start() => _ = RunAsync();

    public void Dispose()
    {
        _stopping.Cancel();
        try
        {
            // The remote command removes its socket once its input closes.
            _ssh?.StandardInput.Close();
            if (_ssh?.WaitForExit(5000) == false)
                _ssh.Kill();
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
            var arguments = Ssh.Arguments(target.Ssh, $"cat >/dev/null; rm -f '{socket}'",
                "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3",
                "-R", $"{socket}:127.0.0.1:{controlPort}");
            var uptime = Stopwatch.StartNew();
            string errors;
            SetStatus("connecting");
            try
            {
                using var ssh = _ssh = Process.Start(Processes.StartInfo("ssh", arguments))!;
                var stderr = ssh.StandardError.ReadToEndAsync();
                var exited = ssh.WaitForExitAsync();
                if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(5))) != exited)
                {
                    SetStatus("connected");
                    if (!_clientChecked)
                        _ = UpdateClientAsync();
                }
                await exited;
                errors = await stderr;
            }
            catch (Win32Exception e)
            {
                errors = e.Message;
            }
            if (_stopping.IsCancellationRequested)
                break;

            SetStatus("disconnected", errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "ssh exited");
            if (errors.Contains("remote port forwarding failed"))
                await Ssh.RunAsync(target.Ssh, $"mkdir -p '{target.RemoteDir}' && chmod 700 '{target.RemoteDir}'");
            delay = uptime.Elapsed > TimeSpan.FromMinutes(1) ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
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
            if (machine.Version is { } version && Updater.IsNewer(Updater.Current, version))
            {
                await RemoteClient.UploadAsync(target.Ssh, machine.Architecture);
                Log.Info($"tunnel {target.Name}: updated the client from {version} to {Updater.Current}");
                client = Updater.Current;
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
