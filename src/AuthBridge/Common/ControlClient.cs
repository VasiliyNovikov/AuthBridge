using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace AuthBridge.Common;

/// <summary>Calls the helper's control API: through an SSH tunnel's Unix socket on Linux, over loopback TCP on Windows.</summary>
internal sealed class ControlClient : IDisposable
{
    private readonly HttpClient _http;

    private ControlClient(string endpoint, EndPoint target)
    {
        Endpoint = endpoint;
        var protocol = target is UnixDomainSocketEndPoint ? ProtocolType.Unspecified : ProtocolType.Tcp;
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, cancel) =>
            {
                var socket = new Socket(target.AddressFamily, SocketType.Stream, protocol);
                try
                {
                    await socket.ConnectAsync(target, cancel);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        })
        {
            BaseAddress = new Uri($"http://{Protocol.HostName}/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.Add(Protocol.VersionHeader, Protocol.Version.ToString());
    }

    /// <summary>The socket path or address.</summary>
    public string Endpoint { get; }

    public static ControlClient ForSocket(string path) => new(path, new UnixDomainSocketEndPoint(path));

    public static ControlClient ForPort(int port) => new($"127.0.0.1:{port}", new IPEndPoint(IPAddress.Loopback, port));

    public async Task<PingResult?> PingAsync(TimeSpan timeout, CancellationToken cancel = default)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, "v1/ping", null, timeout, cancel);
        return status == 200 ? JsonSerializer.Deserialize(body, Json.Default.PingResult) : null;
    }

    /// <summary>Opens a URL in the browser; Session identifies the relay session for a loopback redirect.</summary>
    public async Task<(int Status, string? Session, string? Error)> OpenAsync(OpenRequest request)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "v1/open", JsonSerializer.SerializeToUtf8Bytes(request, Json.Default.OpenRequest), TimeSpan.FromSeconds(30));
        if (status == 200)
            return (status, JsonSerializer.Deserialize(body, Json.Default.OpenResult)?.Session, null);
        try
        {
            return (status, null, JsonSerializer.Deserialize(body, Json.Default.ErrorResult)?.Error);
        }
        catch (JsonException)
        {
            return (status, null, $"HTTP {status}");
        }
    }

    /// <summary>Waits for the session's next browser request; Closed is true once the helper has ended the session.</summary>
    public async Task<(bool Closed, PendingRequest? Request)> NextAsync(string session, int waitSeconds, CancellationToken cancel)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, $"v1/sessions/{session}/next?wait={waitSeconds}", null, TimeSpan.FromSeconds(waitSeconds + 30), cancel);
        return status switch
        {
            200 => (false, JsonSerializer.Deserialize(body, Json.Default.PendingRequest)),
            204 => (false, null),
            _ => (true, null),
        };
    }

    public Task ReplyAsync(string session, string id, HttpResponse response, CancellationToken cancel) =>
        SendAsync(HttpMethod.Post, $"v1/sessions/{session}/reply/{id}", JsonSerializer.SerializeToUtf8Bytes(response, Json.Default.HttpResponse), TimeSpan.FromSeconds(30), cancel);

    public Task CloseAsync(string session) => SendAsync(HttpMethod.Post, $"v1/sessions/{session}/close", [], TimeSpan.FromSeconds(5));

    public void Dispose() => _http.Dispose();

    private async Task<(int Status, byte[] Body)> SendAsync(HttpMethod method, string path, byte[]? content, TimeSpan timeout, CancellationToken cancel = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(method, path);
        if (content is not null)
            request.Content = new ByteArrayContent(content) { Headers = { ContentType = new("application/json") } };
        using var response = await _http.SendAsync(request, deadline.Token);
        return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(deadline.Token));
    }
}
