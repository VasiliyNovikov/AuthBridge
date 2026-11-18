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
}
