using System.Net.Sockets;
using System.Runtime.Versioning;
using AuthBridge.Common;

namespace AuthBridge.Client;

/// <summary>Finds the SSH tunnel sockets that the Windows helper keeps in ~/.authbridge.</summary>
[SupportedOSPlatform("linux")]
internal static class Bridge
{
    /// <summary>A client for the newest connected tunnel; sockets whose SSH session has ended are removed.</summary>
    public static async Task<ControlClient?> FindAsync(TimeSpan timeout)
    {
        foreach (var path in Sockets())
        {
            var client = ControlClient.ForSocket(path);
            try
            {
                if (await client.PingAsync(timeout) is not null)
                    return client;
            }
            catch (HttpRequestException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused } && path.StartsWith(Paths.StateDir))
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            {
                // Unreachable or unresponsive; try the next socket.
            }
            client.Dispose();
        }
        return null;
    }

    private static IEnumerable<string> Sockets() =>
        Environment.GetEnvironmentVariable("AUTHBRIDGE_SOCKET") is { Length: > 0 } socket ? [socket]
        : Directory.Exists(Paths.StateDir) ? Directory.EnumerateFiles(Paths.StateDir, "bridge-*.sock").OrderByDescending(File.GetLastWriteTimeUtc)
        : [];
}
