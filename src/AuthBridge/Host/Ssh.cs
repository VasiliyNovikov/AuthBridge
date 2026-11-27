using System.Diagnostics;
using AuthBridge.Common;

namespace AuthBridge.Host;

internal static class Ssh
{
    /// <summary>Enough for ssh to connect, which ConnectTimeout limits, and run a short command.</summary>
    public static readonly TimeSpan ShortCommandTimeout = TimeSpan.FromSeconds(45);

    public static string[] Arguments(IEnumerable<string> target, string command, params string[] options) =>
        ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=20", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3",
            .. options, .. target, $"sh -c {PosixShell.Quote(command)}"];

    public static Process Start(string[] arguments) =>
        Process.Start(Processes.StartInfo("ssh", arguments)) ?? throw new IOException("could not start ssh");

    /// <summary>Runs a command on the target, cancelling it after <paramref name="timeout"/> if one is given.</summary>
    public static async Task<ProcessResult> RunAsync(IEnumerable<string> target, string command, Stream? input = null,
        TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(timeout ?? Timeout.InfiniteTimeSpan);
        return await Processes.RunAsync("ssh", Arguments(target, command), input, deadline.Token);
    }
}
