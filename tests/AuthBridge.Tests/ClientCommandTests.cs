using AuthBridge.Client;
using AuthBridge.Common;

namespace AuthBridge.Tests;

[TestClass]
public sealed class ClientCommandTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task VersionCommandPrintsTheFullBuildVersionWithoutContactingAHelper()
    {
        var previous = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.AreEqual(0, await Program.Main(["--version"]));
            Assert.AreEqual(AppVersion.Informational + Environment.NewLine, output.ToString());
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

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
            Assert.AreEqual(1, await Program.Main(args));
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
