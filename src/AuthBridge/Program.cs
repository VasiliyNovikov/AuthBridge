using System.CommandLine;
using System.ComponentModel;
using AuthBridge.Client;
using AuthBridge.Host;

namespace AuthBridge;

internal static class Program
{
    internal static Task<int> Main(string[] args) => RunAsync(() => Cli.InvokeAsync(CreateCommand(), args));

    /// <summary>Runs a command and reports an expected failure, such as an unreachable machine, without a stack trace.</summary>
    internal static async Task<int> RunAsync(Func<Task<int>> command)
    {
        try
        {
            return await command();
        }
        catch (Exception e) when (IsCommandError(e))
        {
            return Fail($"authbridge: {e.Message}");
        }
    }

    private static RootCommand CreateCommand()
    {
#if WINDOWS
        return HostCommands.CreateCommand();
#else
        if (OperatingSystem.IsLinux())
            return ClientCommands.CreateCommand();
        var root = new RootCommand();
        root.SetAction(_ => Fail("authbridge runs on Windows (helper) and Linux (client)."));
        return root;
#endif
    }

    internal static bool IsCommandError(Exception error) =>
        error is Win32Exception or IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or InvalidDataException;

    internal static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
