#!/usr/bin/env dotnet
#:include eng/Repository.cs
using System.Globalization;
using System.Text.Json;

var baseline = Repository.LoadBaseline();
var threshold = (int)baseline["crapMethodThreshold"];
var ceiling = (decimal)baseline["maxTotalCrapLoad"];
if (threshold <= 0 || ceiling < 0)
    throw new InvalidDataException("The CRAP method threshold must be positive and the load ceiling nonnegative.");

var coverage = Repository.Artifact("crap", "coverage.cobertura.xml");
var report = Repository.Artifact("crap", "report.json");

var restore = Repository.Dotnet("tool", "restore");
if (restore != 0)
    return restore;

// Coverlet names its files with timestamps. Never pick up coverage left over from an earlier run.
var results = Directory.CreateTempSubdirectory("authbridge-crap-");
try
{
    var test = Repository.Dotnet("test", "--project", Path.Combine("tests", "AuthBridge.Tests", "AuthBridge.Tests.csproj"),
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

var analysis = Repository.Dotnet("tool", "run", "dotnet-crap", "--", "analyze", Path.Combine("src", "AuthBridge", "AuthBridge.csproj"),
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
