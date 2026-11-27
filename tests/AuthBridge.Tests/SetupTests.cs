using AuthBridge.Client;
using AuthBridge.Common;

namespace AuthBridge.Tests;

[TestClass]
public sealed class SetupTests
{
    [TestMethod]
    [DataRow(false, false, ".profile")]
    [DataRow(false, true, ".bash_login")]
    [DataRow(true, true, ".bash_profile")]
    public void ShellSetupUpdatesOnlyTheActiveLoginFileAndBashrc(bool bashProfile, bool bashLogin, string active)
    {
        using var home = new TempDirectory("authbridge-shell-");
        const string original = "export KEEP=this\n";
        const string executable = "/home/team's tools/authbridge";
        List<string> profiles = [".profile"];
        if (bashProfile) profiles.Add(".bash_profile");
        if (bashLogin) profiles.Add(".bash_login");
        foreach (var profile in profiles)
            File.WriteAllText(Path.Combine(home.FullName, profile), original);

        using var output = new StringWriter();
        Setup.ConfigureShell(home.FullName, executable, output);
        var export = Setup.ExportBrowser(executable);
        var login = Path.Combine(home.FullName, active);
        var bashrc = Path.Combine(home.FullName, ".bashrc");
        Assert.AreEqual(Setup.WithBlock(original, export), File.ReadAllText(login));
        Assert.AreEqual(Setup.WithBlock("", export), File.ReadAllText(bashrc));
        foreach (var file in profiles.Where(name => name != active))
            Assert.AreEqual(original, File.ReadAllText(Path.Combine(home.FullName, file)));
        StringAssert.Contains(output.ToString(), $"eval \"$({PosixShell.Quote(executable)} --env)\"");

        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(login, stamp);
        File.SetLastWriteTimeUtc(bashrc, stamp);
        Setup.ConfigureShell(home.FullName, executable, TextWriter.Null);
        Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(login));
        Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(bashrc));
    }

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
            new[] { "", "!'/bin/wb' --gcm '/usr/bin/git-credential-manager'", "!'/bin/wb' --gcm '/usr/bin/git-credential-manager'", "store", helpers[4] },
            wrapped);
    }

    [TestMethod]
    [TestCategory("Mutation")]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = "Requires a POSIX shell.")]
    [DataRow("")]
    [DataRow("/my other browser")]
    public async Task BrowserExportPassesTheChosenLiteralPathToChildShells(string existing)
    {
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
        CollectionAssert.AreEqual(new[] { "config", "--global", "--get-all", "--null", "credential.helper" }, calls.Single());
    }

    [TestMethod]
    public async Task RewritingGitHelpersKeepsUnrelatedMultilineEntriesAndTrailingResets()
    {
        var added = new List<string>();
        await Setup.ConfigureGcmAsync("/my tools/authbridge", _ => "/team's tools/git-credential-manager", args =>
        {
            if (args[2] == "--get-all")
                return Task.FromResult(new ProcessResult(0, "manager --flag 'two  words'\0!keep\nthis\0\0", ""));
            if (args[2] == "--add")
                added.Add(args[^1]);
            return Task.FromResult(new ProcessResult(0, "", ""));
        });
        CollectionAssert.AreEqual(new[] { "!'/my tools/authbridge' --gcm '/team'\\''s tools/git-credential-manager' --flag 'two  words'", "!keep\nthis", "" }, added);
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
                : new ProcessResult(0, args[2] == "--get-all" ? "/bin/git-credential-manager\0store\0" : "", "");
            return Task.FromResult(result);
        }));

        StringAssert.Contains(error.Message, "config unavailable");
        Assert.AreEqual(expectedCalls, calls);
    }
}
