using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AuthBridge.Host;

/// <summary>Release versions, and the installation for the current user, from packages or the GitHub releases.</summary>
internal static class Updater
{
    private const string Releases = "https://github.com/VasiliyNovikov/AuthBridge/releases";
    private const string ReplacedSuffix = ".authbridge-old";

    /// <summary>The files of a Windows package, as release.cs creates it.</summary>
    private static readonly string[] PackageFiles = ["authbridge.exe", "authbridgew.exe", "authbridge-linux-x64", "authbridge-linux-arm64", "README.md", "LICENSE"];

    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AuthBridge");

    /// <summary>This program's version, such as 1.2.3, without build metadata.</summary>
    public static string Current { get; } =
        WithoutMetadata(typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);

    public static string WithoutMetadata(string version) => version.Split('+')[0].Trim();

    /// <summary>Whether <paramref name="version"/> is a later release than <paramref name="than"/>, ignoring prerelease labels.</summary>
    public static bool IsNewer(string version, string than) => Numbers(version) > Numbers(than);

    public static async Task<string> LatestAsync()
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var response = await http.GetAsync($"{Releases}/latest");
        // GitHub redirects to the page of the latest release, .../releases/tag/v1.2.3.
        var location = response.Headers.Location?.OriginalString ?? "";
        var tag = location.LastIndexOf("/tag/v", StringComparison.Ordinal);
        return tag >= 0 ? location[(tag + 6)..] : throw new HttpRequestException($"no release found at {Releases}");
    }

    public static async Task<byte[]> DownloadAsync(string version)
    {
        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        return await http.GetByteArrayAsync($"{Releases}/download/v{version}/authbridge-win-{architecture}.zip");
    }

    /// <summary>Installs a release package downloaded from GitHub.</summary>
    public static void Install(byte[] package)
    {
        using var archive = new ZipArchive(new MemoryStream(package));
        foreach (var entry in archive.Entries)
            Replace(Path.GetFileName(entry.FullName), entry.ExtractToFile);
        DeleteReplacedFiles();
    }

    /// <summary>Installs the package in a folder, such as the one this program runs from.</summary>
    public static void InstallFrom(string folder)
    {
        foreach (var name in PackageFiles.Where(name => File.Exists(Path.Combine(folder, name))))
            Replace(name, path => File.Copy(Path.Combine(folder, name), path));
        DeleteReplacedFiles();
    }

    /// <summary>Deletes the files that updates replaced, except those still in use.</summary>
    public static void DeleteReplacedFiles()
    {
        if (!Directory.Exists(InstallDir))
            return;
        foreach (var file in Directory.EnumerateFiles(InstallDir, $"*{ReplacedSuffix}"))
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
    private static void Replace(string name, Action<string> write)
    {
        Directory.CreateDirectory(InstallDir);
        var path = Path.Combine(InstallDir, name);
        var fresh = $"{path}.{Environment.ProcessId}.new";
        write(fresh);
        if (File.Exists(path))
            File.Move(path, $"{path}.{Environment.ProcessId}{ReplacedSuffix}");
        File.Move(fresh, path);
    }

    private static Version Numbers(string version) => Version.TryParse(version.Split('-', '+')[0], out var numbers) ? numbers : new();
}
