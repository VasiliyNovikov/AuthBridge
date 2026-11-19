#!/usr/bin/env dotnet
#:property PublishAot=false
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

Directory.SetCurrentDirectory((string)AppContext.GetData("EntryPointFileDirectoryPath")!);
using var baseline = JsonDocument.Parse(File.ReadAllBytes("crap-baseline.json"));
var threshold = baseline.RootElement.GetProperty("methodThreshold").GetInt32();
var ceiling = baseline.RootElement.GetProperty("maxTotalCrapLoad").GetDecimal();
if (threshold <= 0 || ceiling < 0)
    throw new InvalidDataException("The CRAP method threshold must be positive and the load ceiling nonnegative.");

var output = Directory.CreateDirectory(Path.Combine("artifacts", "crap")).FullName;
var coverage = Path.Combine(output, "coverage.cobertura.xml");
var report = Path.Combine(output, "report.json");
File.Delete(coverage);
File.Delete(report);

var restore = Dotnet("tool", "restore");
if (restore != 0)
    return restore;

// Coverlet names its files with timestamps. Never pick up coverage left over from an earlier run.
var results = Directory.CreateTempSubdirectory("authbridge-crap-");
try
{
    var test = Dotnet("test", "--project", Path.Combine("tests", "AuthBridge.Tests", "AuthBridge.Tests.csproj"),
        "-c", "Release", "--results-directory", results.FullName, "--coverlet", "--coverlet-include", "[authbridge]*",
        "--coverlet-output-format", "cobertura");
    if (test != 0)
        return test;
    if (results.GetFiles("coverage.cobertura*.xml", SearchOption.AllDirectories) is not [var file])
        throw new InvalidDataException("Expected exactly one fresh Cobertura report from AuthBridge.Tests.");
    file.CopyTo(coverage);
}
finally
{
    results.Delete(recursive: true);
}

var analysis = Dotnet("tool", "run", "dotnet-crap", "--", "analyze", Path.Combine("src", "AuthBridge", "AuthBridge.csproj"),
    "--coverage", coverage, "--threshold", threshold.ToString(CultureInfo.InvariantCulture), "--output", report);
// Exit 1 means there are hotspots, not that analysis failed. The project's gate is their combined load.
if (analysis is not (0 or 1))
    return analysis;

using var measured = JsonDocument.Parse(File.ReadAllBytes(report));
var root = measured.RootElement;
if (!root.GetProperty("methods").EnumerateArray().Any(method => method.GetProperty("coverage").GetDouble() > 0))
    throw new InvalidDataException("The report contains no covered AuthBridge methods; check the coverage collector.");
var stats = root.GetProperty("stats");
var load = Math.Round(stats.GetProperty("totalCrapLoad").GetDecimal(), 3, MidpointRounding.AwayFromZero);
Console.WriteLine(FormattableString.Invariant(
    $"Total CRAP load: {load:F3} (baseline {ceiling:F3}); {stats.GetProperty("crappyMethodCount")} of {stats.GetProperty("methodCount")} methods above {threshold}."));
if (load > ceiling)
{
    Console.Error.WriteLine("CRAP load exceeds the baseline. See artifacts/crap/report.json for individual methods.");
    return 1;
}
return 0;

static int Dotnet(params string[] arguments)
{
    using var process = Process.Start(new ProcessStartInfo("dotnet", arguments))!;
    process.WaitForExit();
    return process.ExitCode;
}
