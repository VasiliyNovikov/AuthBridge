using System.Diagnostics;
using AuthBridge.Common;

namespace AuthBridge.Host;

/// <summary>Owns the manual and automatic updater on the tray thread. A failed launch is retried only after a fresh release check.</summary>
internal sealed class TrayUpdater(HostSettings settings, UpdateCheck releases, Helper helper, Func<bool, Process> launch) : IDisposable
{
    private Process? _process;
    private bool _automatic;
    private UpdateCheck.ReleaseOffer? _attempted;

    public TrayUpdater(HostSettings settings, UpdateCheck releases, Helper helper) : this(settings, releases, helper,
        automatic => Process.Start(new InstalledPackage(AppContext.BaseDirectory).TrayUpdateStartInfo(automatic))
            ?? throw new IOException("could not start authbridge update")) { }

    public bool CanUpdate => _process is null && releases.Available;
    public bool UpdateAutomatically => settings.UpdateAutomatically;

    public void SetUpdateAutomatically(bool enabled)
    {
        settings.SetUpdateAutomatically(enabled);
        _attempted = null;
    }

    public void StartManual()
    {
        if (_process is null && releases.Offer is { } offer)
            Start(offer, automatic: false);
    }

    public void Refresh()
    {
        Reap();
        if (UpdateAutomatically && _process is null && releases.Offer is { } offer && offer != _attempted && !helper.HasRelaySessions)
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
