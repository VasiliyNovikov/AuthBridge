using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AuthBridge.Client;
using AuthBridge.Common;
using AuthBridge.Host;
using static AuthBridge.Tests.TestSupport;

namespace AuthBridge.Tests;

[TestClass]
public sealed class RelayTests
{
    [TestMethod]
    [TestCategory("Mutation")]
    public async Task ReadsOnlyTheDeclaredCallbackBody()
    {
        const string Head = "POST /callback?x=1 HTTP/1.1\r\ncontent-length: 4\r\nContent-Length: 4\r\nX-Note: a:b\r\n\r\n";
        using var wire = new LimitedReadStream(Encoding.ASCII.GetBytes(Head + "bodyextra"), Head.Length + 2);

        var request = await Http.ReadRequestAsync(wire, CancellationToken.None);

        Assert.AreEqual(("POST", "/callback?x=1", "a:b", "body"),
            (request.Method, request.Target, request.GetHeader("x-note"), Encoding.UTF8.GetString(request.Body)));
        Assert.AreEqual(Head.Length + 4, wire.Position);
    }

    private sealed class LimitedReadStream(byte[] bytes, int limit) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, limit)], cancel);
    }

    [TestMethod]
    [TestCategory("Mutation")]
    [DataRow("GET\r\n\r\n", "request line")]
    [DataRow("POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n", "Chunked")]
    [DataRow("POST / HTTP/1.1\r\nContent-Length: -1\r\n\r\n", "body length")]
    [DataRow("POST / HTTP/1.1\r\nContent-Length: unknown\r\n\r\n", "body length")]
    [DataRow("POST / HTTP/1.1\r\nContent-Length: 0\r\nContent-Length: 2\r\n\r\n", "body length")]
    public async Task ReaderRejectsRequestsItCannotFrame(string head, string reason)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(head));
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Http.ReadRequestAsync(stream, CancellationToken.None));
        StringAssert.Contains(error.Message, reason);
    }

    [TestMethod]
    [TestCategory("Mutation")]
    public async Task ReaderDistinguishesAnIncompleteHeaderFromAnOversizedOne()
    {
        using var incomplete = new MemoryStream("GET / HTTP/1.1\r\n"u8.ToArray());
        using var oversized = new MemoryStream(new byte[64 << 10]);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => Http.ReadRequestAsync(incomplete, stop.Token));
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Http.ReadRequestAsync(oversized, stop.Token));
        StringAssert.Contains(error.Message, "headers");
    }

    [TestMethod]
    [TestCategory("Mutation")]
    public async Task ReaderAcceptsEmptyBodiesAndTheExactMaximumBodySize()
    {
        const int maximum = 16 << 20;
        foreach (var size in new[] { 0, maximum })
        {
            var head = Encoding.ASCII.GetBytes($"POST / HTTP/1.1\r\nContent-Length: {size}\r\n\r\n");
            using var request = new MemoryStream(head.Length + size);
            request.Write(head);
            request.SetLength(head.Length + size);
            request.Position = 0;
            Assert.AreEqual(size, (await Http.ReadRequestAsync(request, CancellationToken.None)).Body.Length);
        }
    }

    [TestMethod]
    [TestCategory("Mutation")]
    public void CallbackQueryDecodesNamesAndValuesWithoutIncludingTheFragment()
    {
        CollectionAssert.AreEqual(new[] { ("flag", ""), ("", "value"), ("client name", "a=b") },
            Http.Query("?flag&&=value&client+name=a%3Db#&ignored").ToArray());
    }

    [TestMethod]
    [TestCategory("Mutation")]
    public async Task HeadResponseReframesTheContentLengthWithoutSendingTheBody()
    {
        using var wire = new MemoryStream();
        var response = new HttpResponse(200, [
            new("Content-Length", "99"), new("Connection", "keep-alive"), new("Keep-Alive", "timeout=5"),
            new("Proxy-Connection", "keep-alive"), new("Transfer-Encoding", "chunked"), new("TE", "trailers"),
            new("Trailer", "Expires"), new("Upgrade", "h2c"), new("X-Note", "kept"),
        ], "body"u8.ToArray());

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
        await using var application = Serve(request =>
            (request.Method, request.Target, request.GetHeader("Content-Type"), request.GetHeader("Host")) == ("POST", "/callback", Form, "localhost:7512")
                ? new HttpResponse(303, [new("Location", "/done"), new("Content-Type", "text/plain")], request.Body, "Continue sign-in")
                : new HttpResponse(404, [], []));
        var callback = new HttpRequest("POST", "/callback", [new("Content-Type", Form), new("Host", "localhost:7512")], "state=ok"u8.ToArray());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var response = await Relay.ForwardAsync(application.Port, callback, stop.Token);

        Assert.AreEqual((303, "Continue sign-in", "/done"), (response.Status, response.Reason, Http.Find(response.Headers, "Location")));
        Assert.AreEqual("text/plain", Http.Find(response.Headers, "Content-Type"));
        CollectionAssert.AreEqual(callback.Body, response.Body);
    }

    [TestMethod]
    [TestCategory("Mutation")]
    public async Task RelayForwardsOnlyTheCookiesOfTheCurrentBrowserRequest()
    {
        const string cookie = "authbridge-test=cookie";
        await using var application = Serve(request =>
            new HttpResponse(200, [new("Set-Cookie", cookie)], Encoding.UTF8.GetBytes(request.GetHeader("Cookie") ?? "")));
        var request = new HttpRequest("GET", "/callback", [], []);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = await Relay.ForwardAsync(application.Port, request with { Headers = [new("Cookie", "browser=first")] }, stop.Token);
        Assert.AreEqual("browser=first", Encoding.UTF8.GetString(first.Body));
        Assert.AreEqual(cookie, Http.Find(first.Headers, "Set-Cookie"));
        var second = await Relay.ForwardAsync(application.Port, request, stop.Token);
        Assert.AreEqual(200, second.Status);
        Assert.IsEmpty(second.Body);
    }

    [TestMethod]
    [TestCategory("Mutation")]
    public async Task AClosedCallbackPortReturnsABrowserErrorButCallerCancellationPropagates()
    {
        using var closed = ReserveClosedPort(out var port);
        var request = new HttpRequest("GET", "/callback", [], []);
        var response = await Relay.ForwardAsync(port, request, CancellationToken.None);
        Assert.AreEqual(502, response.Status);
        Assert.AreEqual("text/html; charset=utf-8", Http.Find(response.Headers, "Content-Type"));
        var page = Encoding.UTF8.GetString(response.Body);
        StringAssert.Contains(page, "<title>Sign-in no longer awaited</title>");
        StringAssert.Contains(page, $"localhost:{port}");
        await Assert.ThrowsAsync<OperationCanceledException>(() => Relay.ForwardAsync(port, request, new CancellationToken(canceled: true)));
    }

    [TestMethod]
    [TestCategory("Mutation")]
    public async Task RelayAcceptsOnlyAPathOnTheWaitingApplication()
    {
        var response = await Relay.ForwardAsync(0, new HttpRequest("OPTIONS", "*", [], []), CancellationToken.None);
        Assert.AreEqual(400, response.Status);
        var page = Encoding.UTF8.GetString(response.Body);
        StringAssert.Contains(page, "<title>Invalid callback</title>");
        StringAssert.Contains(page, "loopback listener");
    }

    [TestMethod]
    [TestCategory("Mutation")]
    public async Task IncompleteApplicationResponseReturnsARelayErrorInsteadOfAPartialSignInPage()
    {
        using var application = new TcpListener(IPAddress.Loopback, 0);
        application.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var port = ((IPEndPoint)application.LocalEndpoint).Port;
        var forward = Relay.ForwardAsync(port, new HttpRequest("POST", "/callback", [], "data"u8.ToArray()), stop.Token);
        using (var peer = await application.AcceptTcpClientAsync(stop.Token))
        {
            var stream = peer.GetStream();
            await Http.ReadRequestAsync(stream, stop.Token);
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\nx"u8.ToArray(), stop.Token);
        }
        var response = await forward;
        Assert.AreEqual(502, response.Status);
        var html = Encoding.UTF8.GetString(response.Body);
        StringAssert.Contains(html, "<title>Sign-in relay failed</title>");
        StringAssert.Contains(html, $"localhost:{port}");
    }

    [TestMethod]
    public async Task PingReportsLiveHostStateFromTheFirstRequest()
    {
        TunnelStatus[] tunnels = [new("dev", "connected", "", DateTimeOffset.UtcNow)];
        int? idle = 5;
        await using var helper = new Helper(_ => Assert.Fail("Ping does not open a browser."), () => tunnels, () => idle);
        helper.Start(0);
        using var bridge = ClientFor(helper);

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
    [DataRow("{}")]
    [DataRow("not a helper")]
    public async Task AnUnrelatedEndpointDoesNotCountAsARunningHelper(string response)
    {
        await using var server = Serve(_ => new HttpResponse(200, [], Encoding.UTF8.GetBytes(response)));
        using var client = ControlClient.ForPort(server.Port);
        Assert.IsNull(await client.PingAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    [TestCategory("Mutation")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RelaysTheBrowserRedirectToTheApplication(bool tunnelReconnects)
    {
        await using var application = Serve(request => new HttpResponse(200, [new("Set-Cookie", "a=1"), new("Set-Cookie", "b=2")],
            Encoding.UTF8.GetBytes($"{request.Method} {request.Target} {Encoding.UTF8.GetString(request.Body)}")));
        await using var helper = StartHelper();
        using var bridge = Connect(helper);
        // A dropped tunnel: nothing listens there any more, and the relay must find the helper again.
        using var closed = ReserveClosedPort(out var droppedPort);
        using var dropped = ControlClient.ForPort(droppedPort);
        var relayPort = FreePort();

        var (opener, workers) = RecordingOpener();
        Assert.AreEqual(0, await opener.OpenAsync(Open(relayPort), bridge));
        var worker = workers.Single();
        var session = worker[2];
        CollectionAssert.AreEqual(new[] { "__relay", bridge.Client.Endpoint, session, relayPort.ToString(), $"{Environment.ProcessId}@test PC" }, worker);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var relay = Relay.RunAsync(tunnelReconnects ? dropped : bridge.Client, session, application.Port, stop.Token,
            () => Task.FromResult<ControlClient?>(ControlClient.ForPort(helper.Port)));
        using var browser = new HttpClient();
        try
        {
            using var response = await browser.PostAsync($"http://localhost:{relayPort}/callback?x=1", new StringContent("code=c&state=s"), stop.Token);
            Assert.AreEqual("POST /callback?x=1 code=c&state=s", await response.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(new[] { "a=1", "b=2" }, response.Headers.GetValues("Set-Cookie").ToArray());
        }
        finally
        {
            await stop.CancelAsync();
            await relay.WaitAsync(TimeSpan.FromSeconds(10));
        }
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

        var (opener, workers) = RecordingOpener();
        Assert.AreEqual(0, await opener.OpenAsync(request, bridge));
        var cancel = workers.Single();
        CollectionAssert.AreEqual(new[] { "__cancel", port.ToString(), request.Url }, cancel[..3]);
        StringAssert.Contains(cancel[3], "retry the sign-in");
    }

    [TestMethod]
    public async Task DisconnectedClientOffersManualSignInUnlessItCanNotifyTheApplication()
    {
        using var messages = new StringWriter();
        var (opener, workers) = RecordingOpener(messages);
        var request = Open(45678, includeState: false);

        Assert.AreEqual(1, await opener.OpenAsync(request, null));
        Assert.IsEmpty(workers);
        StringAssert.Contains(messages.ToString(), $"Open this URL manually:\n  {request.Url}");

        var cancellable = Open(45678);
        Assert.AreEqual(0, await opener.OpenAsync(cancellable, null));
        CollectionAssert.AreEqual(new[] { "__cancel", "45678", cancellable.Url, "the Windows helper is not connected" }, workers.Single());
    }

    [TestMethod]
    public async Task ASecondOpenCannotEvictAnActiveSessionOnTheSamePort()
    {
        await using var helper = StartHelper();
        using var client = ClientFor(helper);
        var request = Open(FreePort());
        var original = await client.OpenAsync(request);
        Assert.IsNotNull(original.Session);
        Assert.AreEqual(409, (await client.OpenAsync(request with { Client = "second machine" })).Status);
        Assert.IsFalse((await client.NextAsync(original.Session, 0, CancellationToken.None)).Closed);
        await client.CloseAsync(original.Session);
        Assert.AreEqual(200, (await client.OpenAsync(request)).Status);
    }

    [TestMethod]
    public async Task AutomaticTrayUpdateWaitsForIdleBeforeLaunchAndAgainBeforeRestart()
    {
        using var directory = new TempDirectory("authbridge-idle-update-");
        var settings = NewSettings(Path.Combine(directory.FullName, "config.json"));
        var releases = await OfferUpdateAsync();
        await using var helper = StartHelper();
        using var client = ClientFor(helper);
        var launches = 0;
        using var tray = new TrayUpdater(settings, releases, helper, automatic =>
        {
            Assert.IsTrue(automatic);
            launches++;
            return Process.GetCurrentProcess(); // A live process handle; never launch a real updater.
        });
        tray.Refresh();
        Assert.AreEqual(0, launches);

        var request = Open(FreePort());
        var first = await client.OpenAsync(request);
        Assert.IsNotNull(first.Session);
        tray.SetUpdateAutomatically(true);
        tray.Refresh();
        Assert.AreEqual(0, launches);
        await client.CloseAsync(first.Session);
        tray.Refresh();
        tray.Refresh();
        await tray.CheckOrUpdateAsync("2.0");
        Assert.AreEqual(1, launches);

        var duringDownload = await client.OpenAsync(request);
        Assert.IsNotNull(duringDownload.Session);
        bool UnexpectedClose() => throw new AssertFailedException("A busy or opted-out helper must not be stopped.");
        Assert.IsFalse(tray.TryRequestIdleRestart(Environment.ProcessId, UnexpectedClose));
        await client.CloseAsync(duringDownload.Session);
        tray.SetUpdateAutomatically(false);
        Assert.IsFalse(tray.TryRequestIdleRestart(Environment.ProcessId, UnexpectedClose));
        tray.SetUpdateAutomatically(true);
        Assert.IsFalse(tray.TryRequestIdleRestart(-1, UnexpectedClose));
        Assert.IsFalse(tray.TryRequestIdleRestart(Environment.ProcessId, () => false));
        Assert.IsTrue(tray.TryRequestIdleRestart(Environment.ProcessId, () => true));
        Assert.AreEqual(409, (await client.OpenAsync(request)).Status);
    }

    [TestMethod]
    public async Task RepeatedPollRecoversTheUnansweredRequestButNotAnAnsweredOne()
    {
        await using var helper = StartHelper();
        using var client = ClientFor(helper);
        var port = FreePort();
        var session = (await client.OpenAsync(Open(port))).Session!;
        using var browser = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = browser.GetStringAsync($"http://127.0.0.1:{port}/callback", stop.Token);
        var pending = (await client.NextAsync(session, 3, stop.Token)).Request;
        Assert.IsNotNull(pending);
        Assert.AreEqual(pending.Id, (await client.NextAsync(session, 1, stop.Token)).Request?.Id);
        await client.ReplyAsync(session, pending.Id, new HttpResponse(200, [], "done"u8.ToArray()), stop.Token);
        Assert.AreEqual("done", await response);
        Assert.IsNull((await client.NextAsync(session, 0, stop.Token)).Request);
    }

    [TestMethod]
    public async Task LosingTheTunnelAfterDiscoveryCancelsTheWaitingApplication()
    {
        using var dropped = new Bridge.Connection(ControlClient.ForPort(FreePort()), new(Protocol.Version, 1, 0, []), DateTime.UtcNow);
        var (browser, workers) = RecordingOpener();
        var request = Open(45678);
        Assert.AreEqual(0, await browser.OpenAsync(request, dropped));
        CollectionAssert.AreEqual(new[] { "__cancel", "45678", request.Url }, workers.Single()[..3]);
    }

    [TestMethod]
    [TestCategory("Mutation")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AFailedReplyRetriesWithinBudgetWithoutReplayingTheCallback(bool recovers)
    {
        var calls = 0;
        await using var application = Serve(request =>
        {
            Interlocked.Increment(ref calls);
            return new HttpResponse(200, [], request.Body);
        });
        var pending = new PendingRequest("request", new HttpRequest("POST", "/callback", [], "one use"u8.ToArray()));
        await using var first = Serve(request => request.Method == "GET"
            ? new HttpResponse(200, [], JsonSerializer.SerializeToUtf8Bytes(pending, Json.Default.PendingRequest))
            : new HttpResponse(503, [], []));
        var replies = new ConcurrentQueue<HttpRequest>();
        await using var next = Serve(request =>
        {
            replies.Enqueue(request);
            return new HttpResponse(recovers ? 204 : 503, [], []);
        });
        using var initial = ControlClient.ForPort(first.Port);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var session = new Relay.HelperSession(initial, "retry-session", () => Task.FromResult<ControlClient?>(ControlClient.ForPort(next.Port)), TimeSpan.Zero);
        Assert.AreEqual(recovers, await session.PollAsync(application.Port, stop.Token));
        Assert.AreEqual(recovers ? 1 : 14, replies.Count);
        var delivered = replies.First();
        Assert.AreEqual("/v1/sessions/retry-session/reply/request", delivered.Target);
        CollectionAssert.AreEqual(pending.Request.Body, JsonSerializer.Deserialize(delivered.Body, Json.Default.HttpResponse)!.Body);
        Assert.AreEqual(1, calls);
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
    [TestCategory("Mutation")]
    public async Task ClosedSessionEndsTheRelayEvenBeforeTheApplicationStartsListening()
    {
        const string session = "ended-session";
        using var closed = ReserveClosedPort(out var applicationPort);

        var requests = await RelayEndingSessionAsync(session, applicationPort, request => $"{request.Method} {request.Target.Split('?')[0]}");

        CollectionAssert.AreEqual(new[] { $"GET /v1/sessions/{session}/next", $"GET /v1/sessions/{session}/next",
            $"POST /v1/sessions/{session}/close" }, requests);
    }

    [TestMethod]
    [TestCategory("Mutation")]
    public void RelayAllowsStartupGraceButNotAStoppedListenerOrAnExpiredSession()
    {
        var window = new Relay.ListenerWindow();
        var grace = TimeSpan.FromMinutes(1);
        Assert.IsTrue(window.CanContinue(false, grace));
        Assert.IsFalse(window.CanContinue(false, grace + TimeSpan.FromTicks(1)));
        Assert.IsTrue(window.CanContinue(true, TimeSpan.Zero));
        Assert.IsFalse(window.CanContinue(false, TimeSpan.FromSeconds(1)));
        var lifetime = TimeSpan.FromMinutes(30);
        Assert.IsFalse(Relay.ListenerWindow.Expired(lifetime - TimeSpan.FromTicks(1)));
        Assert.IsTrue(Relay.ListenerWindow.Expired(lifetime));
    }

    [TestMethod]
    [TestCategory("Mutation")]
    public async Task RelayClosesTheHelperSessionAsSoonAsTheApplicationStopsListening()
    {
        using var application = new TcpListener(IPAddress.Loopback, 0);
        application.Start();
        var port = ((IPEndPoint)application.LocalEndpoint).Port;
        var requests = await RelayEndingSessionAsync("finished-session", port, request =>
        {
            application.Stop();
            return request.Method;
        });
        CollectionAssert.AreEqual(new[] { "GET", "POST" }, requests);
    }

    [TestMethod]
    [TestCategory("Mutation")]
    public async Task SuccessfulPollResetsTheRetryBudgetAndOnlyReplacementClientsAreDisposed()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var online = false;
        await using var helper = Serve(_ => Volatile.Read(ref online) ? new HttpResponse(204, [], []) : throw new IOException("tunnel disconnected"));
        var port = helper.Port;
        using var initial = ControlClient.ForPort(port);
        using var restored = ControlClient.ForPort(port);
        using var replacement = ControlClient.ForPort(port);
        var retries = 0;
        Task<ControlClient?> Reconnect() => Task.FromResult(++retries switch
        {
            14 => restored, 15 => restored, 16 => replacement, _ => (ControlClient?)null,
        });
        await using (var session = new Relay.HelperSession(initial, "retry-session", Reconnect, TimeSpan.Zero))
        {
            // Fourteen unreachable polls stay within the budget, so each keeps the session.
            async Task PollOfflineWithinBudgetAsync()
            {
                for (var i = 0; i < 14; i++)
                    Assert.IsTrue(await session.PollAsync(port, stop.Token));
            }
            await PollOfflineWithinBudgetAsync();
            Volatile.Write(ref online, true);
            Assert.IsTrue(await session.PollAsync(port, stop.Token));
            Assert.AreEqual(14, retries);
            Volatile.Write(ref online, false);
            await PollOfflineWithinBudgetAsync();
            Assert.IsFalse(await session.PollAsync(port, stop.Token));
            Assert.AreEqual(28, retries);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => restored.PingAsync(TimeSpan.FromSeconds(1)));
        }
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => replacement.PingAsync(TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<HttpRequestException>(() => initial.PingAsync(TimeSpan.FromSeconds(1)));
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
    public async Task PreferredMatchStaysUsableAndDiscardedOrFaultedProbesReleaseTheirClients()
    {
        await using var helper = StartHelper();
        var timeout = TimeSpan.FromSeconds(2);
        using var first = Connect(helper);
        using var owner = Connect(helper);
        Assert.AreSame(owner, Bridge.TakePreferred([first, owner], info => ReferenceEquals(info, owner.Info)));
        Assert.IsNotNull(await owner.Client.PingAsync(timeout));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => first.Client.PingAsync(timeout));
        Assert.AreSame(owner, Bridge.TakePreferred([owner], null));
        Assert.IsNull(Bridge.TakePreferred([owner], _ => false));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => owner.Client.PingAsync(timeout));

        using var acquired = Connect(helper);
        await Assert.ThrowsExactlyAsync<IOException>(() => Bridge.WaitForAnswersAsync(
            [Task.FromResult<Bridge.Connection?>(acquired), Task.FromException<Bridge.Connection?>(new IOException("probe failed"))], () => { }));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => acquired.Client.PingAsync(timeout));
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
        using var temp = new TempDirectory("ab-");
        var stateDir = Path.Combine(temp.FullName, "state");
        var external = Path.Combine(temp.FullName, "state-other.sock");
        await File.WriteAllTextAsync(external, "not a managed tunnel");

        var connections = await Bridge.FindAllAsync(stateDir, external, TimeSpan.FromSeconds(2));

        Assert.IsEmpty(connections);
        Assert.IsTrue(File.Exists(external));
    }

    [TestMethod]
    [TestCategory("Mutation")]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = "Linux tunnel sockets.")]
    public async Task DiscoveryFindsLiveUnixSocketsAndRemovesOnlyStaleManagedPaths()
    {
        using var temp = new TempDirectory("ab-");
        var timeout = TimeSpan.FromSeconds(3);
        var absent = Path.Combine(temp.FullName, "missing");
        Assert.IsEmpty(await Bridge.FindAllAsync(absent, null, timeout));
        var live = Path.Combine(temp.FullName, "bridge-live.sock");
        var stale = Path.Combine(temp.FullName, "bridge-stale.sock");
        var unrelated = Path.Combine(temp.FullName, "other.sock");
        File.WriteAllText(stale, "");
        File.WriteAllText(unrelated, "");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(live));
        listener.Listen();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        async Task<Bridge.Connection> Discover(string directory, string socketOverride)
        {
            var discovery = Bridge.FindAllAsync(directory, socketOverride, timeout);
            await Task.WhenAll(discovery, AnswerPingAsync(listener, stop.Token));
            return (await discovery).Single();
        }
        using var found = await Discover(temp.FullName, "");
        Assert.AreEqual("test-helper", found.Info.Machine);
        Assert.IsFalse(File.Exists(stale));
        Assert.IsTrue(File.Exists(unrelated));

        using var fromOverride = await Discover(absent, live);
        var ping = fromOverride.Client.PingAsync(timeout);
        await Task.WhenAll(ping, AnswerPingAsync(listener, stop.Token));
        Assert.AreEqual("test-helper", (await ping)?.Machine);
    }

    private static async Task AnswerPingAsync(Socket listener, CancellationToken cancel)
    {
        using var peer = await listener.AcceptAsync(cancel);
        using var stream = new NetworkStream(peer);
        var request = await Http.ReadRequestAsync(stream, cancel);
        Assert.AreEqual("/v1/ping", request.Target);
        var body = JsonSerializer.SerializeToUtf8Bytes(new PingResult(Protocol.Version, 1, 0, [], "test-helper"), Json.Default.PingResult);
        await Http.WriteResponseAsync(stream, new HttpResponse(200, [], body), includeBody: true, cancel);
    }

    /// <summary>Relays a session that has no browser request on the first poll and is gone after that, and returns the
    /// helper requests in the form <paramref name="describe"/> gives them.</summary>
    private static async Task<string[]> RelayEndingSessionAsync(string session, int applicationPort, Func<HttpRequest, string> describe)
    {
        var requests = new ConcurrentQueue<string>();
        await using var helper = Serve(request =>
        {
            requests.Enqueue(describe(request));
            return new HttpResponse(requests.Count == 1 ? 204 : 404, [], []);
        });
        using var bridge = ControlClient.ForPort(helper.Port);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Relay.RunAsync(bridge, session, applicationPort, stop.Token);
        Assert.IsFalse(stop.IsCancellationRequested);
        return [.. requests];
    }

    /// <summary>A browser client that records the workers it would start instead of starting them.</summary>
    private static (BrowserClient Opener, List<string[]> Workers) RecordingOpener(TextWriter? messages = null)
    {
        List<string[]> workers = [];
        return (new BrowserClient(workers.Add, messages ?? TextWriter.Null), workers);
    }

    private static ControlClient ClientFor(Helper helper) => ControlClient.ForPort(helper.Port);

    private static Bridge.Connection Connect(Helper helper) =>
        new(ClientFor(helper), new(Protocol.Version, Environment.ProcessId, 0, [], "test PC"), DateTime.UtcNow);

    private static OpenRequest Open(int port, bool includeState = true) =>
        new($"https://login.example/authorize?redirect_uri=http%3A%2F%2Flocalhost%3A{port}" + (includeState ? "&state=test" : ""), port, DryRun: true, "test");

    /// <summary>
    /// A loopback port for the helper to listen on, below the ranges from which operating systems assign ports: Linux
    /// often hands a port it has just released to the next caller, which may be another test process.
    /// </summary>
    private static int FreePort()
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = Random.Shared.Next(20000, 32768);
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return port;
            }
            catch (SocketException) when (attempt < 100)
            {
                // Taken or reserved; try another port.
            }
        }
    }

    /// <summary>Binds a loopback port without listening, so connections are refused until the socket is disposed.</summary>
    private static Socket ReserveClosedPort(out int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        return socket;
    }
}
