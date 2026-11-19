using AuthBridge.Host;

namespace AuthBridge.Tests;

[TestClass]
public sealed class UpdaterTests
{
    [TestMethod]
    [DataRow("0.1.7", "0.1.6", true)]
    [DataRow("0.1.10", "0.1.9+5e1f0c2", true)]
    [DataRow("0.1.6-local", "0.1.6", false)]
    [DataRow("0.1.6", "0.1.7", false)]
    public void ComparesReleaseNumbers(string version, string than, bool newer) => Assert.AreEqual(newer, Updater.IsNewer(version, than));

    [TestMethod]
    public async Task OffersOnlyConfirmedUpdatesAndCachesTheCheck()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        var updates = new UpdateCheck(_ => ++checks == 1 ? pending.Task : Task.FromResult("0.1.9"), "0.1.9");
        var now = DateTime.UtcNow;

        var first = updates.RefreshAsync(now);
        Assert.IsFalse(updates.Available);
        pending.SetResult("0.1.10");
        await first;
        await updates.RefreshAsync(now.AddMinutes(1));
        Assert.IsTrue(updates.Available);
        Assert.AreEqual(1, checks);

        await updates.RefreshAsync(now.AddHours(1));
        Assert.IsFalse(updates.Available);
    }

    [TestMethod]
    public async Task AnOfflineCheckCanBeRetried()
    {
        var checks = 0;
        var updates = new UpdateCheck(_ => ++checks == 1
            ? Task.FromException<string>(new HttpRequestException("offline"))
            : Task.FromResult("0.1.10"), "0.1.9");
        var now = DateTime.UtcNow;

        await updates.RefreshAsync(now);
        Assert.IsFalse(updates.Available);
        await updates.RefreshAsync(now.AddHours(1));
        Assert.IsTrue(updates.Available);
    }
}
