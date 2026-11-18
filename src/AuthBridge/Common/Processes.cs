using System.Diagnostics;

namespace AuthBridge.Common;

internal static class Processes
{
    /// <summary>A windowless child process with redirected standard streams.</summary>
    public static ProcessStartInfo StartInfo(string file, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(string file, IEnumerable<string> arguments, Stream? input = null)
    {
        using var process = Process.Start(StartInfo(file, arguments))!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            if (input is not null)
                await input.CopyToAsync(process.StandardInput.BaseStream);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process exited early; its exit code and error output tell why.
        }
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }
}
