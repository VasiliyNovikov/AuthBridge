#!/usr/bin/env dotnet
using System.Text.Json;
using System.Xml.Linq;

if (args is not [var reportPath])
{
    Console.Error.WriteLine("Usage: dotnet check-mutation-score.cs <mutation-report.json>");
    return 1;
}

var root = (string)AppContext.GetData("EntryPointFileDirectoryPath")!;
var baseline = XElement.Load(Path.Combine(root, "code-metrics-baseline.props")).Elements("PropertyGroup").Elements()
    .ToDictionary(property => property.Name.LocalName, StringComparer.Ordinal);
var baselineDetected = (int)baseline["detectedMutants"];
var baselineValid = (int)baseline["validMutants"];
if (baselineDetected <= 0 || baselineDetected > baselineValid)
    throw new InvalidDataException("The mutation baseline must have 0 < detected <= valid.");

using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
var mutants = report.RootElement.GetProperty("files").EnumerateObject()
    .SelectMany(file => file.Value.GetProperty("mutants").EnumerateArray());
int detected = 0, undetected = 0;
foreach (var mutant in mutants)
{
    var status = mutant.GetProperty("status").GetString();
    switch (status)
    {
        case "Killed" or "Timeout": detected++; break;
        case "Survived" or "NoCoverage": undetected++; break;
        case "Ignored" or "CompileError" or "RuntimeError": break;
        default: throw new InvalidDataException($"Unexpected status in the final mutation report: '{status}'.");
    }
}

var valid = detected + undetected;
if (valid == 0)
    throw new InvalidDataException("The mutation report contains no valid mutants.");

Console.WriteLine($"Overall mutation score: {Score(detected, valid)}; baseline {Score(baselineDetected, baselineValid)}.");
if ((long)detected * baselineValid < (long)baselineDetected * valid)
{
    Console.Error.WriteLine("Overall mutation score fell below the baseline.");
    return 1;
}
return 0;

static string Score(int detected, int valid) => FormattableString.Invariant($"{100m * detected / valid:F2}% ({detected}/{valid})");
