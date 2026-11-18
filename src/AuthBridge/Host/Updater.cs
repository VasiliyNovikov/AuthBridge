using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AuthBridge.Host;

/// <summary>Release versions, and updates of this installation from the GitHub releases.</summary>
internal static class Updater
{
    private const string Releases = "https://github.com/VasiliyNovikov/AuthBridge/releases";
    private const string ReplacedSuffix = ".authbridge-old";

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

    /// <summary>Extracts a Windows package over this installation.</summary>
    public static void Install(byte[] package)
    {
        using var archive = new ZipArchive(new MemoryStream(package));
        foreach (var entry in archive.Entries)
        {
            var path = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(entry.FullName));
            // Files in use, such as this program, cannot be replaced or deleted, but they can be renamed.
            if (File.Exists(path))
                File.Move(path, $"{path}.{Environment.ProcessId}{ReplacedSuffix}");
            entry.ExtractToFile(path);
        }
        DeleteReplacedFiles();
    }

    /// <summary>Deletes the files that updates replaced, except those still in use.</summary>
    public static void DeleteReplacedFiles()
    {
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, $"*{ReplacedSuffix}"))
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

    private static Version Numbers(string version) => Version.TryParse(version.Split('-', '+')[0], out var numbers) ? numbers : new();
}
