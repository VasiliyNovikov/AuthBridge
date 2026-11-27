#:property PublishAot=false
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;

/// <summary>
/// Shared by the file-based apps in the repository root, which include this file with <c>#:include eng/Repository.cs</c>.
/// They run on the JIT and are never published, so the directive above turns off Native AOT analysis for all of them.
/// </summary>
static class Repository
{
    /// <summary>The repository root, which contains the script being run.</summary>
    public static string Root { get; } = (string)AppContext.GetData("EntryPointFileDirectoryPath")!;

    /// <summary>The gate thresholds in code-metrics-baseline.props, by property name.</summary>
    public static Dictionary<string, XElement> LoadBaseline() =>
        XElement.Load(Path.Combine(Root, "code-metrics-baseline.props")).Elements("PropertyGroup").Elements()
            .ToDictionary(property => property.Name.LocalName, StringComparer.Ordinal);

    /// <summary>A path in artifacts/<paramref name="tool"/>, without the file that an earlier run left there.</summary>
    public static string Artifact(string tool, string file)
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(Root, "artifacts", tool)).FullName, file);
        File.Delete(path);
        return path;
    }

    /// <summary>Runs the dotnet whose runtime runs this script in the repository root, so that it uses the SDK from
    /// global.json, and returns its exit code.</summary>
    public static int Dotnet(params string[] arguments)
    {
        var dotnet = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        using var process = Process.Start(new ProcessStartInfo(dotnet, arguments) { WorkingDirectory = Root })!;
        process.WaitForExit();
        return process.ExitCode;
    }
}
