using System.CommandLine;

namespace AuthBridge;

internal static class Cli
{
    public static Command Command(string name, string description, Func<Task<int>> run)
    {
        var command = new Command(name, description);
        command.SetAction((_, _) => run());
        return command;
    }

    public static Command Command(string name, string description, Func<int> run)
    {
        var command = new Command(name, description);
        command.SetAction(_ => run());
        return command;
    }

    public static Argument<string[]> ForwardedArguments(string name, ArgumentArity arity) =>
        new(name) { Arity = arity, CaptureRemainingTokens = true };

    public static Task<int> InvokeAsync(RootCommand root, string[] args)
    {
        // A forwarding command has single positional operands followed by an opaque argv. Protect even
        // a leading --help or literal --, which the parser would otherwise consume before capture starts.
        if (args is [var name, ..] && root.Subcommands.FirstOrDefault(c => c.Name == name) is
            { Arguments: [.., { CaptureRemainingTokens: true }] } command && args.Length > command.Arguments.Count)
            args = [name, "--", .. args.AsSpan(1)];

        var result = root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null, EnablePosixBundling = false });
        if (args.Length > 1 && result.GetResult(root.Options.OfType<VersionOption>().Single()) is not null)
            return Task.FromResult(Program.Fail("--version must be used on its own."));
        return result.InvokeAsync(new InvocationConfiguration
        {
            EnableDefaultExceptionHandler = false,
            ProcessTerminationTimeout = null, // The foreground helper handles Ctrl+C itself.
            Output = result.Errors.Count == 0 ? Console.Out : Console.Error,
        });
    }
}
