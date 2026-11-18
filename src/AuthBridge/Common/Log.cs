namespace AuthBridge.Common;

/// <summary>Appends to a log file in the state directory, keeping one older file.</summary>
internal static class Log
{
    private const long MaxBytes = 1 << 20;
    private static readonly Lock Gate = new();
    private static bool _echo;

    public static string? FilePath { get; private set; }

    public static void Init(string fileName, bool echo = false)
    {
        Paths.EnsureStateDir();
        FilePath = Path.Combine(Paths.StateDir, fileName);
        _echo = echo;
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(Exception error, string message) => Write("ERROR", $"{message}: {error}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        lock (Gate)
        {
            try
            {
                if (_echo)
                    Console.Error.WriteLine(line);
                if (FilePath is not { } path)
                    return;
                if (new FileInfo(path) is { Exists: true, Length: > MaxBytes })
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never break a sign-in.
            }
        }
    }
}
