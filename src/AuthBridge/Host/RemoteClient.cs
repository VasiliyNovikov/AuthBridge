using AuthBridge.Common;

namespace AuthBridge.Host;

/// <summary>The Linux client on a remote machine, in ~/.local/bin/authbridge.</summary>
internal static class RemoteClient
{
    /// <summary>A remote machine's architecture, home directory, and client version, which is null without a client.</summary>
    public sealed record Machine(string Architecture, string Home, string? Version)
    {
        /// <summary>A machine without a client needs an explicit install. Never replace a newer client's files.</summary>
        public async Task<string?> EnsureVersionAsync(string bundledVersion, Func<string, Task<string>> upload, bool installIfMissing = false)
        {
            if (Version is null ? !installIfMissing : !AppVersion.IsNewer(bundledVersion, Version))
                return Version;
            return await upload(Architecture);
        }

        internal static Machine Parse(string output)
        {
            var lines = output.TrimEnd('\r', '\n').Split('\n', StringSplitOptions.TrimEntries);
            if (lines.Length < 2 || lines[0].Length == 0 || !lines[1].StartsWith('/'))
                throw new IOException("cannot query the remote machine: expected its architecture and absolute home directory");
            var architecture = lines[0] switch
            {
                "x86_64" => "x64", "aarch64" or "arm64" => "arm64",
                var name => throw new IOException($"unsupported Linux architecture '{name}'; expected x86_64 or aarch64"),
            };
            return new(architecture, lines[1], lines.Length > 2 ? AppVersion.WithoutMetadata(lines[2]) : null);
        }
    }

    public static async Task<Machine> ProbeAsync(IEnumerable<string> ssh, CancellationToken cancel = default)
    {
        // A client that cannot tell its version counts as version 0, which any release replaces.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var probe = await Ssh.RunAsync(ssh, "uname -m; printf '%s\\n' \"$HOME\"; " +
            "if [ -x ~/.local/bin/authbridge ]; then ~/.local/bin/authbridge --version 2>/dev/null || echo 0; fi", cancel: deadline.Token);
        return Machine.Parse(probe.OutputOrThrow("cannot query the remote machine"));
    }

    public static async Task<string?> CheckAsync(Target target, CancellationToken cancel)
    {
        var machine = await ProbeAsync(target.Ssh, cancel);
        var version = await machine.EnsureVersionAsync(AppVersion.Release, arch => UploadAsync(target.Ssh, arch, cancel));
        if (version != machine.Version)
            Log.Info($"tunnel {target.Name}: client version changed from {machine.Version} to {version}");
        return version;
    }

    public static async Task<string?> WaitForTunnelAsync(string name, Func<Task<PingResult?>> ping, Func<string, Task<ProcessResult>> run, TimeSpan interval)
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(interval);
            if (StatusCommand(await ping(), name) is { } command && await run(command) is { ExitCode: 0 } ready)
                return ready.Output;
        }
        return null;
    }

    private static string? StatusCommand(PingResult? local, string name)
    {
        var tunnel = local?.Tunnels.FirstOrDefault(t => t.Name == name && t.State == "connected");
        if (tunnel is null)
            return null;
        const string status = "~/.local/bin/authbridge --status";
        // Older helpers don't publish the socket. At least require their local tunnel to be connected.
        return tunnel.Socket is { Length: > 0 } socket ? $"AUTHBRIDGE_SOCKET={PosixShell.Quote(socket)} {status}" : status;
    }

    /// <summary>After an update, wait for connected clients to report their version; offline clients catch up later.</summary>
    public static async Task<PingResult?> WaitForChecksAsync(Func<Task<PingResult?>> ping, TimeSpan interval)
    {
        PingResult? info = null;
        for (var attempt = 0; attempt < 45; attempt++)
        {
            await Task.Delay(interval);
            info = await ping();
            if (info is null || info.Tunnels.All(tunnel => tunnel.State == "disconnected" || (tunnel.State == "connected" && !string.IsNullOrEmpty(tunnel.Client))))
                break;
        }
        return info;
    }

    /// <summary>Installs the client that matches this helper.</summary>
    public static async Task<string> UploadAsync(IEnumerable<string> ssh, string architecture, CancellationToken cancel = default)
    {
        var client = Path.Combine(AppContext.BaseDirectory, $"authbridge-linux-{architecture}");
        if (!File.Exists(client))
            throw new IOException($"no client for {architecture}: {client} is missing");
        await using var binary = File.OpenRead(client);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var upload = await Ssh.RunAsync(ssh, UploadCommand(binary.Length, AppVersion.Release), binary, deadline.Token);
        var installed = upload.OutputOrThrow("upload failed").Trim();
        if (installed.Length == 0 || AppVersion.IsNewer(AppVersion.Release, installed))
            throw new IOException($"remote install did not confirm client version {AppVersion.Release}");
        return installed;
    }

    // EOF alone does not mean the upload completed. Verify the staged file, then serialize the
    // version comparison and rename so an older Windows machine cannot win a concurrent install.
    internal static string UploadCommand(long length, string version) => $$"""
        set -eu
        bin="$HOME/.local/bin"
        state="$HOME/.authbridge"
        mkdir -p "$state" "$bin"
        chmod 700 "$state"
        t=$(mktemp "$bin/.authbridge.XXXXXX")
        trap 'rm -f "$t"' EXIT
        cat > "$t"
        [ "$(wc -c < "$t")" -eq {{length}} ] || { echo 'incomplete client upload' >&2; exit 1; }
        chmod 755 "$t"
        candidate=$("$t" --version)
        candidate=${candidate%%+*}
        [ "$candidate" = {{PosixShell.Quote(version)}} ] || { echo 'unexpected uploaded client version' >&2; exit 1; }
        exec 9>"$state/client-install.lock"
        flock -w 30 9 || { echo 'cannot lock the remote installation' >&2; exit 1; }
        dest="$bin/authbridge"
        if [ -x "$dest" ]; then
            current=$("$dest" --version 2>/dev/null || echo 0)
            current=${current%%+*}
            if printf '%s\n' "${candidate%%-*}" "${current%%-*}" | LC_ALL=C sort -V -C; then
                printf '%s\n' "$current"
                exit 0
            else
                rc=$?
                [ "$rc" -eq 1 ] || exit "$rc"
            fi
        fi
        mv -fT "$t" "$dest"
        printf '%s\n' "$candidate"
        """.ReplaceLineEndings("\n");
}
