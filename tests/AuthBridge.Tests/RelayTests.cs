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

    private static Helper StartHelper()
    {
        var helper = new Helper(_ => Assert.Fail("Dry runs do not open a browser."));
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
