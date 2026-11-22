using System.Diagnostics;
using System.Runtime.Versioning;
using AuthBridge.Common;

namespace AuthBridge.Client;

[SupportedOSPlatform("linux")]
internal static class ClientCommands
{
    public static ClientCommandLine CreateCommand() => new(OpenAllAsync, Wrappers.GcmAsync, Wrappers.NuGetPluginAsync,
        RelayAsync, CancelAsync,
        Cli.Command("--status", "Show the connections to Windows helpers.", StatusAsync),
        Cli.Command("--env", "Print the command that sets BROWSER in this shell.", Setup.Env),
        Cli.Command("--setup-shell", "Set BROWSER in new shells.", Setup.Shell),
        Cli.Command("--setup-gcm", "Set up Git Credential Manager.", Setup.GcmAsync),
        Cli.Command("--setup-nuget", "Set up the Azure Artifacts credential provider.", Setup.NuGet),
        Cli.Command("--setup-all", "Set up the shell and all installed integrations.", Setup.AllAsync));

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

    private static async Task<int> RelayAsync(string socket, string session, int port, string? owner)
    {
        Posix.Detach();
        Log.Init("client.log");
        using var bridge = ControlClient.ForSocket(socket);
        owner ??= await ReadOwnerAsync(bridge); // Workers launched by an older client have no owner argument.
        await Relay.RunAsync(bridge, session, port, CancellationToken.None, async () => owner is null ? null :
            (await Bridge.FindAsync(TimeSpan.FromSeconds(5), info => BrowserClient.OwnerKey(info) == owner))?.Client);
        return 0;
    }

    private static async Task<string?> ReadOwnerAsync(ControlClient bridge)
    {
        try
        {
            return await bridge.PingAsync(TimeSpan.FromSeconds(5)) is { } info ? BrowserClient.OwnerKey(info) : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            Log.Warn($"cannot identify the helper for this relay: {e.Message}");
            return null;
        }
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
