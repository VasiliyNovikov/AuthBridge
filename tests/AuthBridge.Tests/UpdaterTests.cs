using System.CommandLine;
using System.IO.Compression;
using System.Net;
using System.Text;
using AuthBridge.Common;
using AuthBridge.Host;

namespace AuthBridge.Tests;

[TestClass]
public sealed class UpdaterTests
{
    [TestMethod]
    [TestCategory("Mutation")]
    [DataRow("0.1.7", "0.1.6", true)]
    [DataRow("0.1.10", "0.1.9+5e1f0c2", true)]
    [DataRow("0.1.6-local", "0.1.6", false)]
    [DataRow("0.1.6", "0.1.7+build", false)]
    [DataRow("0.1.6", "0.1.7", false)]
    public void ComparesReleaseNumbers(string version, string than, bool newer) => Assert.AreEqual(newer, AppVersion.IsNewer(version, than));

    [TestMethod]
    [TestCategory("Mutation")]
    [DataRow("x86_64\n/home/dev\n1.2.3+build\n", "x64", "1.2.3")]
    [DataRow("aarch64\r\n/home/dev\r\n", "arm64", null)]
    public void RemoteProbeIdentifiesTheClientPackageAndInstalledVersion(string output, string architecture, string? version) =>
        Assert.AreEqual(new RemoteClient.Machine(architecture, "/home/dev", version), RemoteClient.Machine.Parse(output));

    [TestMethod]
    [DataRow("x86_64\n\n/unexpected-third-line\n")]
    [DataRow("unsupported\n/home/dev\n")]
    public void RemoteProbeRejectsInvalidMachineData(string output) =>
        Assert.ThrowsExactly<IOException>(() => RemoteClient.Machine.Parse(output));

    [TestMethod]
    public async Task InstallVerificationCannotSucceedThroughAnotherMachinesTunnel()
    {
        var other = new TunnelStatus("other", "connected", "", DateTimeOffset.UtcNow, Socket: "/other.sock");
        var target = other with { Name = "dev", Socket = "/home/team's dev/bridge.sock" };
        PingResult Status(string state) => new(Protocol.Version, 1, 0, [other, target with { State = state }]);
        var pings = new Queue<PingResult>([Status("connecting"), Status("connected"), Status("connected")]);
        var attempts = 0;
        var result = await RemoteClient.WaitForTunnelAsync("dev", () => Task.FromResult<PingResult?>(pings.Dequeue()), command =>
        {
            Assert.AreEqual("AUTHBRIDGE_SOCKET='/home/team'\\''s dev/bridge.sock' ~/.local/bin/authbridge --status", command);
            return Task.FromResult(++attempts == 1 ? new ProcessResult(1, "", "not yet ready") : new ProcessResult(0, "verified", ""));
        }, TimeSpan.Zero);
        Assert.AreEqual("verified", result);
        Assert.IsEmpty(pings);
    }

    [TestMethod]
    [DataRow("1.2.2", false, true)]
    [DataRow("1.2.3", false, false)]
    [DataRow("1.2.4", true, false)]
    [DataRow(null, false, false)]
    [DataRow(null, true, true)]
    public async Task RemoteSetupAndReconnectPreserveNewerClients(string? installed, bool explicitInstall, bool shouldUpload)
    {
        var machine = new RemoteClient.Machine("arm64", "/home/dev", installed);
        var uploads = new List<string>();
        var reported = await machine.EnsureVersionAsync("1.2.3", arch =>
        {
            uploads.Add(arch);
            return Task.FromResult("1.2.5"); // Another helper installed a newer client during the upload.
        }, explicitInstall);
        Assert.AreEqual(shouldUpload ? "1.2.5" : installed, reported);
        CollectionAssert.AreEqual(shouldUpload ? new[] { "arm64" } : Array.Empty<string>(), uploads.ToArray());
    }

    [TestMethod]
    public async Task RemoteInstallRejectsTruncationOrWrongVersionAndCannotOverwriteANewerClient()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Checks the actual remote install command in a POSIX shell.");
            return;
        }
        var home = Directory.CreateTempSubdirectory("authbridge upload ");
        try
        {
            async Task<ProcessResult> Upload(string advertised, string staged, bool truncate = false)
            {
                var bytes = Encoding.UTF8.GetBytes($"#!/bin/sh\nprintf '%s\\n' '{staged}+build'\n");
                using var input = new MemoryStream(truncate ? bytes[..^1] : bytes);
                var command = "export HOME=\"$1\"\n" + Ssh.Arguments(["unused"], RemoteClient.UploadCommand(bytes.Length, advertised))[^1];
                return await Processes.RunAsync("sh", ["-c", command, "sh", home.FullName], input);
            }
            Assert.AreEqual("1.0.0", (await Upload("1.0.0", "1.0.0")).OutputOrThrow("install").Trim());
            var dest = Path.Combine(home.FullName, ".local", "bin", "authbridge");
            var original = File.ReadAllBytes(dest);
            Assert.AreNotEqual(0, (await Upload("2.0.0", "2.0.0", truncate: true)).ExitCode);
            Assert.AreNotEqual(0, (await Upload("2.0.0", "1.9.9")).ExitCode);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(dest));

            var concurrent = await Task.WhenAll(Upload("1.2.0", "1.2.0"), Upload("1.10.0", "1.10.0"));
            foreach (var result in concurrent)
                result.OutputOrThrow("concurrent install");
            Assert.AreEqual("1.10.0", (await Upload("1.2.0", "1.2.0")).OutputOrThrow("no downgrade").Trim());
            Assert.AreEqual("1.10.0+build", (await Processes.RunAsync(dest, ["--version"])).OutputOrThrow("installed version").Trim());
        }
        finally
        {
            home.Delete(recursive: true);
        }
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
    [DataRow(302)]
    [DataRow(503)]
    public async Task ChecksOnlyTheLatestReleaseHeadersAndRequiresARedirect(int status)
    {
        await using var server = HttpServer.Start(new IPEndPoint(IPAddress.Loopback, 0), request => Task.FromResult(
            request is { Method: "HEAD", Target: "/releases/latest" }
                ? new HttpResponse(status, [new("Location", "/releases/tag/v1.2.3")], [])
                : new HttpResponse(405, [], [])));
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/releases/"),
        };
        if (status == 302)
            Assert.AreEqual("1.2.3", await GitHubReleases.ReadLatestAsync(http, CancellationToken.None));
        else
            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => GitHubReleases.ReadLatestAsync(http, CancellationToken.None));
    }

    [TestMethod]
    public async Task RechecksAtFiveMinutesWithoutOverlappingAnUnfinishedCheck()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        var updates = new UpdateCheck(_ => ++checks == 1 ? Task.FromResult("0.1.10") : pending.Task, "0.1.9");
        var now = DateTime.UtcNow;

        await updates.RefreshAsync(now);
        await updates.RefreshAsync(now.AddMinutes(5).AddTicks(-1));
        Assert.IsTrue(updates.Available);
        Assert.AreEqual(1, checks);

        var second = updates.RefreshAsync(now.AddMinutes(5));
        Assert.AreSame(second, updates.RefreshAsync(now.AddMinutes(10)));
        Assert.AreEqual(2, checks);
        Assert.IsTrue(updates.Available);
        pending.SetResult("0.1.9");
        await second;
        Assert.IsFalse(updates.Available);
    }

    [TestMethod]
    public async Task OfflineChecksBackOffWithoutLosingAKnownUpdateAndRecoveryRestoresFiveMinutes()
    {
        var checks = 0;
        var offline = false;
        var updates = new UpdateCheck(_ =>
        {
            checks++;
            return offline ? Task.FromException<string>(new HttpRequestException("offline")) : Task.FromResult("0.1.10");
        }, "0.1.9");
        var now = DateTime.UtcNow;
        await updates.RefreshAsync(now);
        offline = true;
        foreach (var minutes in new[] { 5, 10, 20, 40, 60, 60 })
        {
            var before = checks;
            now = now.AddMinutes(minutes);
            await updates.RefreshAsync(now.AddTicks(-1));
            Assert.AreEqual(before, checks);
            await updates.RefreshAsync(now);
            Assert.AreEqual(before + 1, checks);
            Assert.IsTrue(updates.Available);
        }
        offline = false;
        now = now.AddHours(1);
        var beforeRecovery = checks;
        await updates.RefreshAsync(now);
        await updates.RefreshAsync(now.AddMinutes(5));
        Assert.AreEqual(beforeRecovery + 2, checks);
    }

    [TestMethod]
    public async Task InstallingAReplacementWaitsForShutdownButAnInPlaceInstallLeavesTheHelperAlone()
    {
        var root = Directory.CreateTempSubdirectory("authbridge-replace-");
        try
        {
            var source = root.CreateSubdirectory("source").FullName;
            var target = root.CreateSubdirectory("installed").FullName;
            var executable = Path.Combine(target, "authbridge.exe");
            File.WriteAllText(executable, "running");
            File.WriteAllText(Path.Combine(source, "authbridge.exe"), "replacement");
            var package = new InstalledPackage(target);
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var install = package.InstallFromAsync(source, () => stopped.Task);

            Assert.IsFalse(install.IsCompleted);
            Assert.AreEqual("running", File.ReadAllText(executable));
            stopped.SetResult();
            Assert.IsTrue(await install);
            Assert.AreEqual("replacement", File.ReadAllText(executable));
            Assert.IsFalse(await package.InstallFromAsync(Path.Combine(target, "."), () => throw new AssertFailedException("Unexpected helper shutdown.")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingPackageExecutableDoesNotStopTheRunningHelper()
    {
        var source = Directory.CreateTempSubdirectory("authbridge-incomplete-");
        try
        {
            var package = new InstalledPackage(Path.Combine(source.FullName, "target"));
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
                package.InstallFromAsync(source.FullName, () => throw new AssertFailedException("Unexpected helper shutdown.")));
        }
        finally
        {
            source.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task ReplacingAPackageKeepsAnOpenExecutableReadable()
    {
        var directory = Directory.CreateTempSubdirectory("authbridge-install-");
        try
        {
            var path = Path.Combine(directory.FullName, "authbridge.exe");
            File.WriteAllBytes(path, [1]);
            using var running = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var package = new InstalledPackage(directory.FullName);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                package.InstallAsync(ReleaseZip("authbridge.exe"), () => throw new AssertFailedException("Incomplete archive stopped the helper.")));
            CollectionAssert.AreEqual(new byte[] { 1 }, File.ReadAllBytes(path));

            var release = ReleaseZip("authbridge.exe", "authbridgew.exe", "authbridge-linux-x64", "authbridge-linux-arm64", "README.md", "LICENSE");
            Assert.IsFalse(await package.InstallAsync(release, () => Task.FromResult(false)));
            CollectionAssert.AreEqual(new byte[] { 1 }, File.ReadAllBytes(path));
            Assert.IsTrue(await package.InstallAsync(release, () => Task.FromResult(true)));

            Assert.AreEqual(1, running.ReadByte());
            CollectionAssert.AreEqual(new byte[] { 2 }, File.ReadAllBytes(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static byte[] ReleaseZip(params string[] files)
    {
        using var contents = new MemoryStream();
        using (var zip = new ZipArchive(contents, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                using var entry = zip.CreateEntry(file).Open();
                entry.WriteByte(2);
            }
        }
        return contents.ToArray();
    }

    [TestMethod]
    public void LogonAndTrayUpdateUseTheGuiExecutableWhenAvailable()
    {
        var folder = Directory.CreateTempSubdirectory("authbridge launch ");
        try
        {
            var package = new InstalledPackage(folder.FullName);
            var console = Path.Combine(folder.FullName, "authbridge.exe");
            Assert.AreEqual(console, package.HelperExecutable);
            Assert.AreEqual($"\"{console}\" start", package.LogonCommand);
            AssertWindowlessUpdate(console);

            var gui = Path.Combine(folder.FullName, "authbridgew.exe");
            File.WriteAllBytes(gui, []);
            Assert.AreEqual(gui, package.HelperExecutable);
            Assert.AreEqual($"\"{gui}\" serve", package.LogonCommand);
            AssertWindowlessUpdate(gui);

            void AssertWindowlessUpdate(string executable)
            {
                var start = package.TrayUpdateStartInfo();
                Assert.AreEqual(executable, start.FileName);
                CollectionAssert.AreEqual(new[] { "update", "--from-tray" }, start.ArgumentList.ToArray());
                Assert.IsFalse(start.UseShellExecute);
                Assert.IsTrue(start.CreateNoWindow);
                Assert.IsFalse(start.RedirectStandardOutput || start.RedirectStandardError);
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task TrayUpdateRecordsProgressAndFailureWithoutWritingToTheConsole()
    {
        var directory = Directory.CreateTempSubdirectory("authbridge-tray-update-");
        var path = Path.Combine(directory.FullName, "update.log");
        var output = Console.Out;
        var error = Console.Error;
        using var terminal = new StringWriter();
        try
        {
            Console.SetOut(terminal);
            Console.SetError(terminal);
            var fail = false;
            var root = new RootCommand { TrayUpdate.CreateCommand(directory.FullName, automatic =>
            {
                Console.WriteLine(automatic ? "automatic download" : "downloading");
                return fail ? Task.FromException<int>(new IOException("installation failed")) : Task.FromResult(0);
            }) };
            Assert.AreEqual(0, await Cli.InvokeAsync(root, ["update"]));
            Assert.AreEqual("downloading" + Environment.NewLine, terminal.ToString());
            Assert.IsFalse(File.Exists(path));
            terminal.GetStringBuilder().Clear();

            fail = true;
            var package = new InstalledPackage(directory.FullName);
            var arguments = package.TrayUpdateStartInfo().ArgumentList.ToArray();
            Assert.AreEqual(1, await Cli.InvokeAsync(root, arguments));
            Assert.AreEqual("", terminal.ToString());
            CollectionAssert.AreEqual(new[] { "downloading", "authbridge: installation failed" }, File.ReadAllLines(path));

            Assert.AreEqual(1, await Cli.InvokeAsync(root, package.TrayUpdateStartInfo(automatic: true).ArgumentList.ToArray()));
            CollectionAssert.AreEqual(new[] { "automatic download", "authbridge: installation failed" }, File.ReadAllLines(path));
            var unavailable = new RootCommand { TrayUpdate.CreateCommand(path, _ => throw new AssertFailedException("No log, no update.")) };
            Assert.AreEqual(TrayUpdate.LogUnavailable, await Cli.InvokeAsync(unavailable, arguments));
            Assert.AreEqual("", terminal.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task AnAutomaticUpdateCannotRaceAnUpdateStartedInATerminal()
    {
        var directory = Directory.CreateTempSubdirectory("authbridge-exclusive-update-");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var root = new RootCommand { TrayUpdate.CreateCommand(directory.FullName, _ =>
        {
            started.TrySetResult();
            return ++runs == 1 ? finish.Task : Task.FromResult(0);
        }) };
        var manual = Cli.InvokeAsync(root, ["update"]);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var args = new InstalledPackage(directory.FullName).TrayUpdateStartInfo(automatic: true).ArgumentList.ToArray();
            Assert.AreEqual(1, await Cli.InvokeAsync(root, args));
            Assert.AreEqual(1, runs);
            finish.SetResult(0);
            Assert.AreEqual(0, await manual);
            Assert.AreEqual(0, await Cli.InvokeAsync(root, args));
            Assert.AreEqual(2, runs);
        }
        finally
        {
            finish.TrySetResult(0);
            await manual;
            directory.Delete(recursive: true);
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
