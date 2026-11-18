using System.Runtime.Versioning;
using AuthBridge.Common;

namespace AuthBridge.Client;

/// <summary>Runs tools whose browser sign-in needs a desktop session, pretending one while the Windows helper is connected.</summary>
[SupportedOSPlatform("linux")]
internal static class Wrappers
{
    private static readonly string NuGetProviderDir = Path.Combine(Posix.AccountHome(), ".nuget", "plugins", "netcore", "CredentialProvider.Microsoft");

    public static string NuGetProviderDll => Path.Combine(NuGetProviderDir, "CredentialProvider.Microsoft.dll");

    /// <summary>Runs Git Credential Manager; it offers browser sign-in only in desktop sessions.</summary>
    public static async Task<int> GcmAsync(string gcm, string[] args)
    {
        var environment = new Dictionary<string, string>();
        if (await IsConnectedAsync(TimeSpan.FromSeconds(2)))
        {
            environment["GCM_MSAUTH_FLOW"] = "system";
            if (!IsSet("DISPLAY") && !IsSet("WAYLAND_DISPLAY"))
            {
                // Nothing connects to this display; GUI prompts stay off so GCM keeps using the terminal otherwise.
                environment["WAYLAND_DISPLAY"] = "authbridge";
                environment["GCM_GUI_PROMPT"] = "false";
            }
            AddBrowser(environment);
        }
        Posix.Exec([gcm, .. args], environment);
        return 1;
    }

    /// <summary>Runs the Azure Artifacts credential provider as a NuGet plugin.</summary>
    public static async Task<int> NuGetPluginAsync(string[] args)
    {
        // The MSAL broker cannot load on Linux, and while it is enabled even cached tokens fail.
        var environment = new Dictionary<string, string>
        {
            ["NUGET_CREDENTIALPROVIDER_MSAL_ALLOW_BROKER"] = Environment.GetEnvironmentVariable("NUGET_CREDENTIALPROVIDER_MSAL_ALLOW_BROKER") ?? "false",
        };
        // MSAL.NET offers browser sign-in only when DISPLAY is set. NuGet expects a plugin handshake within 5 seconds.
        if (!IsSet("DISPLAY") && await IsConnectedAsync(TimeSpan.FromSeconds(1)))
        {
            environment["DISPLAY"] = ":authbridge";
            AddBrowser(environment);
        }
        var executable = Path.Combine(NuGetProviderDir, "CredentialProvider.Microsoft");
        string[] provider = File.Exists(executable) ? [executable] : [Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", NuGetProviderDll];
        Posix.Exec([.. provider, .. args], environment);
        return 1;
    }

    private static bool IsSet(string variable) => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable));

    private static void AddBrowser(Dictionary<string, string> environment)
    {
        if (!IsSet("BROWSER"))
            environment["BROWSER"] = Environment.ProcessPath!;
    }

    private static async Task<bool> IsConnectedAsync(TimeSpan timeout)
    {
        using var bridge = await Bridge.FindAsync(timeout);
        return bridge is not null;
    }
}
