using AuthBridge.Common;
using AuthBridge.Host;

namespace AuthBridge.Tests;

[TestClass]
public sealed class TrayTests
{
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
        var actions = new List<string>();
        var menu = new TrayMenu(() => actions.Add("log"), () => available, () => actions.Add("update"),
            () => actions.Add("about"), () => actions.Add("exit"));
        menu.Show(labels =>
        {
            CollectionAssert.AreEqual(new[] { "Open log", "About", "Exit" }, labels.ToArray());
            available = true;
            return 2; // The visible item is still About, not Update.
        });
        menu.Show(labels =>
        {
            CollectionAssert.AreEqual(new[] { "Open log", "Update", "About", "Exit" }, labels.ToArray());
            return 2;
        });
        menu.Show(_ => { available = false; return 2; });
        menu.Show(_ => 0); // Dismissing the menu must not run anything.
        CollectionAssert.AreEqual(new[] { "about", "update" }, actions.ToArray());
    }

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
}
