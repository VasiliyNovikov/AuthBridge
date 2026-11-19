using System.Runtime.Versioning;
using AuthBridge.Common;

namespace AuthBridge.Client;

/// <summary>Configures the shell and optional tool integrations on Linux.</summary>
internal static class Setup
{
    public const string NuGetPluginName = "nuget-plugin-azure-artifacts-authbridge";
    private const string BlockStart = "# >>> authbridge >>>";
    private const string BlockEnd = "# <<< authbridge <<<";

    private static string Self => Environment.ProcessPath!;

    /// <summary>The shell command in the init files; it keeps a BROWSER that is already set.</summary>
    internal static string ExportBrowser(string executable) =>
        $"if [ -z \"${{BROWSER:-}}\" ]; then BROWSER={PosixShell.Quote(executable)}; fi; export BROWSER";

    /// <summary>Exports BROWSER in the login and interactive shell init files.</summary>
    [SupportedOSPlatform("linux")]
    public static int Shell() => ConfigureShell(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Self, Console.Out);

    internal static int ConfigureShell(string home, string executable, TextWriter output)
    {
        var login = new[] { ".bash_profile", ".bash_login" }.FirstOrDefault(name => File.Exists(Path.Combine(home, name))) ?? ".profile";
        var export = ExportBrowser(executable);
        foreach (var path in new[] { login, ".bashrc" }.Select(name => Path.Combine(home, name)))
        {
            var content = File.Exists(path) ? File.ReadAllText(path) : "";
            var updated = WithBlock(content, export);
            if (updated != content)
                File.WriteAllText(path, updated);
            output.WriteLine($"{path}: {(updated == content ? "already configured" : "configured")}");
        }
        output.WriteLine($"New shells get BROWSER; in shells that are already open, run: eval \"$({PosixShell.Quote(executable)} --env)\"");
        return 0;
    }

    /// <summary>Prints the shell command in the init files. A program cannot change the environment of the shell that
    /// runs it, but that shell can run what this prints.</summary>
    public static int Env()
    {
        Console.WriteLine(ExportBrowser(Self));
        return 0;
    }

    /// <summary>Wraps Git Credential Manager in the global credential.helper list with "authbridge --gcm".</summary>
    [SupportedOSPlatform("linux")]
    public static Task<int> GcmAsync() => ConfigureGcmAsync(Self, Posix.Which, args => Processes.RunAsync("git", args));

    internal static async Task<int> ConfigureGcmAsync(string self, Func<string, string?> which, Func<string[], Task<ProcessResult>> git)
    {
        var read = await git(["config", "--global", "--get-all", "credential.helper"]);
        // git config --get-all exits with 1 and no output when the key is absent.
        var output = read is { ExitCode: 1, Output: "", Error: "" } ? "" : read.OutputOrThrow("cannot read git credential.helper");
        string[] helpers = output.Length == 0 ? [] : output.TrimEnd('\n').Split('\n');
        var wrapped = WrapGcmHelpers(helpers, self, which);
        if (wrapped.SequenceEqual(helpers))
        {
            Console.WriteLine("git credential.helper: nothing to change");
            return 0;
        }
        var unset = await git(["config", "--global", "--unset-all", "credential.helper"]);
        unset.OutputOrThrow("cannot unset git credential.helper");
        foreach (var helper in wrapped)
        {
            var add = await git(["config", "--global", "--add", "credential.helper", helper]);
            add.OutputOrThrow("cannot add git credential.helper");
        }
        Console.WriteLine($"git credential.helper: [{string.Join(", ", helpers)}] -> [{string.Join(", ", wrapped)}]");
        return 0;
    }

    /// <summary>Installs a NuGet plugin next to authbridge; NuGet 6.13+ prefers plugins found on PATH.</summary>
    [SupportedOSPlatform("linux")]
    public static int NuGet()
    {
        if (!File.Exists(Wrappers.NuGetProviderDll))
            return Program.Fail($"Azure Artifacts credential provider not found: {Wrappers.NuGetProviderDll}");
        var plugin = Path.Combine(Path.GetDirectoryName(Self)!, NuGetPluginName);
        File.Delete(plugin);
        File.WriteAllText(plugin, $"#!/bin/sh\nexec {PosixShell.Quote(Self)} --nuget-plugin \"$@\"\n");
        File.SetUnixFileMode(plugin, File.GetUnixFileMode(Self));
        Console.WriteLine($"{plugin}: installed; its directory must be on PATH");
        return 0;
    }

    /// <summary>Configures the shell and the integrations of the tools that are installed.</summary>
    [SupportedOSPlatform("linux")]
    public static async Task<int> AllAsync()
    {
        var result = Math.Max(Shell(), await GcmAsync());
        if (File.Exists(Wrappers.NuGetProviderDll))
            return Math.Max(result, NuGet());
        Console.WriteLine("NuGet: skipped, the Azure Artifacts credential provider is not installed");
        return result;
    }

    /// <summary>Adds the authbridge block to a shell init file, or replaces the existing one.</summary>
    public static string WithBlock(string content, string body)
    {
        var block = $"{BlockStart}\n{body}\n{BlockEnd}\n";
        var start = content.IndexOf(BlockStart, StringComparison.Ordinal);
        var end = start < 0 ? -1 : content.IndexOf(BlockEnd, start, StringComparison.Ordinal);
        if (end >= 0)
        {
            var rest = content[(end + BlockEnd.Length)..];
            return content[..start] + block + (rest.StartsWith('\n') ? rest[1..] : rest);
        }
        var separator = content.Length == 0 ? "" : content.EndsWith('\n') ? "\n" : "\n\n";
        return content + separator + block;
    }

    /// <summary>Puts "authbridge --gcm" in front of the Git Credential Manager entries of a credential.helper list.</summary>
    public static string[] WrapGcmHelpers(string[] helpers, string self, Func<string, string?> which) =>
        Array.ConvertAll(helpers, helper =>
        {
            var parts = helper.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts is [] || parts[0].StartsWith('!') || parts.Contains("--gcm"))
                return helper;
            var program = parts[0].StartsWith('/') ? parts[0] : which("git-credential-" + parts[0]);
            if (program is null || Path.GetFileName(program) is not ("git-credential-manager" or "git-credential-manager-core"))
                return helper;
            return string.Join(' ', [self, "--gcm", program, .. parts[1..]]);
        });
}
