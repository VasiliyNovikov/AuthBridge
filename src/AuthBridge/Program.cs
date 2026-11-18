using System.ComponentModel;
using System.Reflection;
using AuthBridge.Client;
using AuthBridge.Host;

try
{
    if (args is ["--version"])
    {
        Console.WriteLine(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        return 0;
    }
    if (OperatingSystem.IsWindows())
        return await HostCommands.RunAsync(args);
    if (OperatingSystem.IsLinux())
        return await ClientCommands.RunAsync(args);
    return Program.Fail("authbridge runs on Windows (helper) and Linux (client).");
}
catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or HttpRequestException)
{
    return Program.Fail($"authbridge: {e.Message}");
}

internal partial class Program
{
    internal static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
