using System.CommandLine;

namespace AuthBridge.Host;

internal static class TrayUpdate
{
    public const string OptionName = "--from-tray";
    public const string LogFileName = "update.log";
    public const int LogUnavailable = 2;

    public static Command CreateCommand(string stateDir, Func<Task<int>> update)
    {
        var fromTray = new Option<bool>(OptionName) { Hidden = true };
        var command = new Command("update", "Install the latest release; Linux clients get it when they connect.") { fromTray };
        command.SetAction((result, _) => result.GetValue(fromTray)
            ? RunLoggedAsync(stateDir, update)
            : update());
        return command;
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
