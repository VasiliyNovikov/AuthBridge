using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using AuthBridge.Common;

namespace AuthBridge.Host;

/// <summary>Keeps a reverse SSH tunnel from a Unix socket in the remote ~/.authbridge to the control port.</summary>
internal sealed class Tunnel(Target target, int controlPort, Func<string[], Process> startSsh, Func<CancellationToken, Task<string?>> checkClient) : IAsyncDisposable
{
    internal const string ReadyMarker = "authbridge: reverse forward ready";
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private volatile TunnelStatus _status = new(target.Name, "starting", "", DateTimeOffset.Now);
    private Task? _lifetime, _shutdown;

    public TunnelStatus Status => _status;

    public Tunnel(Target target, int controlPort) : this(target, controlPort, Ssh.Start, cancel => RemoteClient.CheckAsync(target, cancel)) { }

    public Task RunAsync() => _lifetime ??= RunLoopAsync();

    public ValueTask DisposeAsync() => new(_shutdown ??= StopAsync());

    private async Task StopAsync()
    {
        await _stopping.CancelAsync();
        try
        {
            if (_lifetime is not null)
                await _lifetime;
        }
        finally
        {
            _stopping.Dispose();
        }
    }

    private static void StopSsh(Process ssh)
    {
        try
        {
            // The remote command removes its socket once its input closes.
            ssh.StandardInput.Close();
        }
        catch (IOException)
        {
            // The input pipe already closed. Still wait for the process itself.
        }
        try
        {
            if (!ssh.WaitForExit(5000))
                ssh.Kill();
        }
        catch (Exception e) when ((e is InvalidOperationException or Win32Exception) && ssh.HasExited)
        {
            // ssh exited while we were stopping it.
        }
    }

    private async Task RunLoopAsync()
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!_stopping.IsCancellationRequested)
        {
            var socket = $"{target.RemoteDir}/bridge-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}.sock";
            var path = PosixShell.Quote(socket);
            var arguments = Ssh.Arguments(target.Ssh, $"test -S {path} || exit 1; echo '{ReadyMarker}'; cat >/dev/null; rm -f {path}",
                "-o", "ExitOnForwardFailure=yes",
                "-R", $"{socket}:127.0.0.1:{controlPort}");
            var uptime = Stopwatch.StartNew();
            string errors;
            SetStatus("connecting");
            try
            {
                errors = await ConnectOnceAsync(arguments, socket);
            }
            catch (Exception e) when (e is Win32Exception or IOException)
            {
                errors = e.Message;
            }
            if (_stopping.IsCancellationRequested)
                break;

            SetStatus("disconnected", errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "ssh exited");
            if (errors.Contains("forwarding failed", StringComparison.OrdinalIgnoreCase))
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

    private async Task<string> ConnectOnceAsync(string[] arguments, string socket)
    {
        using var ssh = startSsh(arguments);
        // Register on this specific process, including when shutdown raced with Process.Start.
        using var stop = _stopping.Token.Register(() => StopSsh(ssh));
        var stderr = ssh.StandardError.ReadToEndAsync();
        using var connected = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        var check = Task.CompletedTask;
        try
        {
            while (await ssh.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line != ReadyMarker || _stopping.IsCancellationRequested)
                    continue;
                SetStatus("connected", socket: socket);
                check = UpdateClientAsync(connected.Token);
                await ssh.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
                break;
            }
            await ssh.WaitForExitAsync();
        }
        finally
        {
            await connected.CancelAsync();
            await check;
        }
        return await stderr;
    }

    private async Task PrepareRemoteDirectoryAsync()
    {
        var directory = PosixShell.Quote(target.RemoteDir);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            var result = await Ssh.RunAsync(target.Ssh, $"mkdir -p {directory} && chmod 700 {directory}", cancel: deadline.Token);
            result.OutputOrThrow("cannot prepare the remote socket directory");
        }
        catch (Exception e) when (e is Win32Exception or IOException or OperationCanceledException)
        {
            Log.Warn($"tunnel {target.Name}: {e.Message}");
        }
    }

    private void SetStatus(string state, string detail = "", string? socket = null)
    {
        lock (_gate)
        {
            if (state == Status.State && detail == Status.Detail)
                return;
            _status = Status with { State = state, Detail = detail, Since = DateTimeOffset.Now, Socket = socket, Client = state == "connecting" ? "" : Status.Client };
        }
        Log.Info($"tunnel {target.Name}: {state}{(detail.Length > 0 ? $" ({detail})" : "")}");
    }

    /// <summary>Replaces an older client on the remote machine with the one that matches this helper; it never
    /// downgrades, so that helpers of different versions do not undo each other's updates.</summary>
    private async Task UpdateClientAsync(CancellationToken cancel)
    {
        try
        {
            var client = await checkClient(cancel);
            lock (_gate)
                _status = Status with { Client = client ?? "not installed" };
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // This connection ended; a new one will probe its client again.
        }
        catch (Exception e) when (Program.IsCommandError(e))
        {
            Log.Warn($"tunnel {target.Name}: cannot check the client: {e.Message}");
            lock (_gate)
                _status = Status with { Client = "unknown" };
        }
    }
}

/// <summary>Runs one tunnel per configured target; a tunnel restarts when its settings change.</summary>
internal sealed class TunnelSet(int controlPort) : IAsyncDisposable
{
    private readonly Dictionary<string, Tunnel> _tunnels = [];

    public TunnelStatus[] Statuses()
    {
        lock (_tunnels)
            return [.. _tunnels.Values.Select(tunnel => tunnel.Status)];
    }

    public async Task UpdateAsync(IEnumerable<Target> targets)
    {
        var wanted = targets.DistinctBy(Key).ToDictionary(Key);
        var removed = new List<Tunnel>();
        lock (_tunnels)
        {
            foreach (var key in _tunnels.Keys.Except(wanted.Keys).ToList())
            {
                _tunnels.Remove(key, out var tunnel);
                removed.Add(tunnel!);
            }
        }
        await Task.WhenAll(removed.Select(tunnel => tunnel.DisposeAsync().AsTask()));
        lock (_tunnels)
        {
            foreach (var (key, target) in wanted.Where(entry => !_tunnels.ContainsKey(entry.Key)))
            {
                var tunnel = new Tunnel(target, controlPort);
                _tunnels.Add(key, tunnel);
                _ = tunnel.RunAsync();
            }
        }
    }

    public ValueTask DisposeAsync() => new(UpdateAsync([]));

    private static string Key(Target target) => string.Join('\n', [target.Name, target.RemoteDir, .. target.Ssh]);
}
