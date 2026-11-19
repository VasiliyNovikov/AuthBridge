using AuthBridge.Host;

namespace AuthBridge.Tests;

[TestClass]
public sealed class TrayTests
{
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
