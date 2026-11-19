namespace AuthBridge.Common;

internal static class PosixShell
{
    /// <summary>One literal argument in a POSIX shell command, including an empty value or embedded apostrophes.</summary>
    public static string Quote(string value) => $"'{value.Replace("'", "'\\''")}'";
}
