namespace AuthBridge.Host;

/// <summary>The Linux client on a remote machine, in ~/.local/bin/authbridge.</summary>
internal static class RemoteClient
{
    /// <summary>A remote machine's architecture, home directory, and client version, which is null without a client.</summary>
    public sealed record Machine(string Architecture, string Home, string? Version);

    public static async Task<Machine> ProbeAsync(IEnumerable<string> ssh)
    {
        // A client that cannot tell its version counts as version 0, which any release replaces.
        var probe = await Ssh.RunAsync(ssh, "uname -m; echo \"$HOME\"; " +
            "if [ -x ~/.local/bin/authbridge ]; then ~/.local/bin/authbridge --version 2>/dev/null || echo 0; fi");
        var lines = probe.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (probe.ExitCode != 0 || lines.Length < 2 || !lines[1].StartsWith('/'))
            throw new IOException($"cannot query the remote machine: {probe.Error.Trim()}");
        var architecture = lines[0] switch { "x86_64" => "x64", "aarch64" or "arm64" => "arm64", var machine => machine };
        return new(architecture, lines[1], lines.Length > 2 ? Updater.WithoutMetadata(lines[2]) : null);
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
        if (upload.ExitCode != 0)
            throw new IOException($"upload failed: {upload.Error.Trim()}");
    }
}
