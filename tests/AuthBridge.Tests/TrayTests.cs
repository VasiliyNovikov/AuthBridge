using System.Runtime.InteropServices;
using AuthBridge.Common;
using AuthBridge.Host;

namespace AuthBridge.Tests;

[TestClass]
public sealed class TrayTests
{
    [TestMethod]
    public void TrayRoutesSelectionRestartAndShutdownWithoutSwallowingWindowClose()
    {
        var calls = new List<string>();
        const uint taskbarCreated = 0xC123;
        var procedure = new TrayWindowProcedure(taskbarCreated, () => calls.Add("refresh"),
            (x, y) => calls.Add($"menu {x} {y}"), () => calls.Add("close"), () => calls.Add("quit"), () => calls.Add("invalidate"),
            pid => pid == 42);

        Assert.AreEqual((nint)0, procedure.Handle(0x0113, 0, 0)); // WM_TIMER
        Assert.AreEqual((nint)0, procedure.Handle(0x8001, (-20 << 16) | 0xFFF6, (1 << 16) | 0x0401)); // keyboard selection on a secondary monitor
        Assert.IsNull(procedure.Handle(0x8001, 0, 0x0200)); // mouse movement
        Assert.IsNull(procedure.Handle(taskbarCreated, 0, 0));
        Assert.AreEqual((nint)0, procedure.Handle(0x8002, 41, 0));
        Assert.AreEqual((nint)1, procedure.Handle(0x8002, 42, 0));
        Assert.IsNull(procedure.Handle(0x0010, 0, 0)); // let Windows destroy the window
        Assert.AreEqual((nint)0, procedure.Handle(0x0002, 0, 0));
        CollectionAssert.AreEqual(new[] { "refresh", "menu -10 -20", "invalidate", "refresh", "close", "quit" }, calls.ToArray());
    }

    [TestMethod]
    public void OpenAboutDialogReappliesTheThemeAndLetsWindowsPaintSystemColors()
    {
        var darkPreference = true;
        var dark = false;
        const int window = 7, dc = 8, darkBrush = 9;
        var procedure = new AboutDialogProcedure(
            dialog => { Assert.AreEqual((nint)window, dialog); dark = darkPreference; },
            context => { Assert.AreEqual((nint)dc, context); return dark ? darkBrush : 0; },
            (_, _) => Assert.Fail("Changing themes must not close About."));

        Assert.AreEqual((nint)1, procedure.Handle(window, 0x0110, 0, 0)); // WM_INITDIALOG: set default focus
        Assert.AreEqual((nint)darkBrush, procedure.Handle(window, 0x0138, dc, 0)); // static icon or text
        darkPreference = false;
        Assert.AreEqual((nint)0, procedure.Handle(window, 0x001A, 0, 0)); // WM_SETTINGCHANGE
        Assert.AreEqual((nint)0, procedure.Handle(window, 0x0136, dc, 0)); // dialog background
    }

    [TestMethod]
    [DataRow(0x0010, 0, 0)] // title-bar Close
    [DataRow(0x0111, 2, 0)] // Esc
    [DataRow(0x0111, 1, 123)] // button click
    public void AboutDismissesOnCloseButNotOnUnrelatedControlNotifications(int message, int command, int control)
    {
        var ended = new List<(nint Window, int Result)>();
        var procedure = new AboutDialogProcedure(_ => { }, _ => 0, (window, result) => ended.Add((window, result)));
        Assert.AreEqual((nint)0, procedure.Handle(42, 0x0111, (6 << 16) | 2, 123)); // BN_SETFOCUS on IDCANCEL
        Assert.IsEmpty(ended);
        Assert.AreEqual((nint)1, procedure.Handle(42, (uint)message, command, control));
        Assert.AreEqual(((nint)42, 1), ended.Single());
    }

    [TestMethod]
    public void AboutKeepsSystemColorsForHighContrastOrUnreadablePreferences()
    {
        Assert.IsFalse(AboutTheme.UseDarkPalette(() => true, () => throw new InvalidOperationException("Do not read app colors in high contrast.")));
        Assert.IsFalse(AboutTheme.UseDarkPalette(() => false, () => throw new UnauthorizedAccessException("Theme preference is restricted.")));
        Assert.IsTrue(AboutTheme.UseDarkPalette(() => false, () => true));
    }

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

    [TestMethod]
    public void OpenMenuKeepsItsActionsWhenUpdateAvailabilityChanges()
    {
        var available = false;
        var automatic = false;
        var actions = new List<string>();
        var menu = new TrayMenu(() => actions.Add("log"), () => available, () => actions.Add("update"),
            () => automatic, enabled => automatic = enabled,
            () => actions.Add("about"), () => actions.Add("exit"));
        menu.Show(items =>
        {
            CollectionAssert.AreEqual(new[] { "Open log", "Update automatically", "About", "Exit" }, items.Select(item => item.Label).ToArray());
            Assert.IsFalse(items[1].Checked);
            available = true;
            return 3; // The visible item is still About.
        });
        menu.Show(items =>
        {
            CollectionAssert.AreEqual(new[] { "Open log", "Update", "Update automatically", "About", "Exit" }, items.Select(item => item.Label).ToArray());
            return 2;
        });
        menu.Show(_ => { available = false; return 2; });
        menu.Show(_ => 2); // Check the option while no update is offered.
        menu.Show(items =>
        {
            Assert.AreEqual("Update automatically", items.Single(item => item.Checked).Label);
            return 0;
        });
        Assert.IsTrue(automatic);
        menu.Show(_ => 2);
        Assert.IsFalse(automatic);
        CollectionAssert.AreEqual(new[] { "about", "update" }, actions.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WindowsPopupReflectsTheSavedCheckMark(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires a Windows popup menu.");
            return;
        }
        TrayMenu.Item[] items = [new("Open log", () => { }), new("Update automatically", () => { }, enabled)];
        TrayIcon.WithPopup(items, popup =>
        {
            var state = GetMenuState(popup, 2, 0); // MF_BYCOMMAND
            Assert.AreNotEqual(uint.MaxValue, state);
            Assert.AreEqual(enabled, (state & 0x0008) != 0); // MF_CHECKED
            return 0;
        });
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetMenuState(nint menu, uint id, uint flags);

    [TestMethod]
    public void NotificationRetriesUntilExplorerAcceptsItAndReregistersAfterRestart()
    {
        var calls = new List<string>();
        bool ready = false, present = false;
        var notification = new TrayNotification(
            tip => { calls.Add($"modify:{tip}"); return present; },
            name => { calls.Add($"add:{name}"); return present = ready; });
        var status = ("connected", 1u);
        notification.Publish(status);
        CollectionAssert.AreEqual(new[] { "modify:connected", "add:AuthBridge" }, calls.ToArray());

        calls.Clear();
        ready = true;
        notification.Publish(status);
        notification.Publish(status);
        CollectionAssert.AreEqual(new[] { "modify:connected", "add:AuthBridge", "modify:connected" }, calls.ToArray());

        calls.Clear();
        present = false;
        notification.Invalidate();
        notification.Publish(status);
        CollectionAssert.AreEqual(new[] { "modify:connected", "add:AuthBridge", "modify:connected" }, calls.ToArray());
    }

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
