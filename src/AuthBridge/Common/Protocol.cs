using System.Text.Json;
using System.Text.Json.Serialization;
using AuthBridge.Host;

namespace AuthBridge.Common;

internal static class Protocol
{
    public const int Version = 2;
    public const string HostName = "authbridge";
    public const string VersionHeader = "X-AuthBridge";

    /// <summary>The loopback port of the helper's control API; the SSH tunnels lead to it.</summary>
    public static readonly int ControlPort = int.TryParse(Environment.GetEnvironmentVariable("AUTHBRIDGE_PORT"), out var port) ? port : 28561;
}

internal sealed record OpenRequest(string Url, int? RelayPort, bool DryRun, string Client);

internal sealed record OpenResult(string? Session);

internal sealed record ErrorResult(string Error);

internal sealed record PingResult(int Version, int Pid, int Sessions, TunnelStatus[] Tunnels);

internal sealed record TunnelStatus(string Name, string State, string Detail, DateTimeOffset Since);

/// <summary>A browser request waiting in a relay session for the remote client's answer.</summary>
internal sealed record PendingRequest(string Id, HttpRequest Request);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(OpenRequest))]
[JsonSerializable(typeof(OpenResult))]
[JsonSerializable(typeof(ErrorResult))]
[JsonSerializable(typeof(PingResult))]
[JsonSerializable(typeof(PendingRequest))]
[JsonSerializable(typeof(HttpResponse))]
[JsonSerializable(typeof(HostConfig))]
internal sealed partial class Json : JsonSerializerContext;
