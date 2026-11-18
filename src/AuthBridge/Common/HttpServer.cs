using System.Net;
using System.Net.Sockets;

namespace AuthBridge.Common;

/// <summary>A minimal HTTP/1.1 server for loopback endpoints.</summary>
internal sealed class HttpServer : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly Func<HttpRequest, Task<HttpResponse>> _handler;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _accepting;

    private HttpServer(Socket listener, Func<HttpRequest, Task<HttpResponse>> handler)
    {
        _listener = listener;
        _handler = handler;
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndPoint!).Port;

    /// <summary>Starts listening; throws <see cref="SocketException"/> when the endpoint is unavailable.</summary>
    public static HttpServer Start(IPEndPoint endpoint, Func<HttpRequest, Task<HttpResponse>> handler)
    {
        var listener = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (OperatingSystem.IsWindows())
                listener.ExclusiveAddressUse = true; // Windows would otherwise let another socket share the port.
            if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
                listener.DualMode = false;
            listener.Bind(endpoint);
            listener.Listen();
            return new HttpServer(listener, handler);
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Dispose();
        await _accepting;
        _stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            try
            {
                _ = ServeAsync(await _listener.AcceptAsync(_stopping.Token));
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException && _stopping.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                // A client gave up before the connection was accepted.
            }
        }
    }

    private async Task ServeAsync(Socket connection)
    {
        await using var stream = new NetworkStream(connection, ownsSocket: true);
        try
        {
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            reading.CancelAfter(TimeSpan.FromSeconds(30));
            var request = await Http.ReadRequestAsync(stream, reading.Token);
            var response = await _handler(request);
            await Http.WriteResponseAsync(stream, response, includeBody: request.Method != "HEAD", _stopping.Token);
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidDataException or OperationCanceledException)
        {
            // The client disconnected or sent an unsupported request.
        }
        catch (Exception e)
        {
            Log.Error(e, "HTTP request failed");
        }
    }
}
