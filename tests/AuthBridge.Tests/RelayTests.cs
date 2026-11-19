using System.Collections.Concurrent;
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
    [TestCategory("Mutation")]
    public async Task ReadsOnlyTheDeclaredCallbackBody()
    {
        using var wire = new MemoryStream("POST /callback?x=1 HTTP/1.1\r\ncontent-length: 4\r\nX-Note: a:b\r\n\r\nbodyextra"u8.ToArray());

        var request = await Http.ReadRequestAsync(wire, CancellationToken.None);

        Assert.AreEqual(("POST", "/callback?x=1", "a:b", "body"),
            (request.Method, request.Target, request.GetHeader("x-note"), Encoding.UTF8.GetString(request.Body)));
    }

    [TestMethod]
    [TestCategory("Mutation")]
    public async Task HeadResponseReframesTheContentLengthWithoutSendingTheBody()
    {
        using var wire = new MemoryStream();
        var response = new HttpResponse(200, [new("Content-Length", "99"), new("X-Note", "kept")], "body"u8.ToArray());

        await Http.WriteResponseAsync(wire, response, includeBody: false, CancellationToken.None);

        var parts = Encoding.Latin1.GetString(wire.ToArray()).Split("\r\n\r\n");
        Assert.HasCount(2, parts);
        Assert.AreEqual("", parts[1]);
        var lines = parts[0].Split("\r\n");
        Assert.AreEqual("HTTP/1.1 200 OK", lines[0]);
        CollectionAssert.AreEquivalent(new[] { "X-Note: kept", "Content-Length: 4", "Connection: close" }, lines[1..]);
    }

    [TestMethod]
    [TestCategory("Mutation")]
    public async Task ForwardsTheCallbackFormAndReturnsTheRedirectWithoutFollowingIt()
    {
        const string Form = "application/x-www-form-urlencoded";
        await using var application = HttpServer.Start(new IPEndPoint(IPAddress.Loopback, 0), request => Task.FromResult(
            (request.Method, request.Target, request.GetHeader("Content-Type")) == ("POST", "/callback", Form)
                ? new HttpResponse(303, [new("Location", "/done")], request.Body, "Continue sign-in")
                : new HttpResponse(404, [], [])));
        var callback = new HttpRequest("POST", "/callback", [new("Content-Type", Form)], "state=ok"u8.ToArray());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var response = await Relay.ForwardAsync(application.Port, callback, stop.Token);

        Assert.AreEqual((303, "Continue sign-in", "/done"), (response.Status, response.Reason, Http.Find(response.Headers, "Location")));
        CollectionAssert.AreEqual(callback.Body, response.Body);
    }

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
    public async Task PingRejectsAMismatchedProtocolVersion()
    {
        await using var helper = StartHelper();
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        client.DefaultRequestHeaders.Host = Protocol.HostName;
        client.DefaultRequestHeaders.Add(Protocol.VersionHeader, (Protocol.Version + 1).ToString());

        using var response = await client.GetAsync($"http://127.0.0.1:{helper.Port}/v1/ping");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "install the same authbridge version");
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
        using var bridge = Connect(helper);
        // A dropped tunnel: nothing listens there any more, and the relay must find the helper again.
        using var dropped = ControlClient.ForPort(FreePort());
        var relayPort = FreePort();

        var workers = new List<string[]>();
        var opener = new BrowserClient(workers.Add, TextWriter.Null);
        Assert.AreEqual(0, await opener.OpenAsync(Open(relayPort), bridge));
        var worker = workers.Single();
        var session = worker[2];
        CollectionAssert.AreEqual(new[] { "__relay", bridge.Client.Endpoint, session, relayPort.ToString() }, worker);
        using var stop = new CancellationTokenSource();
        var relay = Relay.RunAsync(tunnelReconnects ? dropped : bridge.Client, session, application.Port, stop.Token,
            () => Task.FromResult<ControlClient?>(ControlClient.ForPort(helper.Port)));
        using var browser = new HttpClient();
        using var response = await browser.PostAsync($"http://localhost:{relayPort}/callback?x=1", new StringContent("code=c&state=s"));

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
        using var bridge = Connect(helper);

        var port = ((IPEndPoint)occupant.LocalEndpoint).Port;
        var request = Open(port);
        var (status, session, _) = await bridge.Client.OpenAsync(request);

        Assert.AreEqual(409, status);
        Assert.IsNull(session);

        var workers = new List<string[]>();
        var opener = new BrowserClient(workers.Add, TextWriter.Null);
        Assert.AreEqual(0, await opener.OpenAsync(request, bridge));
        var cancel = workers.Single();
        CollectionAssert.AreEqual(new[] { "__cancel", port.ToString(), request.Url }, cancel[..3]);
        StringAssert.Contains(cancel[3], "retry the sign-in");
    }

    [TestMethod]
    public async Task DisconnectedClientOffersManualSignInUnlessItCanNotifyTheApplication()
    {
        var workers = new List<string[]>();
        using var messages = new StringWriter();
        var opener = new BrowserClient(workers.Add, messages);
        var request = Open(45678, includeState: false);

        Assert.AreEqual(1, await opener.OpenAsync(request, null));
        Assert.IsEmpty(workers);
        StringAssert.Contains(messages.ToString(), $"Open this URL manually:\n  {request.Url}");

        var cancellable = Open(45678);
        Assert.AreEqual(0, await opener.OpenAsync(cancellable, null));
        CollectionAssert.AreEqual(new[] { "__cancel", "45678", cancellable.Url, "the Windows helper is not connected" }, workers.Single());
    }

    [TestMethod]
    public async Task LongBrowserLinksCanBeUsedOnlyOnce()
    {
        string? launched = null;
        await using var helper = StartHelper(url => launched = url);
        using var bridge = Connect(helper);
        var opener = new BrowserClient(_ => Assert.Fail("A URL without a loopback redirect needs no worker."), TextWriter.Null);
        var url = "https://login.example/authorize?state=" + new string('x', 2100);
        Assert.AreEqual(0, await opener.OpenAsync(new OpenRequest(url, null, DryRun: false, "test"), bridge));

        Assert.IsNotNull(launched);
        var link = new Uri(launched);
        Assert.IsTrue(link.IsLoopback);
        Assert.AreEqual(helper.Port, link.Port);
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        using var first = await browser.GetAsync(link);
        Assert.AreEqual(HttpStatusCode.Found, first.StatusCode);
        Assert.AreEqual(url, first.Headers.Location?.OriginalString);
        using var reused = await browser.GetAsync(link);
        Assert.AreEqual(HttpStatusCode.NotFound, reused.StatusCode);
    }

    [TestMethod]
    public async Task ClosedSessionEndsTheRelayEvenBeforeTheApplicationStartsListening()
    {
        const string session = "ended-session";
        var requests = new ConcurrentQueue<string>();
        await using var helper = HttpServer.Start(new IPEndPoint(IPAddress.Loopback, 0), request =>
        {
            requests.Enqueue($"{request.Method} {request.Target.Split('?')[0]}");
            return Task.FromResult(new HttpResponse(404, [], []));
        });
        using var bridge = ControlClient.ForPort(helper.Port);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Relay.RunAsync(bridge, session, FreePort(), stop.Token);

        Assert.IsFalse(stop.IsCancellationRequested);
        CollectionAssert.AreEqual(new[] { $"GET /v1/sessions/{session}/next", $"POST /v1/sessions/{session}/close" }, requests.ToArray());
    }

    [TestMethod]
    [TestCategory("Mutation")]
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
    [TestCategory("Mutation")]
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
    [TestCategory("Mutation")]
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

    private static Helper StartHelper(Action<string>? openBrowser = null)
    {
        var helper = new Helper(openBrowser ?? (_ => Assert.Fail("Dry runs do not open a browser.")), () => [], () => null);
        helper.Start(0);
        return helper;
    }

    private static Bridge.Connection Connect(Helper helper) =>
        new(ControlClient.ForPort(helper.Port), new(Protocol.Version, Environment.ProcessId, 0, [], "test PC"), DateTime.UtcNow);

    private static OpenRequest Open(int port, bool includeState = true) =>
        new($"https://login.example/authorize?redirect_uri=http%3A%2F%2Flocalhost%3A{port}" + (includeState ? "&state=test" : ""), port, DryRun: true, "test");

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
