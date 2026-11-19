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
    public static Task<List<Connection>> FindAllAsync(TimeSpan timeout) =>
        FindAllAsync(Paths.StateDir, Environment.GetEnvironmentVariable("AUTHBRIDGE_SOCKET"), timeout);

    internal static async Task<List<Connection>> FindAllAsync(string stateDir, string? socketOverride, TimeSpan timeout)
    {
        if (socketOverride is { Length: > 0 })
        {
            var connection = await PingAsync(socketOverride, timeout, CancellationToken.None, removeIfStale: false);
            return connection is null ? [] : [connection];
        }
        if (!Directory.Exists(stateDir))
            return [];
        using var grace = new CancellationTokenSource();
        var probes = Directory.EnumerateFiles(stateDir, "bridge-*.sock")
            .Select(path => PingAsync(path, timeout, grace.Token, removeIfStale: true)).ToArray();
        var connections = await WaitForAnswersAsync(probes, () => grace.CancelAfter(Grace));
        return [.. Preferred(connections.OfType<Connection>())];
    }

    internal static async Task<Connection?[]> WaitForAnswersAsync(Task<Connection?>[] probes, Action startGrace)
    {
        await foreach (var completed in Task.WhenEach(probes))
        {
            if (completed.IsCompletedSuccessfully && await completed is not null)
            {
                startGrace();
                break;
            }
        }
        // Keep discovery order for equally preferred helpers. Each successful probe transfers ownership to the caller.
        try
        {
            return await Task.WhenAll(probes);
        }
        catch
        {
            foreach (var probe in probes.Where(probe => probe.IsCompletedSuccessfully))
                probe.Result?.Dispose();
            throw;
        }
    }

    /// <summary>Puts first the helper whose PC had keyboard or mouse input most recently, so that sign-ins open where the
    /// user is, and otherwise the one whose tunnel connected last.</summary>
    public static IEnumerable<Connection> Preferred(IEnumerable<Connection> connections) =>
        connections.OrderBy(connection => connection.Info.IdleSeconds ?? int.MaxValue).ThenByDescending(connection => connection.Connected);

    private static async Task<Connection?> PingAsync(string path, TimeSpan timeout, CancellationToken cancel, bool removeIfStale)
    {
        var client = ControlClient.ForSocket(path);
        Connection? connection = null;
        try
        {
            if (await client.PingAsync(timeout, cancel) is { } info)
                connection = new Connection(client, info, File.GetLastWriteTimeUtc(path));
        }
        catch (HttpRequestException e) when (removeIfStale && e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            // Unreachable or unresponsive.
        }
        finally
        {
            if (connection is null)
                client.Dispose();
        }
        return connection;
    }
}
