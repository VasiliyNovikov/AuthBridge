using AuthBridge.Host;

namespace AuthBridge.Tests;

[TestClass]
public sealed class TunnelTests
{
    [TestMethod]
    public void ShortFailuresBackOffButAStableConnectionResetsTheDelay()
    {
        var delay = TimeSpan.FromSeconds(2);
        foreach (var seconds in new[] { 4, 8, 16, 32, 60, 60 })
        {
            delay = Tunnel.NextRetryDelay(delay, TimeSpan.FromSeconds(1));
            Assert.AreEqual(TimeSpan.FromSeconds(seconds), delay);
        }
        Assert.AreEqual(TimeSpan.FromSeconds(2), Tunnel.NextRetryDelay(delay, TimeSpan.FromMinutes(2)));
    }
}
