using AuthBridge.Client;

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
}
