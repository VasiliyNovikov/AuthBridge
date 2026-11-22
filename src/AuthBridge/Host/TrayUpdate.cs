using System.CommandLine;

namespace AuthBridge.Host;

internal static class TrayUpdate
{
    public const string OptionName = "--from-tray";
    public const string AutomaticOptionName = "--auto-from-tray";
    public const string LogFileName = "update.log";
    public const int LogUnavailable = 2;
    public const int Deferred = 3;

    public static Command CreateCommand(string stateDir, Func<bool, Task<int>> update)
    {
        var fromTray = new Option<bool>(OptionName) { Hidden = true };
        var automatic = new Option<bool>(AutomaticOptionName) { Hidden = true };
        var command = new Command("update", "Install the latest release; Linux clients get it when they connect.") { fromTray, automatic };
        command.SetAction((result, _) =>
        {
            var idleOnly = result.GetValue(automatic);
            Task<int> Run() => RunExclusiveAsync(stateDir, idleOnly, update);
            return idleOnly || result.GetValue(fromTray) ? RunLoggedAsync(stateDir, Run) : Run();
        });
        return command;
    }

    private static async Task<int> RunExclusiveAsync(string stateDir, bool automatic, Func<bool, Task<int>> update)
    {
        // A terminal update can race the tray. Hold the lock through file replacement and helper restart.
        Directory.CreateDirectory(stateDir);
        using var owner = new FileStream(Path.Combine(stateDir, "update.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        return await update(automatic);
    }

    private static async Task<int> RunLoggedAsync(string stateDir, Func<Task<int>> update)
    {
        var output = Console.Out;
        var error = Console.Error;
        try
        {
            Directory.CreateDirectory(stateDir);
            using var log = File.CreateText(Path.Combine(stateDir, LogFileName));
            log.AutoFlush = true;
            Console.SetOut(log);
            Console.SetError(log);
            try
            {
                return await update();
            }
            catch (Exception e) when (Program.IsCommandError(e))
            {
                return Program.Fail($"authbridge: {e.Message}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return LogUnavailable;
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }
}
