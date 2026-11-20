using System.Diagnostics;
using System.Text.Json;
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
    public async Task StoppingATunnelClosesItsChildAndCompletesItsLifetime()
    {
        // Like the remote tunnel command, this benign local process exits when standard input closes.
        using var child = Process.Start(Processes.StartInfo(OperatingSystem.IsWindows() ? "sort.exe" : "cat", []))!;
        using var observer = Process.GetProcessById(child.Id);
        using var tunnel = new Tunnel(new Target("test", ["host"], "/home/test machine/.authbridge"), 12345, args =>
        {
            Assert.IsTrue(args.Any(arg => arg.EndsWith(":127.0.0.1:12345", StringComparison.Ordinal)));
            return child;
        });
        try
        {
            var lifetime = tunnel.RunAsync();
            tunnel.Dispose();
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
}
