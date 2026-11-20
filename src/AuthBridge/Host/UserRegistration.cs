using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AuthBridge.Host;

[SupportedOSPlatform("windows")]
internal static partial class UserRegistration
{
    public static void Register(InstalledPackage installation)
    {
        var command = installation.LogonCommand;
        using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            run.SetValue("authbridge", command);
        Console.WriteLine($"The helper starts at logon: {command}");

        using var environment = Registry.CurrentUser.CreateSubKey("Environment");
        // Unexpanded, so that entries such as %USERPROFILE%\bin keep working.
        var path = environment.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var updatedPath = installation.IncludeInUserPath(path);
        if (updatedPath == path)
        {
            Console.WriteLine($"{installation.Location} is already on your PATH");
            return;
        }
        var kind = path.Length > 0 ? environment.GetValueKind("Path") : RegistryValueKind.ExpandString;
        environment.SetValue("Path", updatedPath, kind);
        // Tells running programs such as Explorer, so that new terminals see the updated PATH.
        SendMessageTimeout(HwndBroadcast, WmSettingChange, 0, "Environment", SmtoAbortIfHung, 5000, out _);
        Console.WriteLine($"Added {installation.Location} to your PATH; terminals started from now on can run 'authbridge'");
    }

    private const nint HwndBroadcast = 0xFFFF;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeout(nint window, uint message, nint wParam, string lParam, uint flags, uint timeout, out nint result);
}
