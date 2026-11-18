using System.Net.Sockets;
using AuthBridge.Common;

namespace AuthBridge.Client;

/// <summary>Finds the Windows helpers that the SSH tunnel sockets in ~/.authbridge lead to, one per connected PC.</summary>
internal static class Bridge
{
    /// <summary>How much longer the other helpers may take once one has answered: the tunnel of a PC that has gone to
    /// sleep can stay without answers for a while.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(1);

    /// <summary>A helper that answered through a tunnel socket, and when that tunnel connected.</summary>
    public sealed record Connection(ControlClient Client, PingResult Info, DateTime Connected) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    /// <summary>The preferred helper among those that <paramref name="match"/> accepts.</summary>
    public static async Task<Connection?> FindAsync(TimeSpan timeout, Func<PingResult, bool>? match = null)
    {
        var connections = await FindAllAsync(timeout);
        var found = connections.FirstOrDefault(connection => match?.Invoke(connection.Info) ?? true);
        foreach (var other in connections.Where(connection => !ReferenceEquals(connection, found)))
            other.Dispose();
        return found;
    }

    /// <summary>The helpers that answer, the preferred one first; sockets whose SSH session has ended are removed.</summary>
    public static async Task<List<Connection>> FindAllAsync(TimeSpan timeout)
    {
        using var answered = new CancellationTokenSource();
        var connections = await Task.WhenAll(Sockets().Select(path => PingAsync(path, timeout, answered)));
        return [.. Preferred(connections.OfType<Connection>())];
    }

    /// <summary>Puts first the helper whose PC had keyboard or mouse input most recently, so that sign-ins open where the
    /// user is, and otherwise the one whose tunnel connected last.</summary>
    public static IEnumerable<Connection> Preferred(IEnumerable<Connection> connections) =>
        connections.OrderBy(connection => connection.Info.IdleSeconds ?? int.MaxValue).ThenByDescending(connection => connection.Connected);

    private static async Task<Connection?> PingAsync(string path, TimeSpan timeout, CancellationTokenSource answered)
    {
        var client = ControlClient.ForSocket(path);
        try
        {
            if (await client.PingAsync(timeout, answered.Token) is { } info)
            {
                answered.CancelAfter(Grace);
                return new Connection(client, info, File.GetLastWriteTimeUtc(path));
            }
        }
        catch (HttpRequestException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused } && path.StartsWith(Paths.StateDir))
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            // Unreachable or unresponsive.
        }
        client.Dispose();
        return null;
    }

    private static IEnumerable<string> Sockets() =>
        Environment.GetEnvironmentVariable("AUTHBRIDGE_SOCKET") is { Length: > 0 } socket ? [socket]
        : Directory.Exists(Paths.StateDir) ? Directory.EnumerateFiles(Paths.StateDir, "bridge-*.sock")
        : [];
}
