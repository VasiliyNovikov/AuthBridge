using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;

namespace AuthBridge.Client;

internal sealed class ClientCommandLine : RootCommand
{
    public ClientCommandLine(Func<string[], bool, Task<int>> open, Func<string, string[], Task<int>> gcm,
        Func<string[], Task<int>> nugetPlugin, Func<string, string, int, Task<int>> relay,
        Func<int, string, string, Task<int>> cancel, params Command[] modes)
        : base("Supply one or more URLs to open in the Windows browser, or choose a command.")
    {
        var urls = new Argument<string[]>("url") { Arity = ArgumentArity.ZeroOrMore, Description = "HTTP or HTTPS URLs to open." };
        Validators.Add(result =>
        {
            if (result.Tokens.Count == 0)
                result.AddError("Specify at least one URL to open, or choose a command.");
            else if (result.Tokens.FirstOrDefault(token => token.Value.StartsWith('-')) is { } option)
                result.AddError($"Unrecognized option '{option.Value}'.");
        });
        var dryRun = new Option<bool>("--dry-run") { Arity = ArgumentArity.Zero, Description = "Set up the relay without opening a browser." };
        Add(urls);
        Add(dryRun);
        SetAction((result, _) => open(result.GetRequiredValue(urls), result.GetValue(dryRun)));

        foreach (var mode in modes)
            Add(mode);
        AddWrappers(gcm, nugetPlugin);
        AddWorkers(relay, cancel);

        foreach (var command in Subcommands)
            command.Validators.Add(result =>
            {
                if (result.Parent is CommandResult parent && parent.Children.Any(child => child != result))
                    result.AddError($"{command.Name} cannot be combined with browser arguments.");
            });
    }

    private void AddWrappers(Func<string, string[], Task<int>> gcm, Func<string[], Task<int>> nugetPlugin)
    {
        var program = new Argument<string>("program");
        var arguments = Cli.ForwardedArguments("arguments", ArgumentArity.ZeroOrMore);
        var git = new Command("--gcm") { Hidden = true, Arguments = { program, arguments } };
        git.SetAction((result, _) => gcm(result.GetRequiredValue(program), result.GetValue(arguments) ?? []));
        Add(git);
        var nuget = new Command("--nuget-plugin") { Hidden = true, Arguments = { arguments } };
        nuget.SetAction((result, _) => nugetPlugin(result.GetValue(arguments) ?? []));
        Add(nuget);
    }

    private void AddWorkers(Func<string, string, int, Task<int>> relay, Func<int, string, string, Task<int>> cancel)
    {
        var port = new Argument<int>("port")
        {
            CustomParser = result =>
            {
                if (int.TryParse(result.Tokens.Single().Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                    && value is >= 1 and <= 65535)
                    return value;
                result.AddError("The relay port must be an integer from 1 to 65535.");
                return 0;
            },
        };
        var socket = new Argument<string>("socket");
        var session = new Argument<string>("session");
        var relayCommand = new Command(BrowserClient.RelayCommand) { Hidden = true, Arguments = { socket, session, port } };
        relayCommand.SetAction((result, _) => relay(result.GetRequiredValue(socket), result.GetRequiredValue(session), result.GetRequiredValue(port)));
        Add(relayCommand);
        var url = new Argument<string>("url");
        var reason = new Argument<string>("reason");
        var cancelCommand = new Command(BrowserClient.CancelCommand) { Hidden = true, Arguments = { port, url, reason } };
        cancelCommand.SetAction((result, _) => cancel(result.GetRequiredValue(port), result.GetRequiredValue(url), result.GetRequiredValue(reason)));
        Add(cancelCommand);
    }
}
