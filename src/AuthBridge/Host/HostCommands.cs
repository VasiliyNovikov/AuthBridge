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
          authbridge install            install for this user, start the helper now and at logon, and add it to PATH
          authbridge install-remote NAME [SSH OPTIONS...] DESTINATION
                                        install the client on a Linux machine and keep a tunnel to it
          authbridge remote NAME ARGS...
                                        run the client on that machine, e.g. remote devbox --setup-gcm
          authbridge update             install the latest release; Linux machines get it when they connect
          authbridge start | stop | status | serve
        """;

    /// <summary>The same program built as a GUI app, which runs the helper without a console window.</summary>
    private const string Windowless = "authbridgew.exe";

    public static Task<int> RunAsync(string[] args) => args switch
    {
        [] or ["status"] => StatusAsync(),
        ["serve"] => ServeAsync(),
        ["start"] => StartAsync(AppContext.BaseDirectory),
        ["stop"] => StopAsync(),
        ["install"] => InstallAsync(),
        ["install-remote", var name, .. var ssh] when ssh.Length > 0 => InstallRemoteAsync(name, ssh),
        ["remote", var name, .. var arguments] when arguments.Length > 0 => RemoteAsync(name, arguments),
        ["update"] => UpdateAsync(),
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
        Updater.DeleteReplacedFiles();
        using var tunnels = new TunnelSet(Protocol.ControlPort);
        helper.Tunnels = tunnels.Statuses;
        // Not disposed, because the tray icon may cancel it at any time.
        var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };
        using var tray = TrayIcon.Start(tunnels.Statuses, stopping.Cancel);
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
        Log.Info("helper stopping");
        return 0;
    }

    private static void OpenBrowser(string url)
    {
        var info = HostConfig.Load().BrowserCommand is [var browser, .. var arguments]
            ? new ProcessStartInfo(browser, arguments.Select(argument => argument.Replace("{url}", url)))
            : new ProcessStartInfo(url) { UseShellExecute = true };
        Process.Start(info)?.Dispose();
    }

    /// <summary>Starts the helper in a folder, unless one is running.</summary>
    private static async Task<int> StartAsync(string folder)
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
        var windowless = Path.Combine(folder, Windowless);
        var helper = Processes.StartInfo(File.Exists(windowless) ? windowless : Path.Combine(folder, "authbridge.exe"), ["serve"]);
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
        using var helper = Process.GetProcessById(running.Pid);
        // Exiting through its tray icon lets the helper remove the icon and close its tunnels.
        if (!TrayIcon.RequestExit(running.Pid) || !helper.WaitForExit(TimeSpan.FromSeconds(15)))
            helper.Kill(entireProcessTree: true);
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
            var client = tunnel.Client.Length > 0 ? $", client {tunnel.Client}" : "";
            Console.WriteLine($"  tunnel {tunnel.Name}: {tunnel.State} since {tunnel.Since.LocalDateTime:yyyy-MM-dd HH:mm:ss}{client}{detail}");
        }
        return 0;
    }

    /// <summary>Installs the latest release over this installation and restarts the helper, which then updates the
    /// client on each machine when its tunnel connects.</summary>
    private static async Task<int> UpdateAsync()
    {
        var latest = await Updater.LatestAsync();
        if (!Updater.IsNewer(latest, Updater.Current))
        {
            Console.WriteLine($"authbridge {Updater.Current} is up to date");
            return 0;
        }
        Console.WriteLine($"downloading authbridge {latest}");
        var package = await Updater.DownloadAsync(latest);
        await StopAsync();
        Updater.Install(package);
        Console.WriteLine($"updated {Updater.InstallDir} from {Updater.Current} to {latest}");
        if (await StartAsync(Updater.InstallDir) != 0)
            return 1;
        PingResult? info = null;
        for (var attempt = 0; attempt < 45; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            info = await PingAsync();
            if (info is null || info.Tunnels.All(tunnel => tunnel.State == "connected" ? tunnel.Client.Length > 0 : tunnel.State == "disconnected"))
                break;
        }
        await StatusAsync();
        if (info?.Tunnels.Any(tunnel => tunnel.State != "connected") == true)
            Console.WriteLine("Machines that are not connected get the new client when their tunnels connect.");
        return 0;
    }

    /// <summary>Installs this package for the current user unless it runs from the installation, starts the helper now
    /// and at logon, and adds the installation to the user PATH.</summary>
    private static async Task<int> InstallAsync()
    {
        var folder = Updater.InstallDir;
        if (!string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), folder, StringComparison.OrdinalIgnoreCase))
        {
            // A running helper restarts with the new version.
            if (await PingAsync() is not null)
                await StopAsync();
            Updater.InstallFrom(AppContext.BaseDirectory);
            Console.WriteLine($"installed authbridge {Updater.Current} in {folder}");
        }
        var windowless = Path.Combine(folder, Windowless);
        var command = File.Exists(windowless) ? $"\"{windowless}\" serve" : $"\"{Path.Combine(folder, "authbridge.exe")}\" start";
        using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            run.SetValue("authbridge", command);
        Console.WriteLine($"The helper starts at logon: {command}");

        using var environment = Registry.CurrentUser.CreateSubKey("Environment");
        // Unexpanded, so that entries such as %USERPROFILE%\bin keep working.
        var path = environment.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var entries = path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(entry)));
        if (entries.Contains(folder, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"{folder} is already on your PATH");
        else
        {
            var kind = path.Length > 0 ? environment.GetValueKind("Path") : RegistryValueKind.ExpandString;
            environment.SetValue("Path", path.Length == 0 || path.EndsWith(';') ? path + folder : $"{path};{folder}", kind);
            // Tells running programs such as Explorer, so that terminals started from now on see the new PATH.
            SendMessageTimeout(HwndBroadcast, WmSettingChange, 0, "Environment", SmtoAbortIfHung, 5000, out _);
            Console.WriteLine($"Added {folder} to your PATH; terminals started from now on can run 'authbridge'");
        }
        return await StartAsync(folder);
    }

    private static async Task<int> InstallRemoteAsync(string name, string[] ssh)
    {
        var machine = await RemoteClient.ProbeAsync(ssh);
        await RemoteClient.UploadAsync(ssh, machine.Architecture);
        Console.WriteLine($"installed {machine.Home}/.local/bin/authbridge");
        Console.Write((await Ssh.RunAsync(ssh, "~/.local/bin/authbridge --setup-shell")).Output);

        var config = HostConfig.Load();
        (config with { Targets = [.. config.Targets.Where(target => target.Name != name), new Target(name, ssh, $"{machine.Home}/.authbridge")] }).Save();
        Console.WriteLine($"tunnel '{name}' saved to {HostConfig.FilePath}");
        if (await StartAsync(AppContext.BaseDirectory) != 0)
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

    /// <summary>Runs the client with the given arguments on a configured machine.</summary>
    private static async Task<int> RemoteAsync(string name, string[] arguments)
    {
        var targets = HostConfig.Load().Targets;
        if (targets.FirstOrDefault(target => target.Name == name) is not { } target)
            return Program.Fail($"no machine named '{name}'; configured: {string.Join(", ", targets.Select(target => target.Name))}");
        // Single quotes pass each argument to the remote shell unchanged.
        var command = string.Join(' ', ["~/.local/bin/authbridge", .. arguments.Select(argument => $"'{argument.Replace("'", "'\\''")}'")]);
        var (exitCode, output, error) = await Ssh.RunAsync(target.Ssh, command);
        Console.Write(output);
        Console.Error.Write(error);
        return exitCode;
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

    private const nint HwndBroadcast = 0xFFFF;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeout(nint window, uint message, nint wParam, string lParam, uint flags, uint timeout, out nint result);
}
