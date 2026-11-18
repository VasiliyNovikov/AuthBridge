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

    /// <summary>Exports BROWSER in the login and interactive shell init files.</summary>
    [SupportedOSPlatform("linux")]
    public static int Shell()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var login = new[] { ".bash_profile", ".bash_login" }.FirstOrDefault(name => File.Exists(Path.Combine(home, name))) ?? ".profile";
        foreach (var path in new[] { login, ".bashrc" }.Select(name => Path.Combine(home, name)))
        {
            var content = File.Exists(path) ? File.ReadAllText(path) : "";
            var updated = WithBlock(content, $"export BROWSER=\"${{BROWSER:-{Self}}}\"");
            if (updated != content)
                File.WriteAllText(path, updated);
            Console.WriteLine($"{path}: {(updated == content ? "already configured" : "configured")}");
        }
        return 0;
    }

    /// <summary>Wraps Git Credential Manager in the global credential.helper list with "authbridge --gcm".</summary>
    [SupportedOSPlatform("linux")]
    public static async Task<int> GcmAsync()
    {
        var output = (await Git("config", "--global", "--get-all", "credential.helper")).Output;
        string[] helpers = output.Length == 0 ? [] : output.TrimEnd('\n').Split('\n');
        var wrapped = WrapGcmHelpers(helpers, Self, Posix.Which);
        if (wrapped.SequenceEqual(helpers))
        {
            Console.WriteLine("git credential.helper: nothing to change");
            return 0;
        }
        await Git("config", "--global", "--unset-all", "credential.helper");
        foreach (var helper in wrapped)
            await Git("config", "--global", "--add", "credential.helper", helper);
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
        File.WriteAllText(plugin, $"#!/bin/sh\nexec '{Self}' --nuget-plugin \"$@\"\n");
        File.SetUnixFileMode(plugin, File.GetUnixFileMode(Self));
        Console.WriteLine($"{plugin}: installed; its directory must be on PATH");
        return 0;
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
    [
        .. helpers.Select(helper =>
        {
            var parts = helper.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts is [] || parts[0].StartsWith('!') || parts.Contains("--gcm"))
                return helper;
            var program = parts[0].StartsWith('/') ? parts[0] : which("git-credential-" + parts[0]);
            return Path.GetFileName(program) is "git-credential-manager" or "git-credential-manager-core"
                ? string.Join(' ', [self, "--gcm", program!, .. parts[1..]])
                : helper;
        }),
    ];

    private static Task<(int ExitCode, string Output, string Error)> Git(params string[] args) => Processes.RunAsync("git", args);
}
