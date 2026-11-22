using System.Diagnostics;
using System.IO.Compression;

namespace AuthBridge.Host;

/// <summary>Installs the Windows package files for the current user, even when the old executable is in use.</summary>
internal sealed class InstalledPackage(string location)
{
    private const string ReplacedSuffix = ".authbridge-old";

    /// <summary>The files of a Windows package, as release.cs creates it.</summary>
    private static readonly string[] PackageFiles = ["authbridge.exe", "authbridgew.exe", "authbridge-linux-x64", "authbridge-linux-arm64", "README.md", "LICENSE"];

    public static InstalledPackage CurrentUser { get; } =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AuthBridge"));

    public string Location { get; } = location;

    private string ConsoleExecutable => Path.Combine(Location, "authbridge.exe");
    private string? WindowlessExecutable
    {
        get
        {
            var path = Path.Combine(Location, "authbridgew.exe");
            return File.Exists(path) ? path : null;
        }
    }

    public string HelperExecutable => WindowlessExecutable ?? ConsoleExecutable;

    internal bool ContainsExecutable(string? path) => string.Equals(path, ConsoleExecutable, StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, Path.Combine(Location, "authbridgew.exe"), StringComparison.OrdinalIgnoreCase);

    // The updater stops its parent helper, so it must not rely on pipes owned by that helper.
    public ProcessStartInfo TrayUpdateStartInfo() => new(HelperExecutable, ["update", TrayUpdate.OptionName])
    {
        UseShellExecute = false,
        CreateNoWindow = true, // Also covers installations with only the console executable.
    };

    // At logon the console executable must start a detached helper, whereas the GUI executable can serve directly.
    public string LogonCommand => WindowlessExecutable is { } gui ? $"\"{gui}\" serve" : $"\"{ConsoleExecutable}\" start";

    /// <summary>Adds this installation once to the Windows user PATH, preserving the original unexpanded entries.</summary>
    public string IncludeInUserPath(string path)
    {
        var entries = path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(entry.Trim('"'))));
        if (entries.Contains(Path.TrimEndingDirectorySeparator(Location), StringComparer.OrdinalIgnoreCase))
            return path;
        return path.Length == 0 || path.EndsWith(';') ? path + Location : $"{path};{Location}";
    }

    /// <summary>Installs a release package downloaded from GitHub.</summary>
    public async Task InstallAsync(byte[] package, Func<Task> stopIfRunning)
    {
        using var archive = new ZipArchive(new MemoryStream(package));
        var staging = Directory.CreateTempSubdirectory("authbridge-update-");
        try
        {
            // Verify and extract the entire release before stopping the working helper or modifying its files.
            foreach (var name in PackageFiles)
            {
                if (archive.GetEntry(name) is not { Length: > 0 } entry)
                    throw new InvalidDataException($"The release package is missing {name} or it is empty.");
                entry.ExtractToFile(Path.Combine(staging.FullName, name));
            }
            await InstallFromAsync(staging.FullName, stopIfRunning);
        }
        finally
        {
            staging.Delete(recursive: true);
        }
    }

    /// <summary>Copies a package into place after stopping the helper. An in-place install needs neither step.</summary>
    public async Task<bool> InstallFromAsync(string folder, Func<Task> stopIfRunning)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (string.Equals(source, Path.TrimEndingDirectorySeparator(Path.GetFullPath(Location)), StringComparison.OrdinalIgnoreCase))
            return false;
        var executable = Path.Combine(source, "authbridge.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("The Windows package is missing authbridge.exe.", executable);

        await stopIfRunning();
        foreach (var name in PackageFiles.Where(name => File.Exists(Path.Combine(source, name))))
            Replace(name, path => File.Copy(Path.Combine(source, name), path));
        DeleteReplacedFiles();
        return true;
    }

    /// <summary>Deletes the files that updates replaced, except those still in use.</summary>
    public void DeleteReplacedFiles()
    {
        if (!Directory.Exists(Location))
            return;
        foreach (var file in Directory.EnumerateFiles(Location, $"*{ReplacedSuffix}"))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still in use; the helper tries again when it starts.
            }
        }
    }

    /// <summary>Writes a file of the installation. A file in use, such as this program, cannot be overwritten or
    /// deleted, but it can be renamed; so the new file is written beside it and swapped in, which also works when the
    /// source is the installation itself.</summary>
    private void Replace(string name, Action<string> write)
    {
        Directory.CreateDirectory(Location);
        var path = Path.Combine(Location, name);
        var fresh = $"{path}.{Environment.ProcessId}.new";
        try
        {
            write(fresh);
            if (!File.Exists(path))
            {
                File.Move(fresh, path);
                return;
            }
            var backup = $"{path}.{Environment.ProcessId}{ReplacedSuffix}";
            File.Move(path, backup);
            try
            {
                File.Move(fresh, path);
            }
            catch
            {
                File.Move(backup, path);
                throw;
            }
        }
        finally
        {
            File.Delete(fresh);
        }
    }
}
