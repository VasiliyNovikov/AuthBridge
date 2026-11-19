using System.Runtime.InteropServices;
using AuthBridge.Common;

namespace AuthBridge.Host;

internal static class GitHubReleases
{
    private const string Releases = "https://github.com/VasiliyNovikov/AuthBridge/releases";

    public static async Task<string> LatestAsync(CancellationToken stopping = default)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var response = await http.GetAsync($"{Releases}/latest", stopping);
        // GitHub redirects to the page of the latest release, .../releases/tag/v1.2.3.
        const string prefix = "/tag/v";
        var location = response.Headers.Location?.OriginalString ?? "";
        var tag = location.LastIndexOf(prefix, StringComparison.Ordinal);
        return tag >= 0 ? location[(tag + prefix.Length)..] : throw new HttpRequestException($"no release found at {Releases}");
    }

    public static async Task<byte[]> DownloadAsync(string version)
    {
        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        return await http.GetByteArrayAsync($"{Releases}/download/v{version}/authbridge-win-{architecture}.zip");
    }
}

/// <summary>Checks at most once an hour on the helper loop; the tray reads the last successful result without waiting.</summary>
internal sealed class UpdateCheck(Func<CancellationToken, Task<string>> latest, string current)
{
    private DateTime _nextCheck;
    private Task _pending = Task.CompletedTask;
    private volatile bool _available;

    public bool Available => _available;

    public Task RefreshAsync(DateTime now, CancellationToken stopping = default)
    {
        if (now >= _nextCheck && _pending.IsCompleted)
        {
            _nextCheck = now.AddHours(1);
            _pending = CheckAsync(stopping);
        }
        return _pending;
    }

    private async Task CheckAsync(CancellationToken stopping)
    {
        try
        {
            _available = AppVersion.IsNewer(await latest(stopping), current);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            if (!stopping.IsCancellationRequested)
                Log.Warn($"cannot check for updates: {e.Message}");
        }
    }
}
