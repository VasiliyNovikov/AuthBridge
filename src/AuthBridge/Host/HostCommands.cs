using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AuthBridge.Common;

namespace AuthBridge.Host;

[SupportedOSPlatform("windows")]
internal static partial class HostCommands
{
    public static HostCommandLine CreateCommand() => new(StatusAsync, InstallRemoteAsync, RemoteAsync)
    {
        Cli.Command("serve", "Run the helper in the foreground.", HelperHost.RunAsync),
        Cli.Command("start", "Start the background helper.", () => StartAsync(AppContext.BaseDirectory)),
        Cli.Command("stop", "Stop the background helper.", StopAsync),
        Cli.Command("install", "Install for this user, start the helper now and at logon, and add it to PATH.", InstallAsync),
        TrayUpdate.CreateCommand(Paths.StateDir, UpdateAsync),
    };

    private static HelperControl Control => new(PingAsync, Console.Out, Console.Error);
    private static Task<int> StatusAsync() => Control.StatusAsync(Protocol.ControlPort);
    private static Task<int> StartAsync(string folder) => Control.StartAsync(() => LaunchHelper(folder),
        Path.Combine(Paths.StateDir, "host.log"), TimeSpan.FromMilliseconds(200));

    private static void LaunchHelper(string folder)
    {
        Paths.EnsureStateDir();
        // Windows children inherit all inheritable handles; the helper must not keep the caller's output pipes open.
        foreach (var standardHandle in StandardHandles)
            SetHandleInformation(GetStdHandle(standardHandle), HandleFlagInherit, 0);
        // Redirected streams detach the helper from this console.
        var helper = Processes.StartInfo(new InstalledPackage(folder).HelperExecutable, ["serve"]);
        helper.WorkingDirectory = Paths.StateDir;
        using var child = Process.Start(helper) ?? throw new IOException("could not launch the helper");
    }

    private static async Task<int> StopAsync()
    {
        if (!await StopIfRunningAsync())
            Console.WriteLine("authbridge helper is not running");
        return 0;
    }

    private static async Task<bool> StopIfRunningAsync()
    {
        if (await PingAsync() is not { } running)
            return false;
        HelperControl.StopProcess(running.Pid, TrayIcon.RequestExit, TimeSpan.FromSeconds(15));
        Console.WriteLine($"authbridge helper stopped (pid {running.Pid})");
        return true;
    }

    /// <summary>Installs the latest release over this installation and restarts the helper, which then updates the
    /// client on each machine when its tunnel connects.</summary>
    private static async Task<int> UpdateAsync(bool automatic)
    {
        var latest = await GitHubReleases.LatestAsync();
        if (!AppVersion.IsNewer(latest, AppVersion.Release))
        {
            Console.WriteLine($"authbridge {AppVersion.Release} is up to date");
            return 0;
        }
        Console.WriteLine($"downloading authbridge {latest}");
        var package = await GitHubReleases.DownloadAsync(latest);
        var installation = InstalledPackage.CurrentUser;
        if (!await installation.InstallAsync(package, () => StopForUpdateAsync(automatic)))
        {
            Console.WriteLine("Automatic update deferred: the helper is busy, unavailable, or no longer opted in.");
            return TrayUpdate.Deferred;
        }
        Console.WriteLine($"updated {installation.Location} from {AppVersion.Release} to {latest}");
        return await RestartAfterUpdateAsync(installation.Location);
    }

    private static async Task<int> RestartAfterUpdateAsync(string folder)
    {
        if (await StartAsync(folder) != 0)
            return 1;
        var info = await RemoteClient.WaitForChecksAsync(PingAsync, TimeSpan.FromSeconds(2));
        var status = await StatusAsync();
        if (info?.Tunnels.Any(tunnel => tunnel.State != "connected") == true)
            Console.WriteLine("Machines that are not connected get the new client when their tunnels connect.");
        return status;
    }

    private static async Task<bool> StopForUpdateAsync(bool automatic)
    {
        if (!automatic)
        {
            await StopAsync();
            return true;
        }
        // A sign-in may have started, or the user may have opted out, while the package was downloading.
        return await PingAsync() is { Sessions: 0 } running &&
            HelperControl.StopProcess(running.Pid, TrayIcon.RequestIdleExit, TimeSpan.FromSeconds(15), requireApproval: true);
    }

    /// <summary>Installs this package for the current user unless it runs from the installation, starts the helper now
    /// and at logon, and adds the installation to the user PATH.</summary>
    private static async Task<int> InstallAsync()
    {
        var installation = InstalledPackage.CurrentUser;
        if (await installation.InstallFromAsync(AppContext.BaseDirectory, StopIfRunningAsync))
            Console.WriteLine($"installed authbridge {AppVersion.Release} in {installation.Location}");
        UserRegistration.Register(installation);
        return await StartAsync(installation.Location);
    }

    private static async Task<int> InstallRemoteAsync(string name, string[] ssh)
    {
        var machine = await RemoteClient.ProbeAsync(ssh);
        var version = await machine.EnsureVersionAsync(AppVersion.Release, arch => RemoteClient.UploadAsync(ssh, arch), installIfMissing: true);
        Console.WriteLine($"authbridge {version} at {machine.Home}/.local/bin/authbridge");
        var setup = await Ssh.RunAsync(ssh, "~/.local/bin/authbridge --setup-shell");
        Console.Write(setup.OutputOrThrow("remote shell setup failed"));

        HostConfig.Edit(HostConfig.FilePath, config => config with
        {
            Targets = [.. config.Targets.Where(target => target.Name != name), new Target(name, ssh, $"{machine.Home}/.authbridge")],
        });
        Console.WriteLine($"tunnel '{name}' saved to {HostConfig.FilePath}");
        if (await StartAsync(AppContext.BaseDirectory) != 0)
            return 1;
        var status = await RemoteClient.WaitForTunnelAsync(name, PingAsync, command => Ssh.RunAsync(ssh, command), TimeSpan.FromSeconds(3));
        if (status is null)
            return Program.Fail("the tunnel is not connected yet; check 'authbridge status'");
        Console.Write(status);
        return 0;
    }

    /// <summary>Runs the client with the given arguments on a configured machine.</summary>
    private static async Task<int> RemoteAsync(string name, string[] arguments)
    {
        var targets = HostConfig.Load().Targets;
        if (targets.FirstOrDefault(target => target.Name == name) is not { } target)
            return Program.Fail($"no machine named '{name}'; configured: {string.Join(", ", targets.Select(target => target.Name))}");
        var command = string.Join(' ', ["~/.local/bin/authbridge", .. arguments.Select(PosixShell.Quote)]);
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
}
