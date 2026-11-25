using System.Globalization;
using System.Net;
using System.Text;

namespace AuthBridge.Common;

internal readonly record struct Header(string Name, string Value);

internal sealed record HttpRequest(string Method, string Target, Header[] Headers, byte[] Body)
{
    public string? GetHeader(string name) => Http.Find(Headers, name);
}

internal sealed record HttpResponse(int Status, Header[] Headers, byte[] Body, string? Reason = null)
{
    public static HttpResponse Html(int status, string title, string message)
    {
        title = WebUtility.HtmlEncode(title);
        var html = $"""<!DOCTYPE html><html><head><meta charset="utf-8"><title>{title}</title></head>""" +
            $"""<body style="font-family:sans-serif"><h3>{title}</h3><p>{WebUtility.HtmlEncode(message)}</p></body></html>""";
        return new(status, [new("Content-Type", "text/html; charset=utf-8")], Encoding.UTF8.GetBytes(html));
    }
}

/// <summary>Minimal HTTP/1.1: one request per connection, bodies framed by Content-Length.</summary>
internal static class Http
{
    private const int MaxBodyBytes = 16 << 20;
    private const int MaxHeaderBytes = 64 << 10;

    // Every hop frames messages itself, so these headers are never relayed.
    // Stryker disable once all : a reused test host initializes statics once, so these mutants would leak into other runs
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Content-Length",
    };

    public static bool IsHopByHop(string name) => HopByHop.Contains(name);

    public static string? Find(IEnumerable<Header> headers, string name) =>
        headers.FirstOrDefault(header => header.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The decoded query parameters of a URL or request target.</summary>
    public static IEnumerable<(string Name, string Value)> Query(string url)
    {
        var start = url.IndexOf('?');
        if (start < 0)
            yield break;
        var end = url.IndexOf('#', start);
        foreach (var pair in url[(start + 1)..(end < 0 ? url.Length : end)].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            yield return separator < 0 ? (Decode(pair), "") : (Decode(pair[..separator]), Decode(pair[(separator + 1)..]));
        }

        static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    }

    public static async Task<HttpRequest> ReadRequestAsync(Stream stream, CancellationToken cancel)
    {
        var buffer = new byte[MaxHeaderBytes];
        int length = 0, end;
        while ((end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8)) < 0)
        {
            if (length == buffer.Length)
                throw new InvalidDataException("The request headers are too large.");
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancel);
            if (read == 0)
                throw new EndOfStreamException();
            length += read;
        }

        var lines = Encoding.Latin1.GetString(buffer, 0, end).Split("\r\n");
        if (lines[0].Split(' ') is not [var method, var target, _])
            throw new InvalidDataException("Malformed request line.");
        Header[] headers = [.. lines[1..].Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2)
            .Select(parts => new Header(parts[0].Trim(), parts[1].Trim()))];
        var contentLength = BodyLength(headers);
        var body = new byte[contentLength];
        var buffered = Math.Min(length - end - 4, contentLength);
        buffer.AsSpan(end + 4, buffered).CopyTo(body);
        await stream.ReadExactlyAsync(body.AsMemory(buffered), cancel);
        return new HttpRequest(method, target, headers, body);
    }

    private static int BodyLength(Header[] headers)
    {
        if (Find(headers, "Transfer-Encoding") is not null)
            throw new InvalidDataException("Chunked requests are not supported.");
        var lengths = headers.Where(h => h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (lengths.Length == 0)
            return 0;
        if (lengths is [var text] && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var length) && length <= MaxBodyBytes)
            return length;
        throw new InvalidDataException("Invalid request body length.");
    }

    public static async Task WriteResponseAsync(Stream stream, HttpResponse response, bool includeBody, CancellationToken cancel)
    {
        var head = new StringBuilder($"HTTP/1.1 {response.Status} {response.Reason ?? ((HttpStatusCode)response.Status).ToString()}\r\n");
        foreach (var (name, value) in response.Headers.Where(header => !IsHopByHop(header.Name)))
            head.Append($"{name}: {value}\r\n");
        head.Append($"Content-Length: {response.Body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), cancel);
        if (includeBody)
            await stream.WriteAsync(response.Body, cancel);
    }
}
