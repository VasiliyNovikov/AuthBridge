using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuthBridge.Common;

internal static class Protocol
{
    public const int Version = 2;
    public const string HostName = "authbridge";
    public const string VersionHeader = "X-AuthBridge";

    /// <summary>The loopback port of the helper's control API; the SSH tunnels lead to it.</summary>
    public static readonly int ControlPort = int.TryParse(Environment.GetEnvironmentVariable("AUTHBRIDGE_PORT"), out var port) ? port : 28561;
}

internal sealed record OpenRequest(string Url, int? RelayPort, bool DryRun, string Client)
{
    /// <summary>Only absolute http and https URLs are opened in the browser.</summary>
    public static bool CanOpen(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}

internal sealed record OpenResult(string? Session);

internal sealed record ErrorResult(string Error);

/// <summary>The helper's state. Machine and IdleSeconds, the time since the last keyboard or mouse input on that PC, tell
/// clients which of several PCs the user is at; older helpers do not send them.</summary>
internal sealed record PingResult(int Version, int Pid, int Sessions, TunnelStatus[] Tunnels, string? Machine = null, int? IdleSeconds = null);

/// <summary>A tunnel's state; <c>Client</c> is the remote client's version once the helper has checked it.</summary>
internal sealed record TunnelStatus(string Name, string State, string Detail, DateTimeOffset Since, string Client = "", string? Socket = null)
{
    // The values of State.
    public const string Starting = "starting", Connecting = "connecting", Connected = "connected", Disconnected = "disconnected", Stopped = "stopped";
}

/// <summary>A browser request waiting in a relay session for the remote client's answer.</summary>
internal sealed record PendingRequest(string Id, HttpRequest Request);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(OpenRequest))]
[JsonSerializable(typeof(OpenResult))]
[JsonSerializable(typeof(ErrorResult))]
[JsonSerializable(typeof(PingResult))]
[JsonSerializable(typeof(PendingRequest))]
[JsonSerializable(typeof(HttpResponse))]
internal sealed partial class Json : JsonSerializerContext;
