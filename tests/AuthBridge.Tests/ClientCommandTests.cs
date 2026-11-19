using AuthBridge.Client;

namespace AuthBridge.Tests;

[TestClass]
public sealed class ClientCommandTests
{
    [TestMethod]
    [DataRow("__relay", "socket", "session-id", "invalid")]
    [DataRow("__cancel", "65536", "https://login.example/", "reason")]
    [DataRow("__cancel", "0")]
    public async Task MalformedWorkersFailWithoutOpeningABrowserOrDetaching(params string[] args)
    {
        if (OperatingSystem.IsLinux())
            Assert.AreEqual(1, await ClientCommands.RunAsync(args));
        else
            Assert.Inconclusive("Linux client command.");
    }
}
