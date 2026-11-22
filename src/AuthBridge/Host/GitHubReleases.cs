using System.Runtime.InteropServices;
using AuthBridge.Common;

namespace AuthBridge.Host;

internal static class GitHubReleases
{
    private const string Releases = "https://github.com/VasiliyNovikov/AuthBridge/releases";
    private static readonly HttpClient VersionLookup = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        BaseAddress = new Uri($"{Releases}/"),
    };

    public static Task<string> LatestAsync(CancellationToken stopping = default) => ReadLatestAsync(VersionLookup, stopping);

    internal static async Task<string> ReadLatestAsync(HttpClient http, CancellationToken stopping)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, "latest");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stopping);
        // GitHub redirects to the page of the latest release, .../releases/tag/v1.2.3.
        const string prefix = "/tag/v";
        var location = response.Headers.Location?.OriginalString ?? "";
        var tag = location.LastIndexOf(prefix, StringComparison.Ordinal);
        if ((int)response.StatusCode is >= 300 and < 400 && tag >= 0 && tag + prefix.Length < location.Length)
            return location[(tag + prefix.Length)..];
        throw new HttpRequestException($"no release redirect at {Releases}/latest (HTTP {(int)response.StatusCode})");
    }

    public static async Task<byte[]> DownloadAsync(string version)
    {
        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        return await http.GetByteArrayAsync($"{Releases}/download/v{version}/authbridge-win-{architecture}.zip");
    }
}

/// <summary>Checks on the helper loop; the tray reads the last successful result without waiting.</summary>
internal sealed class UpdateCheck(Func<CancellationToken, Task<string>> latest, string current)
{
    private const int CheckMinutes = 5;
    private int _intervalMinutes = CheckMinutes;
    private DateTime _nextCheck;
    private Task _pending = Task.CompletedTask;
    private volatile ReleaseOffer? _offer;

    public sealed record ReleaseOffer(string Version, DateTime CheckedAt);
    public ReleaseOffer? Offer => _offer;
    public bool Available => _offer is not null;

    public Task RefreshAsync(DateTime now, CancellationToken stopping = default)
    {
        if (_pending.IsCompleted && now >= _nextCheck && !stopping.IsCancellationRequested)
            _pending = CheckAsync(now, stopping);
        return _pending;
    }

    private async Task CheckAsync(DateTime now, CancellationToken stopping)
    {
        try
        {
            var version = await latest(stopping);
            _offer = AppVersion.IsNewer(version, current) ? new ReleaseOffer(version, now) : null;
            _intervalMinutes = CheckMinutes;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            if (!stopping.IsCancellationRequested)
            {
                _intervalMinutes = Math.Min(2 * _intervalMinutes, 60);
                Log.Warn($"cannot check for updates: {e.Message}; retrying in {_intervalMinutes} minutes");
            }
        }
        finally
        {
            _nextCheck = now.AddMinutes(_intervalMinutes);
        }
    }
}
