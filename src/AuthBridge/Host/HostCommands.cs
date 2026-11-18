using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AuthBridge.Common;
using Microsoft.Win32;

namespace AuthBridge.Host;

[SupportedOSPlatform("windows")]
internal static partial class HostCommands
{
    private const string Usage = """
        Usage (Windows):
          authbridge install-remote NAME [SSH OPTIONS...] DESTINATION
                                        install the client on a Linux machine and keep a tunnel to it
          authbridge install-startup    start the helper at logon
          authbridge start | stop | status | serve
        """;

    /// <summary>The same program built as a GUI app, which runs the helper without a console window.</summary>
    private static string Windowless => Path.Combine(AppContext.BaseDirectory, "authbridgew.exe");

    public static Task<int> RunAsync(string[] args) => args switch
    {
        [] or ["status"] => StatusAsync(),
        ["serve"] => ServeAsync(),
        ["start"] => StartAsync(),
        ["stop"] => StopAsync(),
        ["install-startup"] => Task.FromResult(InstallStartup()),
        ["install-remote", var name, .. var ssh] when ssh.Length > 0 => InstallRemoteAsync(name, ssh),
        _ => Task.FromResult(Program.Fail(Usage)),
    };

    private static async Task<int> ServeAsync()
    {
        Log.Init("host.log", echo: !Console.IsErrorRedirected);
        await using var helper = new Helper(OpenBrowser);
        try
        {
            helper.Start(Protocol.ControlPort);
        }
        catch (SocketException e)
        {
            Log.Info($"127.0.0.1:{Protocol.ControlPort} is unavailable ({e.Message}); another helper is probably running");
            return 1;
        }
        using var tunnels = new TunnelSet(Protocol.ControlPort);
        helper.Tunnels = tunnels.Statuses;
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };
        Log.Info($"helper listening on 127.0.0.1:{helper.Port} (pid {Environment.ProcessId})");

        var configTime = DateTime.MinValue;
        while (!stopping.IsCancellationRequested)
        {
            if (File.GetLastWriteTimeUtc(HostConfig.FilePath) is var time && time != configTime)
            {
                configTime = time;
                tunnels.Update(HostConfig.Load().Targets);
            }
            await helper.ExpireAsync();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stopping.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return 0;
    }

    private static void OpenBrowser(string url)
    {
        var info = HostConfig.Load().BrowserCommand is [var browser, .. var arguments]
            ? new ProcessStartInfo(browser, arguments.Select(argument => argument.Replace("{url}", url)))
            : new ProcessStartInfo(url) { UseShellExecute = true };
        Process.Start(info)?.Dispose();
    }

    private static async Task<int> StartAsync()
    {
        if (await PingAsync() is { } running)
        {
            Console.WriteLine($"authbridge helper is running (pid {running.Pid})");
            return 0;
        }
        Paths.EnsureStateDir();
        // Windows children inherit all inheritable handles; the helper must not keep the caller's output pipes open.
        foreach (var standardHandle in StandardHandles)
            SetHandleInformation(GetStdHandle(standardHandle), HandleFlagInherit, 0);
        // Redirected streams detach the helper from this console.
        var helper = Processes.StartInfo(File.Exists(Windowless) ? Windowless : Environment.ProcessPath!, ["serve"]);
        helper.WorkingDirectory = Paths.StateDir;
        Process.Start(helper)?.Dispose();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(200);
            if (await PingAsync() is { } started)
            {
                Console.WriteLine($"authbridge helper started (pid {started.Pid})");
                return 0;
            }
        }
        return Program.Fail($"authbridge helper did not start; see {Path.Combine(Paths.StateDir, "host.log")}");
    }

    private static async Task<int> StopAsync()
    {
        if (await PingAsync() is not { } running)
        {
            Console.WriteLine("authbridge helper is not running");
            return 0;
        }
        Process.GetProcessById(running.Pid).Kill(entireProcessTree: true);
        Console.WriteLine($"authbridge helper stopped (pid {running.Pid})");
        return 0;
    }

    private static async Task<int> StatusAsync()
    {
        if (await PingAsync() is not { } info)
            return Program.Fail("authbridge helper: not running");
        Console.WriteLine($"authbridge helper: running (pid {info.Pid}, 127.0.0.1:{Protocol.ControlPort}, {info.Sessions} relay session(s))");
        foreach (var tunnel in info.Tunnels)
        {
            var detail = tunnel.Detail.Length > 0 ? $" - {tunnel.Detail}" : "";
            Console.WriteLine($"  tunnel {tunnel.Name}: {tunnel.State} since {tunnel.Since.LocalDateTime:yyyy-MM-dd HH:mm:ss}{detail}");
        }
        return 0;
    }

    private static int InstallStartup()
    {
        var command = File.Exists(Windowless) ? $"\"{Windowless}\" serve" : $"\"{Environment.ProcessPath}\" start";
        using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        run.SetValue("authbridge", command);
        Console.WriteLine($"The helper starts at logon: {command}");
        return 0;
    }

    private static async Task<int> InstallRemoteAsync(string name, string[] ssh)
    {
        var probe = await Ssh.RunAsync(ssh, "uname -m; printf %s \"$HOME\"");
        var lines = probe.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (probe.ExitCode != 0 || lines is not [var machine, var home] || !home.StartsWith('/'))
            return Program.Fail($"cannot query the remote machine: {probe.Error.Trim()}");
        var architecture = machine switch { "x86_64" => "x64", "aarch64" or "arm64" => "arm64", _ => machine };
        var client = Path.Combine(AppContext.BaseDirectory, $"authbridge-linux-{architecture}");
        if (!File.Exists(client))
            return Program.Fail($"no client for {machine}: {client} is missing");

        await using (var binary = File.OpenRead(client))
        {
            var upload = await Ssh.RunAsync(ssh, "mkdir -p ~/.authbridge ~/.local/bin && chmod 700 ~/.authbridge && " +
                "cat > ~/.local/bin/authbridge.new && chmod 755 ~/.local/bin/authbridge.new && mv -f ~/.local/bin/authbridge.new ~/.local/bin/authbridge", binary);
            if (upload.ExitCode != 0)
                return Program.Fail($"upload failed: {upload.Error.Trim()}");
        }
        Console.WriteLine($"installed {home}/.local/bin/authbridge");
        Console.Write((await Ssh.RunAsync(ssh, "~/.local/bin/authbridge --setup-shell")).Output);

        var config = HostConfig.Load();
        (config with { Targets = [.. config.Targets.Where(target => target.Name != name), new Target(name, ssh, $"{home}/.authbridge")] }).Save();
        Console.WriteLine($"tunnel '{name}' saved to {HostConfig.FilePath}");
        if (await StartAsync() != 0)
            return 1;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            var status = await Ssh.RunAsync(ssh, "~/.local/bin/authbridge --status");
            if (status.ExitCode == 0)
            {
                Console.Write(status.Output);
                return 0;
            }
        }
        return Program.Fail("the tunnel is not connected yet; check 'authbridge status'");
    }

    private static async Task<PingResult?> PingAsync()
    {
        using var client = ControlClient.ForPort(Protocol.ControlPort);
        try
        {
            return await client.PingAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    // GetStdHandle identifiers of standard input, output and error.
    private static readonly int[] StandardHandles = [-10, -11, -12];
    private const uint HandleFlagInherit = 1;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int standardHandle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);
}
