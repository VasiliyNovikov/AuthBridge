using AuthBridge.Host;

namespace AuthBridge.Tests;

[TestClass]
public sealed class TrayTests
{
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
