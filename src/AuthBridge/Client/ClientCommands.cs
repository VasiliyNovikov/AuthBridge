using System.Diagnostics;
using System.Runtime.Versioning;
using AuthBridge.Common;

namespace AuthBridge.Client;

[SupportedOSPlatform("linux")]
internal static class ClientCommands
{
    private const string RelayCommand = "__relay";
    private const string CancelCommand = "__cancel";
    private const string Usage = """
        Usage (Linux):
          authbridge URL...       open URLs in the Windows browser (set as $BROWSER)
          authbridge --status     check the connection to the Windows helper
          authbridge --env        print the command that sets BROWSER: eval "$(authbridge --env)"
          authbridge --setup-shell | --setup-gcm | --setup-nuget | --setup-all
        """;

    public static Task<int> RunAsync(string[] args) => args switch
    {
        ["--status"] => StatusAsync(),
        ["--env"] => Task.FromResult(Setup.Env()),
        ["--setup-shell"] => Task.FromResult(Setup.Shell()),
        ["--setup-gcm"] => Setup.GcmAsync(),
        ["--setup-nuget"] => Task.FromResult(Setup.NuGet()),
        ["--setup-all"] => Setup.AllAsync(),
        ["--gcm", var program, .. var rest] => Wrappers.GcmAsync(program, rest),
        ["--nuget-plugin", .. var rest] => Wrappers.NuGetPluginAsync(rest),
        [RelayCommand, var socket, var session, var port] => RelayAsync(socket, session, int.Parse(port)),
        [CancelCommand, var port, var url, var reason] => CancelAsync(int.Parse(port), url, reason),
        _ when args.Where(arg => !arg.StartsWith('-')).ToArray() is [_, ..] urls => OpenAllAsync(urls, dryRun: args.Contains("--dry-run")),
        _ => Task.FromResult(Program.Fail(Usage)),
    };

    private static async Task<int> StatusAsync()
    {
        var connections = await Bridge.FindAllAsync(TimeSpan.FromSeconds(5));
        if (connections.Count == 0)
            return Program.Fail("authbridge: not connected to the Windows helper");
        var role = "sign-ins open on";
        foreach (var connection in connections)
        {
            var idle = connection.Info.IdleSeconds is { } seconds ? $" (idle {TimeSpan.FromSeconds(seconds):c})" : "";
            Console.WriteLine($"authbridge: {role} {connection.Info.Machine ?? "the Windows helper"}{idle} via {connection.Client.Endpoint}");
            role = "also connected to";
            connection.Dispose();
        }
        return 0;
    }

    private static async Task<int> OpenAllAsync(string[] urls, bool dryRun)
    {
        Log.Init("client.log");
        var result = 0;
        foreach (var url in urls)
            result = Math.Max(result, await OpenAsync(url, dryRun));
        return result;
    }

    private static async Task<int> OpenAsync(string url, bool dryRun)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Program.Fail($"authbridge: only http and https URLs can be opened in the Windows browser: {url}");
        var port = OAuth.LoopbackPort(url);
        Log.Info($"open {OAuth.Redact(url)} {OAuth.Describe(url)} requested by {Posix.ParentChain()}");

        using var bridge = await Bridge.FindAsync(TimeSpan.FromSeconds(5));
        if (bridge is null)
        {
            Console.Error.WriteLine("authbridge: not connected to the Windows helper");
            return Cancel(url, port, "the Windows helper is not connected") ? 0 : Program.Fail($"Open this URL manually:\n  {url}");
        }
        var (status, session, error) = await bridge.Client.OpenAsync(new OpenRequest(url, port, dryRun, Environment.MachineName));
        if (status == 200)
        {
            if (session is not null)
                Spawn(RelayCommand, bridge.Client.Endpoint, session, port.ToString()!);
            Console.Error.WriteLine($"authbridge: opened in the browser on {bridge.Info.Machine ?? "Windows"}{(session is null ? "" : $"; relaying http://localhost:{port}")}");
            return 0;
        }
        Console.Error.WriteLine($"authbridge: {error}");
        // E.g. Windows reserves the port the application picked; a retry gets another one.
        return status == 409 && Cancel(url, port, $"{error}; retry the sign-in") ? 0 : 1;
    }

    /// <summary>Makes the application waiting for the URL's redirect fail at once, since the redirect cannot be relayed.</summary>
    private static bool Cancel(string url, int? port, string reason)
    {
        if (port is null || OAuth.CancelRequest(url, port.Value, reason) is null)
            return false;
        Spawn(CancelCommand, port.Value.ToString(), url, reason);
        Console.Error.WriteLine("authbridge: sign-in cancelled; retry it later or use another sign-in method");
        return true;
    }

    /// <summary>Runs a command in the background: the caller waits for its browser command before it serves the redirect.</summary>
    private static void Spawn(params string[] args)
    {
        using var process = Process.Start(Processes.StartInfo(Environment.ProcessPath!, args))!;
        process.StandardInput.Close();
    }

    private static async Task<int> RelayAsync(string socket, string session, int port)
    {
        Posix.Detach();
        Log.Init("client.log");
        using var bridge = ControlClient.ForSocket(socket);
        // A reconnected tunnel listens on a new socket, while the helper keeps the session for a while. The helpers of
        // other PCs do not know the session.
        PingResult? owner = null;
        try
        {
            owner = await bridge.PingAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            // Then any helper will do.
        }
        await Relay.RunAsync(bridge, session, port, CancellationToken.None, async () =>
            (await Bridge.FindAsync(TimeSpan.FromSeconds(5), info => owner is null || (info.Machine, info.Pid) == (owner.Machine, owner.Pid)))?.Client);
        return 0;
    }

    private static async Task<int> CancelAsync(int port, string url, string reason)
    {
        Posix.Detach();
        Log.Init("client.log");
        if (OAuth.CancelRequest(url, port, reason) is { } request)
            await Relay.ForwardAsync(port, request, CancellationToken.None);
        return 0;
    }
}
