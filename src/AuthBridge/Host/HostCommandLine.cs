namespace AuthBridge.Host;

/// <summary>Routes the Windows CLI without doing any process, registry, or SSH work while parsing arguments.</summary>
internal sealed class HostCommandLine(IReadOnlyDictionary<string, Func<Task<int>>> local,
    Func<string, string[], Task<int>> installRemote, Func<string, string[], Task<int>> remote)
{
    private const string Usage = """
        Usage (Windows):
          authbridge install            install for this user, start the helper now and at logon, and add it to PATH
          authbridge install-remote NAME [SSH OPTIONS...] DESTINATION
                                        install the client on a Linux machine and keep a tunnel to it
          authbridge remote NAME ARGS...
                                        run the client on that machine, e.g. remote devbox --setup-gcm
          authbridge update             install the latest release; Linux machines get it when they connect
          authbridge start | stop | status | serve
        """;

    public Task<int> RunAsync(string[] args) => args switch
    {
        [] => local["status"](),
        [var name] when local.TryGetValue(name, out var command) => command(),
        ["install-remote", var name, .. var ssh] when ssh.Length > 0 => installRemote(name, ssh),
        ["remote", var name, .. var arguments] when arguments.Length > 0 => remote(name, arguments),
        _ => Task.FromResult(Program.Fail(Usage)),
    };
}
