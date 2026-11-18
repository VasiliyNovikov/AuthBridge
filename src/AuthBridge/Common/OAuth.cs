using System.Text;

namespace AuthBridge.Common;

/// <summary>Helpers for OAuth authorization URLs that redirect to a loopback listener (RFC 8252).</summary>
internal static class OAuth
{
    private static readonly string[] DescribedParameters = ["client_id", "scope", "prompt", "response_mode"];

    /// <summary>The loopback port the URL or its redirect_uri points to: where the application waits for the redirect.</summary>
    public static int? LoopbackPort(string url)
    {
        foreach (var candidate in Http.Query(url).Where(p => p.Name == "redirect_uri").Select(p => p.Value).Prepend(url))
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
                return uri.Port;
        }
        return null;
    }

    /// <summary>The URL without query and fragment, which can carry codes and tokens.</summary>
    public static string Redact(string url) => url.Split('?', '#')[0];

    /// <summary>The non-secret request parameters, for logs.</summary>
    public static string Describe(string url) => string.Join(' ', Http.Query(url)
        .Where(p => DescribedParameters.Contains(p.Name))
        .Select(p => $"{p.Name}={p.Value[..Math.Min(p.Value.Length, 120)]}"));

    /// <summary>
    /// An OAuth error response for the application waiting on <paramref name="port"/>, which makes it
    /// fail at once instead of waiting for a redirect that cannot arrive.
    /// </summary>
    public static HttpRequest? CancelRequest(string url, int port, string reason)
    {
        var query = Http.Query(url).ToLookup(p => p.Name, p => p.Value);
        if (query["redirect_uri"].FirstOrDefault() is not { } redirect || query["state"].FirstOrDefault() is not { } state)
            return null;
        var form = $"error=temporarily_unavailable&error_description={Uri.EscapeDataString(reason)}&state={Uri.EscapeDataString(state)}";
        var path = Uri.TryCreate(redirect, UriKind.Absolute, out var uri) ? uri.AbsolutePath : "/";
        Header host = new("Host", $"localhost:{port}");
        return query["response_mode"].FirstOrDefault() == "form_post"
            ? new HttpRequest("POST", path, [host, new("Content-Type", "application/x-www-form-urlencoded")], Encoding.ASCII.GetBytes(form))
            : new HttpRequest("GET", $"{path}?{form}", [host], []);
    }
}
