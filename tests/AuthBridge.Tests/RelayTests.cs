using System.Net;
using System.Net.Sockets;
using System.Text;
using AuthBridge.Client;
using AuthBridge.Common;
using AuthBridge.Host;

namespace AuthBridge.Tests;

[TestClass]
public sealed class RelayTests
{
    [TestMethod]
    public async Task PingReportsLiveHostStateFromTheFirstRequest()
    {
        TunnelStatus[] tunnels = [new("dev", "connected", "", DateTimeOffset.UtcNow)];
        int? idle = 5;
        await using var helper = new Helper(_ => Assert.Fail("Ping does not open a browser."), () => tunnels, () => idle);
        helper.Start(0);
        using var bridge = ControlClient.ForPort(helper.Port);

        var first = await bridge.PingAsync(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(first);
        Assert.AreEqual("dev", first.Tunnels.Single().Name);
        Assert.AreEqual(5, first.IdleSeconds);

        tunnels = [];
        idle = null;
        var next = await bridge.PingAsync(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(next);
        Assert.IsEmpty(next.Tunnels);
        Assert.IsNull(next.IdleSeconds);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RelaysTheBrowserRedirectToTheApplication(bool tunnelReconnects)
    {
        await using var application = HttpServer.Start(new IPEndPoint(IPAddress.Loopback, 0), request => Task.FromResult(new HttpResponse(200,
            [new("Set-Cookie", "a=1"), new("Set-Cookie", "b=2")],
            Encoding.UTF8.GetBytes($"{request.Method} {request.Target} {Encoding.UTF8.GetString(request.Body)}"))));
        await using var helper = StartHelper();
        using var bridge = ControlClient.ForPort(helper.Port);
        // A dropped tunnel: nothing listens there any more, and the relay must find the helper again.
        using var dropped = ControlClient.ForPort(FreePort());
        var relayPort = FreePort();

        var (status, session, _) = await bridge.OpenAsync(Open(relayPort));
        using var stop = new CancellationTokenSource();
        var relay = Relay.RunAsync(tunnelReconnects ? dropped : bridge, session!, application.Port, stop.Token,
            () => Task.FromResult<ControlClient?>(ControlClient.ForPort(helper.Port)));
        using var browser = new HttpClient();
        using var response = await browser.PostAsync($"http://localhost:{relayPort}/callback?x=1", new StringContent("code=c&state=s"));

        Assert.AreEqual(200, status);
        Assert.AreEqual("POST /callback?x=1 code=c&state=s", await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(new[] { "a=1", "b=2" }, response.Headers.GetValues("Set-Cookie").ToArray());
        await stop.CancelAsync();
        await relay;
    }

    [TestMethod]
    public async Task RejectsAPortThatIsInUse()
    {
        using var occupant = new TcpListener(IPAddress.Loopback, 0);
        occupant.Start();
        await using var helper = StartHelper();
        using var bridge = ControlClient.ForPort(helper.Port);

        var (status, session, _) = await bridge.OpenAsync(Open(((IPEndPoint)occupant.LocalEndpoint).Port));

        Assert.AreEqual(409, status);
        Assert.IsNull(session);
    }

    [TestMethod]
    public void PrefersTheHelperOfThePcUsedMostRecently()
    {
        var now = DateTime.UtcNow;
        Bridge.Connection[] helpers =
        [
            new(ControlClient.ForPort(1), new(Protocol.Version, 1, 0, [], "home", IdleSeconds: 7200), now),
            new(ControlClient.ForPort(1), new(Protocol.Version, 2, 0, [], "older, without idle time"), now.AddMinutes(-5)),
            new(ControlClient.ForPort(1), new(Protocol.Version, 3, 0, [], "office", IdleSeconds: 5), now.AddHours(-8)),
            new(ControlClient.ForPort(1), new(Protocol.Version, 4, 0, [], "newer, without idle time"), now.AddMinutes(-1)),
        ];

        var order = Bridge.Preferred(helpers).Select(helper => helper.Info.Machine).ToArray();

        CollectionAssert.AreEqual(new[] { "office", "home", "newer, without idle time", "older, without idle time" }, order);
        foreach (var helper in helpers)
            helper.Dispose();
    }

    [TestMethod]
    public async Task DiscoveryStartsOneGracePeriodWithTheFirstAnswer()
    {
        using var first = new Bridge.Connection(ControlClient.ForPort(1), new(Protocol.Version, 1, 0, []), DateTime.UtcNow);
        using var second = new Bridge.Connection(ControlClient.ForPort(2), new(Protocol.Version, 2, 0, []), DateTime.UtcNow);
        var slow = new TaskCompletionSource<Bridge.Connection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fast = new TaskCompletionSource<Bridge.Connection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var discovery = Bridge.WaitForAnswersAsync([Task.FromResult<Bridge.Connection?>(null), slow.Task, fast.Task], () =>
        {
            starts++;
            started.TrySetResult();
        });

        Assert.IsFalse(started.Task.IsCompleted);
        fast.SetResult(first);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(discovery.IsCompleted);
        slow.SetResult(second);

        CollectionAssert.AreEqual(new Bridge.Connection?[] { null, second, first }, await discovery);
        Assert.AreEqual(1, starts);
    }

    [TestMethod]
    public async Task DiscoveryNeverDeletesAnExplicitSocketPath()
    {
        var temp = Directory.CreateTempSubdirectory("ab-");
        try
        {
            var stateDir = Path.Combine(temp.FullName, "state");
            var external = Path.Combine(temp.FullName, "state-other.sock");
            await File.WriteAllTextAsync(external, "not a managed tunnel");

            var connections = await Bridge.FindAllAsync(stateDir, external, TimeSpan.FromSeconds(2));

            Assert.IsEmpty(connections);
            Assert.IsTrue(File.Exists(external));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static Helper StartHelper()
    {
        var helper = new Helper(_ => Assert.Fail("Dry runs do not open a browser."), () => [], () => null);
        helper.Start(0);
        return helper;
    }

    private static OpenRequest Open(int port) =>
        new($"https://login.example/authorize?redirect_uri=http%3A%2F%2Flocalhost%3A{port}", port, DryRun: true, "test");

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
