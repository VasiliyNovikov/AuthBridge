using System.Reflection;

namespace AuthBridge.Common;

internal static class AppVersion
{
    public static string Informational { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    public static string Release { get; } = WithoutMetadata(Informational);

    public static string WithoutMetadata(string version) => version.Split('+')[0].Trim();

    /// <summary>Compares release numbers, ignoring prerelease labels and build metadata.</summary>
    public static bool IsNewer(string candidate, string installed) => Numbers(candidate) > Numbers(installed);

    private static Version Numbers(string version) => Version.TryParse(version.Split('-', '+')[0], out var numbers) ? numbers : new();
}
