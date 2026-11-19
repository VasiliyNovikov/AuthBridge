using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using AuthBridge.Common;

namespace AuthBridge.Client;

[SupportedOSPlatform("linux")]
internal static class ClientCommands
{
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
        [BrowserClient.RelayCommand or BrowserClient.CancelCommand, ..] => RunWorkerAsync(args),
        _ when args.Where(arg => !arg.StartsWith('-')).ToArray() is [_, ..] urls => OpenAllAsync(urls, dryRun: args.Contains("--dry-run")),
        _ => Task.FromResult(Program.Fail(Usage)),
    };

    private static Task<int> RunWorkerAsync(string[] args) => args switch
    {
        [BrowserClient.RelayCommand, var socket, var session, var port] when TryPort(port, out var number) => RelayAsync(socket, session, number),
        [BrowserClient.CancelCommand, var port, var url, var reason] when TryPort(port, out var number) => CancelAsync(number, url, reason),
        _ => Task.FromResult(Program.Fail("authbridge: invalid background worker arguments")),
    };

    private static bool TryPort(string value, out int port) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

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
        var browser = new BrowserClient(StartWorker, Console.Error);
        var result = 0;
        foreach (var url in urls)
            result = Math.Max(result, await OpenAsync(browser, url, dryRun));
        return result;
    }

    private static async Task<int> OpenAsync(BrowserClient browser, string url, bool dryRun)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Program.Fail($"authbridge: only http and https URLs can be opened in the Windows browser: {url}");
        Log.Info($"open {OAuth.Redact(url)} {OAuth.Describe(url)} requested by {Posix.ParentChain()}");
        var request = new OpenRequest(url, OAuth.LoopbackPort(url), dryRun, Environment.MachineName);
        using var bridge = await Bridge.FindAsync(TimeSpan.FromSeconds(5));
        return await browser.OpenAsync(request, bridge);
    }

    /// <summary>Runs a command in the background: the caller waits for its browser command before it serves the redirect.</summary>
    private static void StartWorker(string[] args)
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
