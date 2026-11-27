using AuthBridge.Client;
using AuthBridge.Common;

namespace AuthBridge.Tests;

[TestClass]
public sealed class ClientCommandTests
{
    private static ClientCommandLine Commands(Func<string[], bool, Task<int>>? open = null,
        Func<string, string[], Task<int>>? gcm = null, Func<string[], Task<int>>? nugetPlugin = null,
        Func<string, string, int, string?, Task<int>>? relay = null, Func<int, string, string, Task<int>>? cancel = null)
    {
        static Task<int> Unexpected() => throw new AssertFailedException("Unexpected command invocation.");
        return new(open ?? ((_, _) => Unexpected()), gcm ?? ((_, _) => Unexpected()), nugetPlugin ?? (_ => Unexpected()),
            relay ?? ((_, _, _, _) => Unexpected()), cancel ?? ((_, _, _) => Unexpected()), Cli.Command("--setup-shell", "", Unexpected));
    }

    [TestMethod]
    [DataRow("--help")]
    [DataRow("-h")]
    public void RootHelpExplainsTheBrowserHandoffWithoutOpeningAnything(string option)
    {
        using var output = new StringWriter();
        Assert.AreEqual(0, Commands().Parse([option]).Invoke(new() { Output = output }));
        var help = output.ToString();
        StringAssert.Contains(help, "For AI agents:");
        StringAssert.Contains(help, "authbridge --status");
        StringAssert.Contains(help, "eval \"$(authbridge --env)\"");
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task VersionCommandPrintsTheFullBuildVersionWithoutContactingAHelper()
    {
        using var output = new StringWriter();
        using var redirect = new ConsoleRedirect(output: output);
        Assert.AreEqual(0, await Program.Main(["--version"]));
        Assert.AreEqual(AppVersion.Informational + Environment.NewLine, output.ToString());
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow]
    [DataRow("--dryrun", "not-a-url")]
    [DataRow("--dry-run")]
    [DataRow("https://example.com", "--setup-shell")]
    [DataRow("--dry-run", "--setup-shell")]
    [DataRow("--setup-shell", "extra")]
    public async Task BrowserOptionsFailBeforeTryingToOpenAnything(params string[] args)
    {
        using var errors = new StringWriter();
        using var redirect = new ConsoleRedirect(error: errors);
        Assert.AreEqual(1, await Cli.InvokeAsync(Commands(), args));
        StringAssert.Contains(errors.ToString(), "Usage:");
        Assert.DoesNotContain("For AI agents:", errors.ToString());
    }

    [TestMethod]
    public async Task BrowserReceivesUrlsInOrderAndDryRunSeparately()
    {
        string[] urls = ["https://first.example", "https://second.example"];
        var cli = Commands(open: (arguments, dryRun) =>
        {
            CollectionAssert.AreEqual(urls, arguments);
            return Task.FromResult(dryRun ? 13 : 7);
        });
        Assert.AreEqual(13, await Cli.InvokeAsync(cli, [urls[0], "--dry-run", urls[1]]));
        Assert.AreEqual(7, await Cli.InvokeAsync(cli, urls));
    }

    [TestMethod]
    [DataRow]
    [DataRow("--help", "--version", "-Plugin", "with spaces", "", "@response", "--")]
    [DataRow("--", "payload")]
    public async Task WrappersPassArgumentsVerbatim(params string[] arguments)
    {
        var cli = Commands(gcm: (program, rest) =>
        {
            Assert.AreEqual("/bin/git credential", program);
            CollectionAssert.AreEqual(arguments, rest);
            return Task.FromResult(17);
        }, nugetPlugin: rest =>
        {
            CollectionAssert.AreEqual(arguments, rest);
            return Task.FromResult(23);
        });
        Assert.AreEqual(17, await Cli.InvokeAsync(cli, ["--gcm", "/bin/git credential", .. arguments]));
        Assert.AreEqual(23, await Cli.InvokeAsync(cli, ["--nuget-plugin", .. arguments]));
    }

    [TestMethod]
    [DataRow("__relay", "socket", "session-id", "invalid")]
    [DataRow("__cancel", "65536", "https://login.example/", "reason")]
    [DataRow("__cancel", "0")]
    [DataRow("__relay", "socket", "session-id")]
    public async Task MalformedWorkersFailWithoutOpeningABrowserOrDetaching(params string[] args)
    {
        Assert.AreEqual(1, await Cli.InvokeAsync(Commands(), args));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("42@test pc")]
    public async Task WorkersReceiveTheTypedPortAndTheirOwnArguments(string? expectedOwner)
    {
        var cli = Commands(relay: (socket, session, port, owner) =>
        {
            Assert.AreEqual(("socket", "session-id", 65535), (socket, session, port));
            Assert.AreEqual(expectedOwner, owner);
            return Task.FromResult(17);
        }, cancel: (port, url, reason) =>
        {
            Assert.AreEqual((1, "https://login.example/", "not connected"), (port, url, reason));
            return Task.FromResult(23);
        });
        string[] ownerArgument = expectedOwner is null ? [] : [expectedOwner];
        Assert.AreEqual(17, await Cli.InvokeAsync(cli, ["__relay", "socket", "session-id", "65535", .. ownerArgument]));
        Assert.AreEqual(23, await Cli.InvokeAsync(cli, ["__cancel", "1", "https://login.example/", "not connected"]));
    }
}
