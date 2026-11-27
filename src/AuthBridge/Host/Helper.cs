using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AuthBridge.Common;

namespace AuthBridge.Host;

/// <summary>Serves the control API: opens URLs in the browser and relays loopback redirects through relay sessions.</summary>
internal sealed class Helper(Action<string> openBrowser, Func<TunnelStatus[]> tunnelStatuses, Func<int?> idleSeconds) : IAsyncDisposable
{
    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SessionMaxAge = TimeSpan.FromMinutes(30);
    private static readonly HttpResponse NoContent = new(204, [], []);
    private readonly ConcurrentDictionary<string, RelaySession> _sessions = new();
    private readonly ConcurrentDictionary<string, (string Url, DateTime Expires)> _redirects = new();
    private HttpServer? _server;
    private bool _restarting;

    public int Port => _server?.Port ?? throw new InvalidOperationException("The helper is not started.");
    public bool HasRelaySessions => !_sessions.IsEmpty;

    public PingResult GetStatus() =>
        new(Protocol.Version, Environment.ProcessId, _sessions.Count, tunnelStatuses(), Environment.MachineName, idleSeconds());

    /// <summary>Once an idle restart is accepted, a new sign-in must not acquire a relay before the helper exits.</summary>
    public bool TryRequestIdleRestart(Func<bool> postClose)
    {
        lock (_sessions)
        {
            if (_restarting || HasRelaySessions)
                return false;
            _restarting = postClose();
            return _restarting;
        }
    }

    private bool TryRegister(RelaySession session)
    {
        lock (_sessions)
        {
            if (_restarting)
                return false;
            _sessions[session.Id] = session;
            return true;
        }
    }

    /// <summary>Listens on the loopback control port; throws <see cref="SocketException"/> if it is in use.</summary>
    public void Start(int port) => _server = HttpServer.Start(new IPEndPoint(IPAddress.Loopback, port), HandleAsync);

    /// <summary>Closes idle or old relay sessions and forgets expired redirects.</summary>
    public async Task ExpireAsync()
    {
        var now = DateTime.UtcNow;
        foreach (var session in _sessions.Values.Where(s => now - s.LastPoll > SessionIdleTimeout || now - s.Created > SessionMaxAge))
            await CloseAsync(session);
        foreach (var (nonce, _) in _redirects.Where(entry => entry.Value.Expires < now))
            _redirects.TryRemove(nonce, out _);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
        foreach (var session in _sessions.Values)
            await CloseAsync(session);
    }

    private async Task<HttpResponse> HandleAsync(HttpRequest request)
    {
        var segments = request.Target.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (request.Method == "GET" && segments is ["v1", "go", var nonce])
        {
            return _redirects.TryRemove(nonce, out var redirect) && redirect.Expires > DateTime.UtcNow
                ? new HttpResponse(302, [new("Location", redirect.Url)], [])
                : Error(404, "link expired");
        }
        // The custom header and Host value keep web pages, including DNS rebinding ones, out of the API.
        if (request.GetHeader("Host") != Protocol.HostName || request.GetHeader(Protocol.VersionHeader) is not { } version)
            return Error(403, "forbidden");
        if (version != Protocol.Version.ToString())
            return Error(400, $"protocol version {version} is not supported; install the same authbridge version on both machines");
        try
        {
            return (request.Method, segments) switch
            {
                ("GET", ["v1", "ping"]) => Reply(200, GetStatus(), Json.Default.PingResult),
                ("POST", ["v1", "open"]) => await OpenAsync(Parse(request, Json.Default.OpenRequest)),
                (_, ["v1", "sessions", var id, .. var action]) when _sessions.TryGetValue(id, out var session) => await SessionAsync(session, request, action),
                (_, ["v1", "sessions", ..]) => Error(404, "relay session closed"),
                _ => Error(404, "not found"),
            };
        }
        catch (JsonException e)
        {
            return Error(400, e.Message);
        }
    }

    private async Task<HttpResponse> OpenAsync(OpenRequest request)
    {
        if (request.Url is not { Length: <= 65536 } || !OpenRequest.CanOpen(request.Url))
            return Error(400, "only http and https URLs can be opened");
        RelaySession? session = null;
        if (request.RelayPort is { } port)
        {
            if (port is < 1 or > 65535)
                return Error(400, "invalid relay port");
            try
            {
                session = await RelaySession.StartAsync(port);
            }
            catch (SocketException e)
            {
                Log.Warn($"cannot relay port {port}: {e.Message}");
                return Error(409, $"port {port} is unavailable on Windows ({e.Message})");
            }
            if (!TryRegister(session))
            {
                await session.DisposeAsync();
                return Error(409, "the helper is restarting for an update");
            }
        }
        return await OpenBrowserAsync(request, session);
    }

    private async Task<HttpResponse> OpenBrowserAsync(OpenRequest request, RelaySession? session)
    {
        if (!request.DryRun)
        {
            try
            {
                openBrowser(Launchable(request.Url));
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                if (session is not null)
                    await CloseAsync(session);
                return Error(500, $"cannot start the browser: {e.Message}");
            }
        }
        var relay = session is null ? "" : $" (relay port {session.Port}, session {session.Id[..8]})";
        Log.Info($"open {OAuth.Redact(request.Url)} for {request.Client}{relay} {OAuth.Describe(request.Url)}");
        return Reply(200, new OpenResult(session?.Id), Json.Default.OpenResult);
    }

    private async Task<HttpResponse> SessionAsync(RelaySession session, HttpRequest request, string[] action)
    {
        switch (request.Method, action)
        {
            case ("GET", ["next"]):
                var wait = int.TryParse(Http.Query(request.Target).FirstOrDefault(p => p.Name == "wait").Value, out var seconds) ? seconds : 10;
                var pending = await session.NextAsync(TimeSpan.FromSeconds(Math.Clamp(wait, 0, 30)));
                return pending is not null ? Reply(200, pending, Json.Default.PendingRequest)
                    : session.IsClosed ? Error(404, "relay session closed")
                    : NoContent;
            case ("POST", ["reply", var id]):
                return session.Complete(id, Parse(request, Json.Default.HttpResponse)) ? NoContent : Error(404, "unknown request");
            case ("POST", ["close"]):
                await CloseAsync(session);
                return NoContent;
            default:
                return Error(404, "not found");
        }
    }

    /// <summary>Windows cannot pass long URLs to the browser reliably, so these go through a local redirect.</summary>
    private string Launchable(string url)
    {
        if (url.Length <= 2000)
            return url;
        var nonce = Guid.NewGuid().ToString("N");
        _redirects[nonce] = (url, DateTime.UtcNow.AddMinutes(5));
        return $"http://127.0.0.1:{Port}/v1/go/{nonce}";
    }

    private async Task CloseAsync(RelaySession session)
    {
        if (!_sessions.TryRemove(session.Id, out _))
            return;
        await session.DisposeAsync();
        Log.Info($"relay session {session.Id[..8]} (port {session.Port}) closed");
    }

    private static HttpResponse Reply<T>(int status, T value, JsonTypeInfo<T> type) =>
        new(status, [new("Content-Type", "application/json")], JsonSerializer.SerializeToUtf8Bytes(value, type));

    private static HttpResponse Error(int status, string message) => Reply(status, new ErrorResult(message), Json.Default.ErrorResult);

    private static T Parse<T>(HttpRequest request, JsonTypeInfo<T> type) =>
        JsonSerializer.Deserialize(request.Body, type) ?? throw new JsonException("The request body is empty.");
}
