using System.CommandLine;

namespace AuthBridge.Host;

/// <summary>Routes the Windows CLI without doing any process, registry, or SSH work while parsing arguments.</summary>
internal sealed class HostCommandLine : RootCommand
{
    public HostCommandLine(Func<Task<int>> status, Func<string, string[], Task<int>> installRemote,
        Func<string, string[], Task<int>> remote) : base("Manage the AuthBridge Windows helper and its Linux clients.")
    {
        var statusCommand = Cli.Command("status", "Show the helper and tunnel status (the default command).", status);
        Action = statusCommand.Action;
        Add(statusCommand);
        Add(RemoteCommand("install-remote", "Install the Linux client and keep a tunnel to it.", "ssh-arguments",
            "SSH options followed by the destination.", installRemote));
        Add(RemoteCommand("remote", "Run the client on a configured Linux machine.", "arguments",
            "Arguments to the Linux client, for example --status.", remote));
        Cli.AddAgentHelp(this, """
              Run authbridge status first. Exit 0 means the helper answered; check that
              the named tunnel is connected. To check from that Linux machine, run:
                authbridge remote <name> --status
              If asked to connect a new machine, use the user's SSH options and destination:
                authbridge install-remote <name> [SSH options...] <destination>
              Full setup: https://github.com/VasiliyNovikov/AuthBridge#with-an-ai-agent
            """);
    }

    private static Command RemoteCommand(string verb, string description, string argumentName, string argumentDescription,
        Func<string, string[], Task<int>> run)
    {
        var name = new Argument<string>("name") { Description = "Name of the Linux machine." };
        var arguments = Cli.ForwardedArguments(argumentName, ArgumentArity.OneOrMore);
        arguments.Description = argumentDescription;
        var command = new Command(verb, description) { name, arguments };
        command.SetAction((result, _) => run(result.GetRequiredValue(name), result.GetRequiredValue(arguments)));
        return command;
    }
}
