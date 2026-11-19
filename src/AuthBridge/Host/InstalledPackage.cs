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
    public void Install(byte[] package)
    {
        using var archive = new ZipArchive(new MemoryStream(package));
        foreach (var entry in archive.Entries)
            Replace(Path.GetFileName(entry.FullName), entry.ExtractToFile);
        DeleteReplacedFiles();
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
        write(fresh);
        if (File.Exists(path))
            File.Move(path, $"{path}.{Environment.ProcessId}{ReplacedSuffix}");
        File.Move(fresh, path);
    }
}
