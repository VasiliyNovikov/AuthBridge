#!/usr/bin/env dotnet
#:include eng/Repository.cs
using System.Globalization;
using System.Text.Json;

var baseline = Repository.LoadBaseline();
var threshold = (int)baseline["crapMethodThreshold"];
if (threshold <= 0)
    throw new InvalidDataException("The CRAP method threshold must be positive.");

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
// Exit 1 means that some methods score above the threshold, which the gate reports below.
if (analysis is not (0 or 1))
    return analysis;

using var measured = JsonDocument.Parse(File.ReadAllBytes(report));
var methods = measured.RootElement.GetProperty("methods").EnumerateArray().ToArray();
if (!methods.Any(method => method.GetProperty("coverage").GetDouble() > 0))
    throw new InvalidDataException("The report contains no covered AuthBridge methods; check the coverage collector.");
// No method may score above the threshold.
var crappy = methods.Where(method => method.GetProperty("crap").GetDouble() > threshold).ToArray();
foreach (var method in crappy)
{
    var (score, covered) = (method.GetProperty("crap").GetDouble(), method.GetProperty("coverage").GetDouble());
    Console.Error.WriteLine(FormattableString.Invariant(
        $"CRAP {score:F1}: {method.GetProperty("fullName").GetString()} (complexity {method.GetProperty("complexity").GetInt32()}, coverage {100 * covered:F0}%)"));
}
Console.WriteLine($"{crappy.Length} of {methods.Length} methods score above {threshold}.");
return crappy.Length == 0 ? 0 : 1;
