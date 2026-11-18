using AuthBridge.Client;

namespace AuthBridge.Common;

internal static class Paths
{
    /// <summary>The account's ~/.authbridge; on Linux independent of $HOME, which tools sometimes override.</summary>
    public static string StateDir { get; } = Path.Combine(Home(), ".authbridge");

    public static void EnsureStateDir()
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(StateDir);
        else
            Directory.CreateDirectory(StateDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string Home()
    {
        if (OperatingSystem.IsLinux())
            return Posix.AccountHome();
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
