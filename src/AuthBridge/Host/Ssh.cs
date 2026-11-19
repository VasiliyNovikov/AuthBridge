using System.Diagnostics;
using AuthBridge.Common;

namespace AuthBridge.Host;

internal static class Ssh
{
    public static string[] Arguments(IEnumerable<string> target, string command, params string[] options) =>
        ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=20", .. options, .. target, command];

    public static Process Start(string[] arguments) =>
        Process.Start(Processes.StartInfo("ssh", arguments)) ?? throw new IOException("could not start ssh");

    public static Task<ProcessResult> RunAsync(IEnumerable<string> target, string command, Stream? input = null) =>
        Processes.RunAsync("ssh", Arguments(target, command), input);
}
