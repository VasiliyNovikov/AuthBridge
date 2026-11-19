using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AuthBridge.Common;
using Microsoft.Win32;

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
        Cli.Command("update", "Install the latest release; Linux clients get it when they connect.", UpdateAsync),
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
        using var helper = Process.GetProcessById(running.Pid);
        // Exiting through its tray icon lets the helper remove the icon and close its tunnels.
        if (!TrayIcon.RequestExit(running.Pid) || !helper.WaitForExit(TimeSpan.FromSeconds(15)))
            helper.Kill(entireProcessTree: true);
        Console.WriteLine($"authbridge helper stopped (pid {running.Pid})");
        return true;
    }

    /// <summary>Installs the latest release over this installation and restarts the helper, which then updates the
    /// client on each machine when its tunnel connects.</summary>
    private static async Task<int> UpdateAsync()
    {
        var latest = await GitHubReleases.LatestAsync();
        if (!AppVersion.IsNewer(latest, AppVersion.Release))
        {
            Console.WriteLine($"authbridge {AppVersion.Release} is up to date");
            return 0;
        }
        Console.WriteLine($"downloading authbridge {latest}");
        var package = await GitHubReleases.DownloadAsync(latest);
        await StopAsync();
        var installation = InstalledPackage.CurrentUser;
        installation.Install(package);
        Console.WriteLine($"updated {installation.Location} from {AppVersion.Release} to {latest}");
        if (await StartAsync(installation.Location) != 0)
            return 1;
        var info = await RemoteClient.WaitForChecksAsync(PingAsync, TimeSpan.FromSeconds(2));
        var status = await StatusAsync();
        if (info?.Tunnels.Any(tunnel => tunnel.State != "connected") == true)
            Console.WriteLine("Machines that are not connected get the new client when their tunnels connect.");
        return status;
    }

    /// <summary>Installs this package for the current user unless it runs from the installation, starts the helper now
    /// and at logon, and adds the installation to the user PATH.</summary>
    private static async Task<int> InstallAsync()
    {
        var installation = InstalledPackage.CurrentUser;
        if (await installation.InstallFromAsync(AppContext.BaseDirectory, StopIfRunningAsync))
            Console.WriteLine($"installed authbridge {AppVersion.Release} in {installation.Location}");
        RegisterForCurrentUser(installation);
        return await StartAsync(installation.Location);
    }

    private static void RegisterForCurrentUser(InstalledPackage installation)
    {
        var folder = installation.Location;
        var command = installation.LogonCommand;
        using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            run.SetValue("authbridge", command);
        Console.WriteLine($"The helper starts at logon: {command}");

        using var environment = Registry.CurrentUser.CreateSubKey("Environment");
        // Unexpanded, so that entries such as %USERPROFILE%\bin keep working.
        var path = environment.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var updatedPath = installation.IncludeInUserPath(path);
        if (updatedPath == path)
            Console.WriteLine($"{folder} is already on your PATH");
        else
        {
            var kind = path.Length > 0 ? environment.GetValueKind("Path") : RegistryValueKind.ExpandString;
            environment.SetValue("Path", updatedPath, kind);
            // Tells running programs such as Explorer, so that terminals started from now on see the new PATH.
            SendMessageTimeout(HwndBroadcast, WmSettingChange, 0, "Environment", SmtoAbortIfHung, 5000, out _);
            Console.WriteLine($"Added {folder} to your PATH; terminals started from now on can run 'authbridge'");
        }
    }

    private static async Task<int> InstallRemoteAsync(string name, string[] ssh)
    {
        var machine = await RemoteClient.ProbeAsync(ssh);
        await RemoteClient.UploadAsync(ssh, machine.Architecture);
        Console.WriteLine($"installed {machine.Home}/.local/bin/authbridge");
        var setup = await Ssh.RunAsync(ssh, "~/.local/bin/authbridge --setup-shell");
        Console.Write(setup.OutputOrThrow("remote shell setup failed"));

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

    private const nint HwndBroadcast = 0xFFFF;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeout(nint window, uint message, nint wParam, string lParam, uint flags, uint timeout, out nint result);
}
