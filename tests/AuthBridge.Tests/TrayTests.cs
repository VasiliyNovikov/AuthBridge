using AuthBridge.Common;
using AuthBridge.Host;

namespace AuthBridge.Tests;

[TestClass]
public sealed class TrayTests
{
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
        var original = menu.Open();
        CollectionAssert.AreEqual(new[] { "Open log", "About", "Exit" }, original.Select(item => item.Label).ToArray());
        available = true;
        original[1].Invoke();
        var update = menu.Open().Single(item => item.Label == "Update");
        update.Invoke();
        available = false;
        update.Invoke();
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
