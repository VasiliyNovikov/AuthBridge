#!/usr/bin/env dotnet
#:include eng/Repository.cs
using System.Text.Json;

if (args is not [var reportPath])
{
    Console.Error.WriteLine("Usage: dotnet check-mutation-score.cs <mutation-report.json>");
    return 1;
}

var baseline = Repository.LoadBaseline();
var baselineDetected = (int)baseline["detectedMutants"];
var baselineValid = (int)baseline["validMutants"];
if (baselineDetected <= 0 || baselineDetected > baselineValid)
    throw new InvalidDataException("The mutation baseline must have 0 < detected <= valid.");

using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
// The leading slash makes a target such as Common/Http.cs match whole path segments only.
var files = report.RootElement.GetProperty("files").EnumerateObject().Select(file => (
    Path: "/" + file.Name.Replace('\\', '/'),
    Statuses: file.Value.GetProperty("mutants").EnumerateArray().Select(mutant => mutant.GetProperty("status").GetString()).ToArray())).ToArray();

// Each file that Stryker mutates needs at least one killed mutant, whatever the overall score.
using var config = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Repository.Root, "tests", "AuthBridge.Tests", "stryker-config.json")));
var passed = true;
foreach (var target in config.RootElement.GetProperty("stryker-config").GetProperty("mutate").EnumerateArray().Select(target => target.GetString()))
{
    var killed = files.Where(file => file.Path.EndsWith($"/{target}", StringComparison.Ordinal)).Sum(file => file.Statuses.Count(status => status == "Killed"));
    if (killed > 0)
        Console.WriteLine($"{target}: {killed} killed");
    else
    {
        Console.Error.WriteLine($"No killed mutants in {target}.");
        passed = false;
    }
}

int detected = 0, undetected = 0;
foreach (var status in files.SelectMany(file => file.Statuses))
{
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
    passed = false;
}
return passed ? 0 : 1;

static string Score(int detected, int valid) => FormattableString.Invariant($"{100m * detected / valid:F2}% ({detected}/{valid})");
