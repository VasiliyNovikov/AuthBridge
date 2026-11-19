using AuthBridge.Common;

namespace AuthBridge.Host;

internal static class Ssh
{
    /// <summary>Passes one literal argument through the remote POSIX shell.</summary>
    public static string QuoteArgument(string value) => $"'{value.Replace("'", "'\\''")}'";

    public static string[] Arguments(IEnumerable<string> target, string command, params string[] options) =>
        ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=20", .. options, .. target, command];

    public static Task<ProcessResult> RunAsync(IEnumerable<string> target, string command, Stream? input = null) =>
        Processes.RunAsync("ssh", Arguments(target, command), input);
}
