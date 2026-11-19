using System.CommandLine;
using System.ComponentModel;
using AuthBridge.Client;
using AuthBridge.Host;

namespace AuthBridge;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            RootCommand root;
            if (OperatingSystem.IsWindows())
                root = HostCommands.CreateCommand();
            else if (OperatingSystem.IsLinux())
                root = ClientCommands.CreateCommand();
            else
            {
                root = new RootCommand();
                root.SetAction(_ => Fail("authbridge runs on Windows (helper) and Linux (client)."));
            }
            return await Cli.InvokeAsync(root, args);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            return Fail($"authbridge: {e.Message}");
        }
    }

    internal static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
