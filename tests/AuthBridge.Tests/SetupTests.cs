using AuthBridge.Client;
using AuthBridge.Common;

namespace AuthBridge.Tests;

[TestClass]
public sealed class SetupTests
{
    [TestMethod]
    public void AddsTheShellBlockOnceAndUpdatesItInPlace()
    {
        var added = Setup.WithBlock("export A=1\n", "export BROWSER=old");
        var updated = Setup.WithBlock(added + "export B=2\n", "export BROWSER=new");

        Assert.AreEqual("export A=1\n\n# >>> authbridge >>>\nexport BROWSER=old\n# <<< authbridge <<<\n", added);
        Assert.AreEqual("export A=1\n\n# >>> authbridge >>>\nexport BROWSER=new\n# <<< authbridge <<<\nexport B=2\n", updated);
    }

    [TestMethod]
    public void WrapsOnlyGitCredentialManagerHelpers()
    {
        string[] helpers = ["", "/usr/bin/git-credential-manager", "manager", "store", "/bin/wb --gcm /usr/bin/git-credential-manager"];

        var wrapped = Setup.WrapGcmHelpers(helpers, "/bin/wb", name => name == "git-credential-manager" ? "/usr/bin/git-credential-manager" : null);

        CollectionAssert.AreEqual(
            new[] { "", "/bin/wb --gcm /usr/bin/git-credential-manager", "/bin/wb --gcm /usr/bin/git-credential-manager", "store", helpers[4] },
            wrapped);
    }

    [TestMethod]
    [TestCategory("Mutation")]
    [DataRow("")]
    [DataRow("/my other browser")]
    public async Task BrowserExportPassesTheChosenLiteralPathToChildShells(string existing)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Requires a POSIX shell.");
            return;
        }
        const string executable = "/home/team's $tools/authbridge";
        var script = $"BROWSER=$1; {Setup.ExportBrowser(executable)}; sh -c 'printf %s \"$BROWSER\"'";
        var result = await Processes.RunAsync("sh", ["-c", script, "sh", existing]);
        Assert.AreEqual(existing.Length == 0 ? executable : existing, result.OutputOrThrow("shell export failed"));
    }

    [TestMethod]
    public async Task MissingGitHelpersNeedNoWrites()
    {
        var calls = new List<string[]>();
        var result = await Setup.ConfigureGcmAsync("/bin/authbridge", _ => null, args =>
        {
            calls.Add(args);
            return Task.FromResult(new ProcessResult(1, "", ""));
        });

        Assert.AreEqual(0, result);
        CollectionAssert.AreEqual(new[] { "config", "--global", "--get-all", "credential.helper" }, calls.Single());
    }

    [TestMethod]
    [DataRow("--get-all", 1)]
    [DataRow("--unset-all", 2)]
    [DataRow("--add", 3)]
    public async Task FailedGitConfigurationStopsAtTheFailingCommand(string failingOption, int expectedCalls)
    {
        var calls = 0;
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => Setup.ConfigureGcmAsync("/bin/authbridge", _ => null, args =>
        {
            calls++;
            var result = args[2] == failingOption ? new ProcessResult(1, "", "config unavailable")
                : new ProcessResult(0, args[2] == "--get-all" ? "/bin/git-credential-manager\nstore\n" : "", "");
            return Task.FromResult(result);
        }));

        StringAssert.Contains(error.Message, "config unavailable");
        Assert.AreEqual(expectedCalls, calls);
    }
}
