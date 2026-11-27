#if WINDOWS
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
#endif
using AuthBridge.Common;
using AuthBridge.Host;
using static AuthBridge.Tests.TestSupport;

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
    public async Task MenuChecksOnDemandAndInstallsOnlyTheDisplayedOffer()
    {
        using var directory = new TempDirectory("authbridge-menu-");
        var settings = NewSettings(Path.Combine(directory.FullName, "config.json"));
        string? latest = "1.0";
        var releases = new UpdateCheck(_ => latest is null ? Task.FromException<string>(new HttpRequestException("offline")) : Task.FromResult(latest), "1.0");
        await using var helper = NewHelper();
        var launches = new List<bool>();
        using var updates = new TrayUpdater(settings, releases, helper, automatic => { launches.Add(automatic); return Process.GetCurrentProcess(); });
        using var about = AboutDialog.Create(SystemIcons.Application);
        using var status = new Form();
        using var window = new TrayControlWindow(updates);
        using var menu = new ContextMenuStrip();
        var messages = new List<string>();
        TrayMenu.Populate(menu, updates, status, about, window, messages.Add);
        menu.Opening += (_, e) => e.Cancel = true; // Prepare the actual menu without displaying it.
        var automatic = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Auto-update");
        var now = DateTime.UtcNow;
        await releases.RefreshAsync(now);
        settings.SetUpdateAutomatically(true);
        menu.Show(Point.Empty);
        var update = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Check for updates");
        Assert.IsTrue(update.Available && update.Enabled);
        Assert.IsTrue(automatic.Checked);

        update.PerformClick();
        StringAssert.Contains(messages[^1], "up to date");
        latest = null;
        update.PerformClick();
        StringAssert.Contains(messages[^1], "Could not check");
        latest = "2.0";
        update.PerformClick(); // Do not wait for the next scheduled check, even after a failure.
        StringAssert.Contains(messages[^1], "2.0");
        Assert.AreEqual("Update to 2.0", update.Text);
        latest = "3.0";
        await releases.RefreshAsync(now.AddMinutes(6));
        update.PerformClick();
        Assert.IsEmpty(launches); // Neither checking nor clicking a stale version installs anything.
        automatic.PerformClick();
        Assert.IsFalse(settings.UpdateAutomatically);
        menu.Show(Point.Empty);
        Assert.IsFalse(automatic.Checked);
        Assert.AreEqual("Update to 3.0", update.Text);
        update.PerformClick();
        CollectionAssert.AreEqual(new[] { false }, launches);
        Assert.IsTrue(update.Available);
        Assert.IsFalse(update.Enabled);
    }

    [STATestMethod]
    public async Task StatusMenuShowsFreshConnectionsAndRemovesStaleRows()
    {
        var since = DateTimeOffset.Now.AddMinutes(-5);
        var dev = new TunnelStatus("dev", "connected", "", since, "1.2.3", "/dev.sock");
        var build = new TunnelStatus("build", "disconnected", "network unavailable", since);
        var snapshot = new PingResult(Protocol.Version, 42, 2, [dev, build], "workstation");
        await using var helper = NewHelper();
        var settings = NewSettings(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        using var updates = new TrayUpdater(settings, new UpdateCheck(_ => Task.FromResult("1.0"), "1.0"), helper);
        using var status = new StatusDialog(SystemIcons.Application, 12345, () => snapshot);
        using var about = new Form();
        about.Load += (_, _) => about.DialogResult = DialogResult.Cancel;
        using var window = new TrayControlWindow(updates);
        using var menu = new ContextMenuStrip();
        TrayMenu.Populate(menu, updates, status.Window, about, window, message => Assert.Fail(message));
        var command = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Status");
        status.Window.Opacity = 0;
        status.Window.VisibleChanged += (_, _) =>
        {
            if (status.Window.Visible)
                status.Window.BeginInvoke(status.Window.Close);
        };
        command.PerformClick();

        var summary = status.Window.Controls.OfType<Label>().Single();
        var connections = status.Window.Controls.OfType<ListView>().Single();
        Assert.DoesNotContain("Remote socket", connections.Columns.Cast<ColumnHeader>().Select(column => column.Text));
        var rows = connections.Items;
        StringAssert.Contains(summary.Text, "workstation");
        StringAssert.Contains(summary.Text, "127.0.0.1:12345");
        StringAssert.Contains(summary.Text, "1/2 connected");
        StringAssert.Contains(summary.Text, "Active relay sessions: 2");
        var first = rows[0];
        CollectionAssert.AreEqual(new[] { "dev", "connected", since.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"), "1.2.3", "" },
            first.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(cell => cell.Text).ToArray());
        Assert.AreEqual("network unavailable", rows[1].SubItems[4].Text);

        status.Update(snapshot with { Tunnels = [dev with { Client = "1.2.4" }] });
        Assert.HasCount(1, rows);
        Assert.AreSame(first, rows[0]);
        Assert.AreEqual("1.2.4", first.SubItems[3].Text);
        snapshot = snapshot with { Tunnels = [], Sessions = 0 };
        command.PerformClick();
        Assert.IsEmpty(rows);
        StringAssert.Contains(summary.Text, "No machines configured");
        StringAssert.Contains(summary.Text, "Active relay sessions: 0");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TrayAcknowledgesOnlyItsAutomaticUpdaterAndStopsThroughItsWindow(bool automatic)
    {
        using var directory = new TempDirectory("authbridge-tray-stop-");
        var settings = NewSettings(Path.Combine(directory.FullName, "config.json"));
        settings.SetUpdateAutomatically(automatic);
        var releases = new UpdateCheck(_ => Task.FromResult("2.0"), "1.0");
        await releases.RefreshAsync(DateTime.UtcNow);
        await using var helper = StartHelper();
        var updates = new TrayUpdater(settings, releases, helper, _ => Process.GetCurrentProcess());
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tray = TrayIcon.Start(helper, updates, stopped.SetResult);
        Assert.AreEqual(automatic, TrayControlWindow.RequestIdleExit(Environment.ProcessId));
        if (!automatic)
        {
            Assert.IsFalse(stopped.Task.IsCompleted);
            Assert.IsTrue(TrayControlWindow.RequestExit(Environment.ProcessId));
        }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
#endif

    [TestMethod]
    public async Task ManualCheckJoinsThePendingPollWithoutLaunchingOrReportingTwice()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        using var stopping = new CancellationTokenSource();
        var releases = new UpdateCheck(_ => { checks++; return pending.Task; }, "1.0", stopping.Token);
        await using var helper = NewHelper();
        var settings = NewSettings(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        using var tray = new TrayUpdater(settings, releases, helper, _ => throw new AssertFailedException("Checking should not install."));
        var poll = releases.RefreshAsync(DateTime.UtcNow);
        var check = tray.CheckOrUpdateAsync(null);
        Assert.IsTrue(tray.IsBusy);
        Assert.IsNull(await tray.CheckOrUpdateAsync(null));

        pending.SetResult("2.0");
        Assert.IsTrue(await poll);
        StringAssert.Contains(await check, "2.0");
        Assert.IsFalse(tray.IsBusy);
        await stopping.CancelAsync();
        Assert.IsNull(await tray.CheckOrUpdateAsync(null));
        Assert.AreEqual(1, checks);
    }

    [TestMethod]
    public async Task FailedAutomaticLaunchWaitsForAFreshCheckButManualRetryIsAvailable()
    {
        using var directory = new TempDirectory("authbridge-auto-retry-");
        var settings = NewSettings(Path.Combine(directory.FullName, "config.json"));
        var offline = false;
        var releases = new UpdateCheck(_ => offline ? Task.FromException<string>(new HttpRequestException("offline")) : Task.FromResult("2.0"), "1.0");
        await using var helper = NewHelper();
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
        await tray.CheckOrUpdateAsync("2.0");
        CollectionAssert.AreEqual(new[] { true, true, false }, attempts);
    }
}
