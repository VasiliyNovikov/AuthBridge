using System.ComponentModel;
using System.Diagnostics;

namespace AuthBridge.Common;

internal readonly record struct ProcessResult(int ExitCode, string Output, string Error)
{
    public string OutputOrThrow(string operation)
    {
        if (ExitCode == 0)
            return Output;
        var message = $"{operation} (exit code {ExitCode})";
        throw new IOException(string.IsNullOrWhiteSpace(Error) ? message : $"{message}: {Error.Trim()}");
    }
}

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

    public static async Task<ProcessResult> RunAsync(string file, IEnumerable<string> arguments, Stream? input = null, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        using var process = Process.Start(StartInfo(file, arguments))!;
        using var stop = cancel.Register(() => Stop(process));
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await SendInputAsync(process, input, cancel);
            await process.WaitForExitAsync(cancel);
        }
        catch
        {
            Stop(process);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            cancel.ThrowIfCancellationRequested();
            throw;
        }
        cancel.ThrowIfCancellationRequested();
        return new(process.ExitCode, await output, await error);
    }

    private static async Task SendInputAsync(Process process, Stream? source, CancellationToken cancel)
    {
        using var stdin = process.StandardInput;
        if (source is null)
            return;
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = await source.ReadAsync(buffer, cancel)) > 0)
        {
            try
            {
                await stdin.BaseStream.WriteAsync(buffer.AsMemory(0, count), cancel);
            }
            catch (IOException) when (process.HasExited && process.ExitCode != 0)
            {
                return; // A rejected command can close its pipe early. Return its stderr and exit code.
            }
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when ((e is InvalidOperationException or Win32Exception) && process.HasExited)
        {
            // The child exited during cancellation.
        }
    }
}
