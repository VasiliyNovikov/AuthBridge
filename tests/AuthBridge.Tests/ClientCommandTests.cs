using AuthBridge.Client;

namespace AuthBridge.Tests;

[TestClass]
public sealed class ClientCommandTests
{
    [TestMethod]
    [DoNotParallelize]
    [DataRow("--dryrun", "not-a-url")]
    [DataRow("--dry-run")]
    public async Task BrowserOptionsFailBeforeTryingToOpenAnything(params string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Linux client command.");
            return;
        }
        var previous = Console.Error;
        using var errors = new StringWriter();
        try
        {
            Console.SetError(errors);
            Assert.AreEqual(1, await ClientCommands.RunAsync(args));
            StringAssert.Contains(errors.ToString(), "Usage (Linux)");
        }
        finally
        {
            Console.SetError(previous);
        }
    }

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
