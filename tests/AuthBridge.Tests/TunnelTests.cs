using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AuthBridge.Common;
using AuthBridge.Host;

namespace AuthBridge.Tests;

[TestClass]
public sealed class TunnelTests
{
    [TestMethod]
    public void HostSettingsRoundTripTheExistingConfigFileFormat()
    {
        using var file = JsonDocument.Parse("""
            { "targets": [{ "name": "dev", "ssh": ["-p","2222","dev"], "remoteDir": "/home/dev" }], "browserCommand": ["browser", "{url}"] }
            """);
        var config = file.RootElement.Deserialize(HostConfigJson.Default.HostConfig);
        Assert.IsNotNull(config);
        Assert.AreEqual("dev", config.Targets.Single().Name);
        Assert.IsTrue(JsonElement.DeepEquals(file.RootElement, JsonSerializer.SerializeToElement(config, HostConfigJson.Default.HostConfig)));
    }

    [TestMethod]
    public void ConfigReloadAcceptsABomAndKeepsTheLastGoodSettingsUntilAnEditIsValid()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"browserCommand":["first","{url}"]}""", new UTF8Encoding(true));
            var warnings = new List<string>();
            var settings = new HostSettings(path, warnings.Add);
            Assert.AreEqual("first", settings.Reload()!.BrowserCommand![0]);
            var stamp = File.GetLastWriteTimeUtc(path).AddMinutes(1);
            File.WriteAllText(path, """{"targets":[""");
            File.SetLastWriteTimeUtc(path, stamp);
            Assert.IsNull(settings.Reload());
            Assert.IsNull(settings.Reload());
            Assert.AreEqual("first", settings.Current.BrowserCommand![0]);
            StringAssert.Contains(warnings.Single(), path);

            File.WriteAllText(path, """{"targets":[null]}""");
            Assert.ThrowsExactly<InvalidDataException>(() => HostConfig.Load(path));
            File.WriteAllText(path, """{"browserCommand":["fixed","{url}"]}""");
            File.SetLastWriteTimeUtc(path, stamp);
            Assert.AreEqual("fixed", settings.Reload()!.BrowserCommand![0]);
            Assert.IsNull(settings.Reload());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task StoppingATunnelClosesItsChildAndCompletesItsLifetime()
    {
        // Like the remote tunnel command, this benign local process exits when standard input closes.
        using var child = Process.Start(Processes.StartInfo(OperatingSystem.IsWindows() ? "sort.exe" : "cat", []))!;
        using var observer = Process.GetProcessById(child.Id);
        await using var tunnel = new Tunnel(new Target("test", ["host"], "/home/test machine/.authbridge"), 12345, args =>
        {
            Assert.IsTrue(args.Any(arg => arg.EndsWith(":127.0.0.1:12345", StringComparison.Ordinal)));
            return child;
        }, _ => throw new AssertFailedException("A silent SSH process is not a ready tunnel."));
        try
        {
            var lifetime = tunnel.RunAsync();
            await tunnel.DisposeAsync();
            await lifetime.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(observer.HasExited);
            Assert.AreEqual("stopped", tunnel.Status.State);
        }
        finally
        {
            if (!observer.HasExited)
                observer.Kill();
        }
    }

    [TestMethod]
    public void ShortFailuresBackOffButAStableConnectionResetsTheDelay()
    {
        var delay = TimeSpan.FromSeconds(2);
        foreach (var seconds in new[] { 4, 8, 16, 32, 60, 60 })
        {
            delay = Tunnel.NextRetryDelay(delay, TimeSpan.FromSeconds(1));
            Assert.AreEqual(TimeSpan.FromSeconds(seconds), delay);
        }
        Assert.AreEqual(TimeSpan.FromSeconds(2), Tunnel.NextRetryDelay(delay, TimeSpan.FromMinutes(2)));
    }

    [TestMethod]
    public async Task EachReadyConnectionRechecksItsClientAndDisconnectCancelsAnInFlightCheck()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var children = Channel.CreateUnbounded<Process>();
        var checks = Channel.CreateUnbounded<CancellationToken>();
        var count = 0;
        Process Start(string[] args)
        {
            CollectionAssert.Contains(args, "ExitOnForwardFailure=yes");
            StringAssert.Contains(args[^1], Tunnel.ReadyMarker);
            var echo = OperatingSystem.IsWindows()
                ? Processes.StartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "while ($null -ne ($s=[Console]::ReadLine())) { [Console]::WriteLine($s) }"])
                : Processes.StartInfo("cat", []);
            var child = Process.Start(echo)!;
            children.Writer.TryWrite(child);
            return child;
        }
        async Task<string?> Check(CancellationToken cancel)
        {
            checks.Writer.TryWrite(cancel);
            if (++count == 2)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancel);
            return "1.0";
        }
        await using var tunnel = new Tunnel(new Target("test", ["fake"], "/fake"), 12345, Start, Check);
        var lifetime = tunnel.RunAsync();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var child = await children.Reader.ReadAsync(limit.Token);
                await child.StandardInput.WriteLineAsync("a login banner");
                Assert.AreEqual("connecting", tunnel.Status.State);
                Assert.IsFalse(checks.Reader.TryPeek(out _));
                await child.StandardInput.WriteLineAsync(Tunnel.ReadyMarker);
                var connection = await checks.Reader.ReadAsync(limit.Token);
                Assert.AreEqual("connected", tunnel.Status.State);
                Assert.IsNotNull(tunnel.Status.Socket);
                child.StandardInput.Close();
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var callback = connection.Register(() => disconnected.TrySetResult());
                await disconnected.Task.WaitAsync(limit.Token);
            }
        }
        finally
        {
            await tunnel.DisposeAsync();
            await lifetime.WaitAsync(TimeSpan.FromSeconds(8));
        }
        Assert.AreEqual("stopped", tunnel.Status.State);
    }

    [TestMethod]
    public async Task AnUploadSourceReadErrorFailsPromptlyEvenWhenTheChildWaitsForEof()
    {
        using var input = new FailingReadStream();
        var copy = Processes.RunAsync(OperatingSystem.IsWindows() ? "sort.exe" : "cat", [], input);
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => copy.WaitAsync(TimeSpan.FromSeconds(5)));
        StringAssert.Contains(error.Message, "source read failed");
    }

    private sealed class FailingReadStream() : MemoryStream([1, 2, 3])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default) =>
            Position == Length ? ValueTask.FromException<int>(new IOException("source read failed")) : base.ReadAsync(buffer, cancel);
    }
}
