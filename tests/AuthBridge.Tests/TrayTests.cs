#if WINDOWS
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
#endif
using AuthBridge.Common;
using AuthBridge.Host;

namespace AuthBridge.Tests;

[TestClass]
public sealed class TrayTests
{
    [TestMethod]
    public void MixedTunnelStatesUseThePartialConnectionColorAndListEachMachine()
    {
        var online = new TunnelStatus("dev", "connected", "", DateTimeOffset.UtcNow);
        var offline = online with { Name = "build", State = "disconnected" };
        var status = TrayNotification.Describe([online, offline]);
        Assert.AreEqual(("AuthBridge: 1/2 connected\ndev: connected\nbuild: disconnected", 0xD29922u), status);
    }

    [TestMethod]
    public void LongTooltipNeverSplitsASupplementaryCharacter()
    {
        const string prefix = "AuthBridge: 1/1 connected\n";
        var name = new string('a', 123 - prefix.Length) + "\U0001F4BB";
        var (tip, _) = TrayNotification.Describe([new TunnelStatus(name, "connected", "", DateTimeOffset.UtcNow)]);
        Assert.AreEqual(prefix + name[..^2] + "...", tip);
        Assert.IsTrue(tip.Length <= 127);
    }

#if WINDOWS
    [STATestMethod]
    public async Task MenuReflectsTheSavedPreferenceAndRechecksTheDisplayedUpdate()
    {
        var directory = Directory.CreateTempSubdirectory("authbridge-menu-");
        try
        {
            var settings = new HostSettings(Path.Combine(directory.FullName, "config.json"), message => Assert.Fail(message));
            var latest = "2.0";
            var releases = new UpdateCheck(_ => Task.FromResult(latest), "1.0");
            await using var helper = new Helper(_ => { }, () => [], () => null);
            using var updates = new TrayUpdater(settings, releases, helper, _ => throw new AssertFailedException("Launched a stale update."));
            using var about = AboutDialog.Create(SystemIcons.Application);
            using var window = new TrayControlWindow(updates, () => Assert.Fail("Unexpected shutdown."));
            using var menu = TrayMenu.Create(updates, about, window);
            menu.Opening += (_, e) => e.Cancel = true; // Prepare the actual menu without displaying it.
            var automatic = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Auto-update");
            var update = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Update");

            var now = DateTime.UtcNow;
            await releases.RefreshAsync(now);
            settings.SetUpdateAutomatically(true);
            menu.Show(Point.Empty);
            Assert.IsTrue(update.Available);
            Assert.IsTrue(automatic.Checked);
            latest = "1.0";
            await releases.RefreshAsync(now.AddMinutes(5));
            update.PerformClick();
            automatic.PerformClick();
            Assert.IsFalse(settings.UpdateAutomatically);
            menu.Show(Point.Empty);
            Assert.IsFalse(update.Available);
            Assert.IsFalse(automatic.Checked);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TrayAcknowledgesOnlyItsAutomaticUpdaterAndStopsThroughItsWindow(bool automatic)
    {
        var directory = Directory.CreateTempSubdirectory("authbridge-tray-stop-");
        try
        {
            var settings = new HostSettings(Path.Combine(directory.FullName, "config.json"), message => Assert.Fail(message));
            settings.SetUpdateAutomatically(automatic);
            var releases = new UpdateCheck(_ => Task.FromResult("2.0"), "1.0");
            await releases.RefreshAsync(DateTime.UtcNow);
            await using var helper = new Helper(_ => { }, () => [], () => null);
            var updates = new TrayUpdater(settings, releases, helper, _ => Process.GetCurrentProcess());
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var tray = TrayIcon.Start(() => [], updates, stopped.SetResult);
            Assert.AreEqual(automatic, TrayControlWindow.RequestIdleExit(Environment.ProcessId));
            if (!automatic)
            {
                Assert.IsFalse(stopped.Task.IsCompleted);
                Assert.IsTrue(TrayControlWindow.RequestExit(Environment.ProcessId));
            }
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
#endif

    [TestMethod]
    public async Task FailedAutomaticLaunchWaitsForAFreshCheckButManualRetryIsAvailable()
    {
        var directory = Directory.CreateTempSubdirectory("authbridge-auto-retry-");
        try
        {
            var settings = new HostSettings(Path.Combine(directory.FullName, "config.json"), message => Assert.Fail(message));
            var offline = false;
            var releases = new UpdateCheck(_ => offline ? Task.FromException<string>(new HttpRequestException("offline")) : Task.FromResult("2.0"), "1.0");
            await using var helper = new Helper(_ => { }, () => [], () => null);
            var attempts = new List<bool>();
            using var tray = new TrayUpdater(settings, releases, helper, automatic => { attempts.Add(automatic); throw new IOException("launch failed"); });
            tray.SetUpdateAutomatically(true);
            tray.Refresh();
            Assert.IsEmpty(attempts);
            var now = DateTime.UtcNow;
            await releases.RefreshAsync(now);
            tray.Refresh();
            tray.Refresh();
            offline = true;
            await releases.RefreshAsync(now.AddMinutes(5));
            tray.Refresh();
            CollectionAssert.AreEqual(new[] { true }, attempts);
            offline = false;
            await releases.RefreshAsync(now.AddMinutes(15));
            tray.Refresh();
            tray.SetUpdateAutomatically(false);
            tray.StartManual();
            CollectionAssert.AreEqual(new[] { true, true, false }, attempts);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
