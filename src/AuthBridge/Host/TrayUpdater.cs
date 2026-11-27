using System.Diagnostics;
using AuthBridge.Common;

namespace AuthBridge.Host;

/// <summary>Owns the manual and automatic updater on the tray thread. A failed launch is retried only after a fresh release check.</summary>
internal sealed class TrayUpdater(HostSettings settings, UpdateCheck releases, Helper helper, Func<bool, Process> launch) : IDisposable
{
    private Process? _process;
    private bool _checking;
    private bool _automatic;
    private UpdateCheck.ReleaseOffer? _attempted;

    public TrayUpdater(HostSettings settings, UpdateCheck releases, Helper helper) : this(settings, releases, helper,
        automatic => Process.Start(new InstalledPackage(AppContext.BaseDirectory).TrayUpdateStartInfo(automatic))
            ?? throw new IOException("could not start authbridge update")) { }

    public string? AvailableVersion => releases.Offer?.Version;
    public bool IsBusy => _checking || _process is not null;
    public bool UpdateAutomatically => settings.UpdateAutomatically;

    public void SetUpdateAutomatically(bool enabled)
    {
        settings.SetUpdateAutomatically(enabled);
        _attempted = null;
    }

    /// <summary>A check returns a notification. An install click applies only to the version the menu displayed.</summary>
    public async Task<string?> CheckOrUpdateAsync(string? displayedVersion)
    {
        if (IsBusy)
            return null;
        if (displayedVersion is null)
            return await CheckNowAsync();
        if (releases.Offer is { } offer && offer.Version == displayedVersion)
            Start(offer, automatic: false);
        return null;
    }

    private async Task<string?> CheckNowAsync()
    {
        _checking = true;
        try
        {
            if (!await releases.RefreshAsync(DateTime.UtcNow, force: true))
                return "Could not check for updates. See host.log for details.";
            return AvailableVersion is { } version ? $"AuthBridge {version} is available." : "AuthBridge is up to date.";
        }
        catch (OperationCanceledException)
        {
            return null; // The helper is shutting down; there is no result to show.
        }
        finally
        {
            _checking = false;
        }
    }

    public void Refresh()
    {
        Reap();
        if (UpdateAutomatically && !IsBusy && releases.Offer is { } offer && offer != _attempted && !helper.HasRelaySessions)
            Start(offer, automatic: true);
    }

    public bool TryRequestIdleRestart(int updaterPid, Func<bool> postClose) =>
        _automatic && _process?.Id == updaterPid && UpdateAutomatically && helper.TryRequestIdleRestart(postClose);

    private void Start(UpdateCheck.ReleaseOffer offer, bool automatic)
    {
        _attempted = offer;
        try
        {
            _process = launch(automatic);
            _automatic = automatic;
            Log.Info($"starting {(automatic ? "automatic" : "manual")} tray update to {offer.Version}");
        }
        catch (Exception e) when (Program.IsCommandError(e))
        {
            Log.Error(e, "could not launch the tray updater");
        }
    }

    private void Reap()
    {
        if (_process is not { HasExited: true } finished)
            return;
        if (finished.ExitCode == TrayUpdate.LogUnavailable)
            Log.Warn($"tray update could not write {TrayUpdate.LogFileName}");
        else if (finished.ExitCode != 0 && finished.ExitCode != TrayUpdate.Deferred)
            Log.Warn($"authbridge update exited with code {finished.ExitCode}; check {TrayUpdate.LogFileName}");
        finished.Dispose();
        _process = null;
    }

    public void Dispose() => _process?.Dispose();
}
