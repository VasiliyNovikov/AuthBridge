using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AuthBridge.Common;

namespace AuthBridge.Client;

/// <summary>Answers a relay session's browser requests by forwarding them to the application's loopback listener.</summary>
internal static class Relay
{
    private const int PollSeconds = 10;

    private static readonly HttpClient Local = new(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
    })
    { Timeout = TimeSpan.FromMinutes(1) };

    /// <summary>
    /// Relays until the application stops listening on <paramref name="port"/> or the helper ends the session.
    /// <paramref name="reconnect"/> finds the helper again when its tunnel reconnected and the bridge stopped working.
    /// </summary>
    public static async Task RunAsync(ControlClient bridge, string session, int port, CancellationToken cancel, Func<Task<ControlClient?>>? reconnect = null)
    {
        var name = $"session {session[..8]}";
        Log.Info($"{name}: relaying to localhost:{port}");
        var current = bridge;
        var elapsed = Stopwatch.StartNew();
        var listened = false;
        var failures = 0;
        try
        {
            while (elapsed.Elapsed < TimeSpan.FromMinutes(30))
            {
                if (IsListening(port))
                    listened = true;
                else if (listened || elapsed.Elapsed > TimeSpan.FromMinutes(1))
                {
                    Log.Info($"{name}: nothing listens on localhost:{port} any more");
                    return;
                }

                (bool Closed, PendingRequest? Request) next;
                try
                {
                    next = await current.NextAsync(session, PollSeconds, cancel);
                    failures = 0;
                }
                catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException && !cancel.IsCancellationRequested)
                {
                    Log.Warn($"{name}: helper unreachable: {e.Message}");
                    if (++failures >= 15)
                        return;
                    await Task.Delay(TimeSpan.FromSeconds(2), cancel);
                    if (reconnect is not null && await reconnect() is { } reconnected)
                    {
                        if (current != bridge)
                            current.Dispose();
                        current = reconnected;
                    }
                    continue;
                }
                if (next.Closed)
                {
                    Log.Info($"{name}: ended by the helper");
                    return;
                }
                if (next.Request is { } pending)
                    await current.ReplyAsync(session, pending.Id, await ForwardAsync(port, pending.Request, cancel), cancel);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                await current.CloseAsync(session);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            {
                // The helper closes idle sessions itself.
            }
            if (current != bridge)
                current.Dispose();
        }
    }

    /// <summary>Sends a request to the application's loopback listener and returns its response.</summary>
    public static async Task<HttpResponse> ForwardAsync(int port, HttpRequest request, CancellationToken cancel)
    {
        var description = $"localhost:{port}: {request.Method} {OAuth.Redact(request.Target)}";
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), $"http://localhost:{port}{request.Target}");
        if (request.Body.Length > 0)
            message.Content = new ByteArrayContent(request.Body);
        foreach (var (name, value) in request.Headers.Where(header => !Http.IsHopByHop(header.Name)))
        {
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                message.Headers.Host = value;
            else if (!message.Headers.TryAddWithoutValidation(name, value))
                message.Content?.Headers.TryAddWithoutValidation(name, value);
        }
        try
        {
            using var response = await Local.SendAsync(message, cancel);
            var body = await response.Content.ReadAsByteArrayAsync(cancel);
            Log.Info($"{description} -> {(int)response.StatusCode}");
            Header[] headers = [.. response.Headers.Concat(response.Content.Headers).SelectMany(header => header.Value.Select(value => new Header(header.Key, value)))];
            return new HttpResponse((int)response.StatusCode, headers, body, response.ReasonPhrase);
        }
        catch (HttpRequestException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
        {
            Log.Warn($"{description}: nothing listens");
            return HttpResponse.Html(502, "Sign-in no longer awaited",
                $"The application on {Environment.MachineName} stopped waiting for this sign-in (nothing listens on localhost:{port}). You can close this tab.");
        }
        catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException && !cancel.IsCancellationRequested)
        {
            Log.Warn($"{description} failed: {e.Message}");
            return HttpResponse.Html(502, "Sign-in relay failed", $"Could not reach the application on {Environment.MachineName} (localhost:{port}): {e.Message}");
        }
    }

    private static bool IsListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);
}
