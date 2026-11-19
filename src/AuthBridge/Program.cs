using System.ComponentModel;
using AuthBridge.Client;
using AuthBridge.Common;
using AuthBridge.Host;

namespace AuthBridge;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--version"])
            {
                Console.WriteLine(AppVersion.Informational);
                return 0;
            }
            if (OperatingSystem.IsWindows())
                return await HostCommands.RunAsync(args);
            if (OperatingSystem.IsLinux())
                return await ClientCommands.RunAsync(args);
            return Fail("authbridge runs on Windows (helper) and Linux (client).");
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
