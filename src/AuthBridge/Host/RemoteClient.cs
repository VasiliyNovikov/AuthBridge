using AuthBridge.Common;

namespace AuthBridge.Host;

/// <summary>The Linux client on a remote machine, in ~/.local/bin/authbridge.</summary>
internal static class RemoteClient
{
    /// <summary>A remote machine's architecture, home directory, and client version, which is null without a client.</summary>
    public sealed record Machine(string Architecture, string Home, string? Version)
    {
        internal static Machine Parse(string output)
        {
            var lines = output.TrimEnd('\r', '\n').Split('\n', StringSplitOptions.TrimEntries);
            if (lines.Length < 2 || lines[0].Length == 0 || !lines[1].StartsWith('/'))
                throw new IOException("cannot query the remote machine: expected its architecture and absolute home directory");
            var architecture = lines[0] switch { "x86_64" => "x64", "aarch64" or "arm64" => "arm64", var machine => machine };
            return new(architecture, lines[1], lines.Length > 2 ? AppVersion.WithoutMetadata(lines[2]) : null);
        }
    }

    public static async Task<Machine> ProbeAsync(IEnumerable<string> ssh)
    {
        // A client that cannot tell its version counts as version 0, which any release replaces.
        var probe = await Ssh.RunAsync(ssh, "uname -m; echo \"$HOME\"; " +
            "if [ -x ~/.local/bin/authbridge ]; then ~/.local/bin/authbridge --version 2>/dev/null || echo 0; fi");
        return Machine.Parse(probe.OutputOrThrow("cannot query the remote machine"));
    }

    /// <summary>After an update, wait for connected clients to report their version; offline clients catch up later.</summary>
    public static async Task<PingResult?> WaitForChecksAsync(Func<Task<PingResult?>> ping, TimeSpan interval)
    {
        PingResult? info = null;
        for (var attempt = 0; attempt < 45; attempt++)
        {
            await Task.Delay(interval);
            info = await ping();
            if (info is null || info.Tunnels.All(tunnel => tunnel.State == "disconnected" || (tunnel.State == "connected" && tunnel.Client.Length > 0)))
                break;
        }
        return info;
    }

    /// <summary>Installs the client that matches this helper.</summary>
    public static async Task UploadAsync(IEnumerable<string> ssh, string architecture)
    {
        var client = Path.Combine(AppContext.BaseDirectory, $"authbridge-linux-{architecture}");
        if (!File.Exists(client))
            throw new IOException($"no client for {architecture}: {client} is missing");
        await using var binary = File.OpenRead(client);
        // Renaming a complete upload into place keeps running clients and concurrent uploads intact.
        var upload = await Ssh.RunAsync(ssh, "mkdir -p ~/.authbridge ~/.local/bin && chmod 700 ~/.authbridge && " +
            "t=$(mktemp ~/.local/bin/.authbridge.XXXXXX) && trap 'rm -f \"$t\"' EXIT && " +
            "cat > \"$t\" && chmod 755 \"$t\" && mv -f \"$t\" ~/.local/bin/authbridge", binary);
        upload.OutputOrThrow("upload failed");
    }
}
