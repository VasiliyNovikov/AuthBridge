using System.Diagnostics;
using AuthBridge.Common;
using AuthBridge.Host;

namespace AuthBridge.Tests;

[TestClass]
public sealed class HostCommandTests
{
    [TestMethod]
    public async Task ExtraOrMissingArgumentsDoNotRunCommands()
    {
        var actions = new List<string>();
        Task<int> Record(string command)
        {
            actions.Add(command);
            return Task.FromResult(0);
        }
        var cli = new HostCommandLine(() => Record("status"), (_, _) => Record("install-remote"), (_, _) => Record("remote"))
        {
            Cli.Command("update", "", () => Record("update")),
        };

        Assert.AreEqual(0, await Cli.InvokeAsync(cli, []));
        Assert.AreEqual(1, await Cli.InvokeAsync(cli, ["update", "unexpected"]));
        Assert.AreEqual(1, await Cli.InvokeAsync(cli, ["install-remote", "dev"]));
        Assert.AreEqual(1, await Cli.InvokeAsync(cli, ["nonexistent"]));
        Assert.AreEqual(1, await Cli.InvokeAsync(cli, ["--version", "update"]));
        Assert.AreEqual(0, await Cli.InvokeAsync(cli, ["update"]));
        CollectionAssert.AreEqual(new[] { "status", "update" }, actions.ToArray());
    }

    [TestMethod]
    [DataRow("install-remote", "-p", 17)]
    [DataRow("remote", "--help", 23)]
    [DataRow("remote", "--", 23)]
    public async Task RemoteCommandsPreserveArgumentsAndReturnTheHandlersExitCode(string verb, string first, int expected)
    {
        var received = new List<string>();
        int Record(string target, string[] arguments, int exitCode)
        {
            received.Add(target);
            received.AddRange(arguments);
            return exitCode;
        }
        var cli = new HostCommandLine(() => Task.FromResult(0),
            (target, args) => Task.FromResult(Record(target, args, 17)), (target, args) => Task.FromResult(Record(target, args, 23)));
        string[] arguments = ["dev", first, "8422", "argument with spaces", "", "--version", "@response", "--"];
        Assert.AreEqual(expected, await Cli.InvokeAsync(cli, [verb, .. arguments]));
        CollectionAssert.AreEqual(arguments, received.ToArray());
    }

    [TestMethod]
    public async Task StartWaitsForAPingAndDoesNotLaunchTwice()
    {
        var ready = new PingResult(Protocol.Version, 42, 0, []);
        var pings = new Queue<PingResult?>([null, null, ready, ready]);
        using var output = new StringWriter();
        var control = new HelperControl(() => Task.FromResult(pings.Dequeue()), output, TextWriter.Null);
        var launches = 0;

        Assert.AreEqual(0, await control.StartAsync(() => launches++, "host.log", TimeSpan.Zero));
        StringAssert.Contains(output.ToString(), "helper started (pid 42)");
        Assert.AreEqual(0, await control.StartAsync(() => launches++, "host.log", TimeSpan.Zero));
        StringAssert.Contains(output.ToString(), "helper is running (pid 42)");
        Assert.AreEqual(1, launches);
        Assert.IsEmpty(pings);
    }

    [TestMethod]
    public async Task UnresponsiveHelperDoesNotProduceSuccessfulStartOrStatus()
    {
        using var error = new StringWriter();
        using var output = new StringWriter();
        var control = new HelperControl(() => Task.FromResult<PingResult?>(null), output, error);
        var launches = 0;
        Assert.AreEqual(1, await control.StartAsync(() => launches++, "test-host.log", TimeSpan.Zero));
        Assert.AreEqual(1, launches);
        Assert.AreEqual(1, await control.StatusAsync(1234));
        StringAssert.Contains(error.ToString(), "test-host.log");
        StringAssert.Contains(error.ToString(), "not running");
        Assert.AreEqual("", output.ToString());
    }

    [TestMethod]
    public void ForcedStopRejectsAnUnrelatedProcessAndGracefulStopWaitsForExit()
    {
        using var child = Process.Start(Processes.StartInfo(OperatingSystem.IsWindows() ? "sort.exe" : "cat", []))!;
        try
        {
            var refused = Assert.ThrowsExactly<IOException>(() => HelperControl.StopProcess(child.Id, _ => false, TimeSpan.Zero));
            StringAssert.Contains(refused.Message, "refusing");
            Assert.IsFalse(child.HasExited);
            HelperControl.StopProcess(child.Id, _ => { child.StandardInput.Close(); return true; }, TimeSpan.FromSeconds(5));
            Assert.IsTrue(child.HasExited);
        }
        finally
        {
            if (!child.HasExited)
                child.Kill();
        }
    }

    [TestMethod]
    public async Task StatusIncludesTheLastClientVersionAndOfflineReason()
    {
        TunnelStatus[] tunnels = [new("dev", "connected", "", DateTimeOffset.UtcNow, "1.2.3"),
            new("other", "disconnected", "network unavailable", DateTimeOffset.UtcNow)];
        var info = new PingResult(Protocol.Version, 42, 2, tunnels);
        using var output = new StringWriter();
        var control = new HelperControl(() => Task.FromResult<PingResult?>(info), output, TextWriter.Null);

        Assert.AreEqual(0, await control.StatusAsync(1234));
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        StringAssert.Contains(lines[0], "pid 42, 127.0.0.1:1234, 2 relay session(s)");
        StringAssert.StartsWith(lines[1], "  tunnel dev: connected");
        StringAssert.EndsWith(lines[1], ", client 1.2.3");
        StringAssert.StartsWith(lines[2], "  tunnel other: disconnected");
        StringAssert.EndsWith(lines[2], " - network unavailable");
    }
}
