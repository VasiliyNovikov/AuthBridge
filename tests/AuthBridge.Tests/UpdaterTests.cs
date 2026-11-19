using System.IO.Compression;
using AuthBridge.Common;
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
    public void ComparesReleaseNumbers(string version, string than, bool newer) => Assert.AreEqual(newer, AppVersion.IsNewer(version, than));

    [TestMethod]
    [DataRow("x86_64\n/home/dev\n1.2.3+build\n", "x64", "1.2.3")]
    [DataRow("aarch64\r\n/home/dev\r\n", "arm64", null)]
    public void RemoteProbeIdentifiesTheClientPackageAndInstalledVersion(string output, string architecture, string? version) =>
        Assert.AreEqual(new RemoteClient.Machine(architecture, "/home/dev", version), RemoteClient.Machine.Parse(output));

    [TestMethod]
    public void IncompleteRemoteProbeDoesNotShiftFields() =>
        Assert.ThrowsExactly<IOException>(() => RemoteClient.Machine.Parse("x86_64\n\n/unexpected-third-line\n"));

    [TestMethod]
    [DataRow("1.2.2", true)]
    [DataRow("1.2.3", false)]
    [DataRow("1.2.4", false)]
    [DataRow(null, false)]
    public async Task ConnectingOnlyUploadsToAnInstalledOlderClient(string? installed, bool shouldUpload)
    {
        var machine = new RemoteClient.Machine("arm64", "/home/dev", installed);
        var uploads = new List<string>();
        var reported = await machine.UpgradeIfOlderAsync("1.2.3", arch =>
        {
            uploads.Add(arch);
            return Task.CompletedTask;
        });
        Assert.AreEqual(shouldUpload ? "1.2.3" : installed, reported);
        CollectionAssert.AreEqual(shouldUpload ? new[] { "arm64" } : Array.Empty<string>(), uploads.ToArray());
    }

    [TestMethod]
    public async Task UpdateWaitsForConnectedClientVersionsButNotOfflineMachines()
    {
        var online = new TunnelStatus("online", "starting", "", DateTimeOffset.UtcNow);
        var offline = online with { Name = "offline", State = "disconnected" };
        PingResult Status(TunnelStatus tunnel) => new(Protocol.Version, 1, 0, [tunnel, offline]);
        var ready = Status(online with { State = "connected", Client = "1.2.3" });
        var replies = new Queue<PingResult>([Status(online), Status(online with { State = "connected" }), ready]);

        var last = await RemoteClient.WaitForChecksAsync(() => Task.FromResult<PingResult?>(replies.Dequeue()), TimeSpan.Zero);

        Assert.AreSame(ready, last);
        Assert.IsEmpty(replies);
    }

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

    [TestMethod]
    public void ReplacingAPackageKeepsAnOpenExecutableReadable()
    {
        var directory = Directory.CreateTempSubdirectory("authbridge-install-");
        try
        {
            var path = Path.Combine(directory.FullName, "authbridge.exe");
            File.WriteAllBytes(path, [1]);
            using var running = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var contents = new MemoryStream();
            using (var zip = new ZipArchive(contents, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = zip.CreateEntry("authbridge.exe").Open())
                entry.WriteByte(2);

            new InstalledPackage(directory.FullName).Install(contents.ToArray());

            Assert.AreEqual(1, running.ReadByte());
            CollectionAssert.AreEqual(new byte[] { 2 }, File.ReadAllBytes(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void LogonRunsTheGuiHelperDirectlyOrDetachesThroughTheConsoleExecutable()
    {
        var folder = Directory.CreateTempSubdirectory("authbridge launch ");
        try
        {
            var package = new InstalledPackage(folder.FullName);
            var console = Path.Combine(folder.FullName, "authbridge.exe");
            Assert.AreEqual(console, package.HelperExecutable);
            Assert.AreEqual($"\"{console}\" start", package.LogonCommand);

            var gui = Path.Combine(folder.FullName, "authbridgew.exe");
            File.WriteAllBytes(gui, []);
            Assert.AreEqual(gui, package.HelperExecutable);
            Assert.AreEqual($"\"{gui}\" serve", package.LogonCommand);
        }

        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void InstallingTwicePreservesTheOriginalUserPath()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AuthBridge test");
        var package = new InstalledPackage(folder);
        var alreadyPresent = $"%SDK_HOME%;\"{folder.ToUpperInvariant()}{Path.DirectorySeparatorChar}\";";
        Assert.AreEqual(alreadyPresent, package.IncludeInUserPath(alreadyPresent));
        var added = package.IncludeInUserPath("%SDK_HOME%;");
        Assert.AreEqual($"%SDK_HOME%;{folder}", added);
        Assert.AreEqual(added, package.IncludeInUserPath(added));
    }
}
