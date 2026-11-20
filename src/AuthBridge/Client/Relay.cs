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
        await using var helper = new HelperSession(bridge, session, reconnect, TimeSpan.FromSeconds(2));
        Log.Info($"{helper.Name}: relaying to localhost:{port}");
        var elapsed = Stopwatch.StartNew();
        var window = new ListenerWindow();
        try
        {
            while (!ListenerWindow.Expired(elapsed.Elapsed))
            {
                if (!window.CanContinue(IsListening(port), elapsed.Elapsed))
                {
                    Log.Info($"{helper.Name}: nothing listens on localhost:{port} any more");
                    return;
                }

                if (!await helper.PollAsync(port, cancel))
                    return;
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
    }

    /// <summary>A browser launcher may exit before the app starts listening. Once seen, a listener must stay up.</summary>
    internal sealed class ListenerWindow
    {
        private bool _seen;

        public static bool Expired(TimeSpan elapsed) => elapsed >= TimeSpan.FromMinutes(30);

        public bool CanContinue(bool listening, TimeSpan elapsed)
        {
            if (listening)
                _seen = true;
            return listening || (!_seen && elapsed <= TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>The caller owns the initial connection; the session owns any replacements.</summary>
    internal sealed class HelperSession(ControlClient initial, string id, Func<Task<ControlClient?>>? reconnect, TimeSpan retryDelay) : IAsyncDisposable
    {
        private readonly ControlClient _initial = initial;
        private ControlClient _current = initial;
        private int _failures;
        public string Name { get; } = $"session {id[..8]}";

        public async Task<bool> PollAsync(int port, CancellationToken cancel)
        {
            (bool Closed, PendingRequest? Request) next;
            try
            {
                next = await _current.NextAsync(id, PollSeconds, cancel);
                _failures = 0;
            }
            catch (Exception e) when (IsUnreachable(e, cancel))
            {
                Log.Warn($"{Name}: helper unreachable: {e.Message}");
                return await RetryAsync(cancel);
            }
            if (next.Closed)
            {
                Log.Info($"{Name}: ended by the helper");
                return false;
            }
            if (next.Request is { } pending)
                await _current.ReplyAsync(id, pending.Id, await ForwardAsync(port, pending.Request, cancel), cancel);
            return true;
        }

        private async Task<bool> RetryAsync(CancellationToken cancel)
        {
            if (++_failures >= 15)
                return false;
            await Task.Delay(retryDelay, cancel);
            if (reconnect is not null && await reconnect() is { } replacement)
            {
                if (_current != _initial && _current != replacement)
                    _current.Dispose();
                _current = replacement;
            }
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _current.CloseAsync(id);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            {
                // The helper closes idle sessions itself.
            }
            finally
            {
                if (_current != _initial)
                    _current.Dispose();
            }
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
        catch (Exception e) when (IsUnreachable(e, cancel))
        {
            Log.Warn($"{description} failed: {e.Message}");
            return HttpResponse.Html(502, "Sign-in relay failed", $"Could not reach the application on {Environment.MachineName} (localhost:{port}): {e.Message}");
        }
    }

    private static bool IsUnreachable(Exception error, CancellationToken cancel) =>
        error is HttpRequestException || (error is OperationCanceledException && !cancel.IsCancellationRequested);

    private static bool IsListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);
}
