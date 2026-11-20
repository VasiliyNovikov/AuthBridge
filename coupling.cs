#!/usr/bin/env dotnet
#:property PublishAot=false
#:package Microsoft.CodeAnalysis.AnalyzerUtilities@5.6.0
#:package Microsoft.CodeAnalysis.CSharp.Workspaces@5.12.0
#:package Microsoft.CodeAnalysis.Workspaces.MSBuild@5.12.0
using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeMetrics;
using Microsoft.CodeAnalysis.MSBuild;

Directory.SetCurrentDirectory((string)AppContext.GetData("EntryPointFileDirectoryPath")!);
var directory = Directory.CreateDirectory(Path.Combine("artifacts", "coupling"));
var output = Path.Combine(directory.FullName, "report.json");
File.Delete(output);

using var baseline = JsonDocument.Parse(File.ReadAllBytes("coupling-baseline.json"));
var ceiling = baseline.RootElement.GetProperty("maxClassCoupling").GetInt32();
if (ceiling < 0)
    throw new InvalidDataException("The class coupling ceiling must be nonnegative.");

var projectPath = Path.Combine("src", "AuthBridge", "AuthBridge.csproj");
using var restore = Process.Start(new ProcessStartInfo("dotnet", ["restore", projectPath]))!;
await restore.WaitForExitAsync();
if (restore.ExitCode != 0)
    return restore.ExitCode;

using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string> { ["Configuration"] = "Release" });
var project = await workspace.OpenProjectAsync(projectPath);
var compilation = await project.GetCompilationAsync() ?? throw new InvalidDataException("Cannot compile the AuthBridge project.");
var errors = workspace.Diagnostics.Where(d => d.Kind == WorkspaceDiagnosticKind.Failure).Select(d => d.Message)
    .Concat(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())).ToArray();
if (errors.Length != 0)
    throw new InvalidDataException(string.Join(Environment.NewLine, errors));

var metrics = await CodeAnalysisMetricData.ComputeAsync(new CodeMetricsAnalysisContext(compilation, CancellationToken.None));
var types = metrics.Children.SelectMany(ns => ns.Children)
    .Select(type => new
    {
        type = type.Symbol.ToDisplayString(),
        classCoupling = type.CoupledNamedTypes.Count,
        dependencies = type.CoupledNamedTypes.Select(t => t.ToDisplayString()).Order(StringComparer.Ordinal).ToArray(),
    })
    .OrderByDescending(row => row.classCoupling).ThenBy(row => row.type, StringComparer.Ordinal).ToArray();
if (types.Length == 0)
    throw new InvalidDataException("The AuthBridge project produced no type metrics.");

File.WriteAllBytes(output, JsonSerializer.SerializeToUtf8Bytes(types, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Class coupling (distinct referenced types):");
foreach (var row in types.Take(10))
    Console.WriteLine($"  {row.classCoupling,3}  {row.type}");
var maximum = types[0].classCoupling;
Console.WriteLine($"Maximum class coupling: {maximum} (baseline {ceiling}); {types.Length} types.");
if (maximum > ceiling)
{
    Console.Error.WriteLine("Class coupling exceeds the baseline. See artifacts/coupling/report.json for the dependencies.");
    return 1;
}
return 0;
