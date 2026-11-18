using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using AuthBridge.Common;

namespace AuthBridge.Host;

/// <summary>Listens on a loopback port and holds the browser's requests until the remote client answers them.</summary>
internal sealed class RelaySession : IAsyncDisposable
{
    private readonly Channel<PendingRequest> _queue = Channel.CreateUnbounded<PendingRequest>();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<HttpResponse>> _replies = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly List<HttpServer> _servers = [];

    private RelaySession(int port) => Port = port;

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public int Port { get; }

    public DateTime Created { get; } = DateTime.UtcNow;

    public DateTime LastPoll { get; private set; } = DateTime.UtcNow;

    public bool IsClosed => _closed.IsCancellationRequested;

    /// <summary>Listens on the IPv4 and, when available, IPv6 loopback port; throws <see cref="SocketException"/> if the port is taken.</summary>
    public static async Task<RelaySession> StartAsync(int port)
    {
        var session = new RelaySession(port);
        try
        {
            session._servers.Add(HttpServer.Start(new IPEndPoint(IPAddress.Loopback, port), session.HandleAsync));
            try
            {
                session._servers.Add(HttpServer.Start(new IPEndPoint(IPAddress.IPv6Loopback, port), session.HandleAsync));
            }
            catch (SocketException e) when (e.SocketErrorCode is not (SocketError.AddressAlreadyInUse or SocketError.AccessDenied))
            {
                // No IPv6 loopback.
            }
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>The next browser request, or null when none arrives in time or the session is closed.</summary>
    public async Task<PendingRequest?> NextAsync(TimeSpan wait)
    {
        LastPoll = DateTime.UtcNow;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        timeout.CancelAfter(wait);
        try
        {
            return await _queue.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            LastPoll = DateTime.UtcNow;
        }
    }

    public bool Complete(string id, HttpResponse response) => _replies.TryGetValue(id, out var reply) && reply.TrySetResult(response);

    public async ValueTask DisposeAsync()
    {
        await _closed.CancelAsync();
        foreach (var server in _servers)
            await server.DisposeAsync();
    }

    private async Task<HttpResponse> HandleAsync(HttpRequest request)
    {
        var pending = new PendingRequest(Guid.NewGuid().ToString("N"), request);
        var reply = new TaskCompletionSource<HttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _replies[pending.Id] = reply;
        try
        {
            await _queue.Writer.WriteAsync(pending, _closed.Token);
            var response = await reply.Task.WaitAsync(TimeSpan.FromMinutes(2), _closed.Token);
            Log.Info($"relay port {Port}: {request.Method} {OAuth.Redact(request.Target)} -> {response.Status}");
            return response;
        }
        catch (TimeoutException)
        {
            return HttpResponse.Html(504, "Sign-in relay timed out", "The remote machine did not answer in time.");
        }
        catch (OperationCanceledException)
        {
            return HttpResponse.Html(502, "Sign-in relay closed", "The remote application no longer waits for this request.");
        }
        finally
        {
            _replies.TryRemove(pending.Id, out _);
        }
    }
}
