#!/usr/bin/env dotnet
#:property PublishAot=false
#:package Microsoft.CodeAnalysis.AnalyzerUtilities@5.6.0
#:package Microsoft.CodeAnalysis.CSharp.Workspaces@5.12.0
#:package Microsoft.CodeAnalysis.Workspaces.MSBuild@5.12.0
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeMetrics;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

Directory.SetCurrentDirectory((string)AppContext.GetData("EntryPointFileDirectoryPath")!);
var directory = Directory.CreateDirectory(Path.Combine("artifacts", "coupling"));
var output = Path.Combine(directory.FullName, "report.json");
var observedPath = Path.Combine(directory.FullName, "observed.json");
File.Delete(output);
File.Delete(observedPath);

var baseline = XElement.Load("code-metrics-baseline.props").Elements("PropertyGroup").Elements()
    .ToDictionary(property => property.Name.LocalName, StringComparer.Ordinal);

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
var sourcePaths = project.Documents.Select(document => document.FilePath).OfType<string>().ToHashSet(StringComparer.Ordinal);
var types = metrics.Children.SelectMany(ns => ns.Children)
    .Select(type => new
    {
        type = type.Symbol.ToDisplayString(),
        classCoupling = type.CoupledNamedTypes.Count,
        depthOfInheritance = type.DepthOfInheritance ?? throw new InvalidDataException($"No inheritance metric for {type.Symbol}."),
        dependencies = type.CoupledNamedTypes.Select(t => t.ToDisplayString()).Order(StringComparer.Ordinal).ToArray(),
        // Property and event accessors are children of their member rather than of the type.
        methods = type.Children.Concat(type.Children.SelectMany(member => member.Children))
            .Where(member => member.Symbol is IMethodSymbol && member.Symbol.DeclaringSyntaxReferences.Any(IsMethodDeclaration))
            .Select(member => new
            {
                method = member.Symbol.ToDisplayString(),
                maintainabilityIndex = member.MaintainabilityIndex,
                cyclomaticComplexity = member.CyclomaticComplexity,
                sourceLines = member.SourceLines,
                executableLines = member.ExecutableLines,
            }).OrderBy(row => row.method, StringComparer.Ordinal).ToArray(),
    })
    .OrderByDescending(row => row.classCoupling).ThenBy(row => row.type, StringComparer.Ordinal).ToArray();
var methods = types.SelectMany(type => type.methods).ToArray();
if (types.Length == 0 || methods.Length == 0)
    throw new InvalidDataException("Expected both type and handwritten method metrics for AuthBridge.");

var json = new JsonSerializerOptions { WriteIndented = true };
File.WriteAllBytes(output, JsonSerializer.SerializeToUtf8Bytes(types, json));
Console.WriteLine("Class coupling (distinct referenced types):");
foreach (var row in types.Take(10))
    Console.WriteLine($"  {row.classCoupling,3}  {row.type}");

var observed = new Dictionary<string, long>();
var passed = true;
Console.WriteLine($"Roslyn baselines for {types.Length} types and {methods.Length} methods:");
Check("maxClassCoupling", types, t => (t.type, t.classCoupling));
Check("maxTypeDepthOfInheritance", types, t => (t.type, t.depthOfInheritance));
Check("minMethodMaintainabilityIndex", methods, m => (m.method, m.maintainabilityIndex), minimum: true);
Check("maxMethodCyclomaticComplexity", methods, m => (m.method, m.cyclomaticComplexity));
Check("maxMethodSourceLines", methods, m => (m.method, m.sourceLines));
Check("maxMethodExecutableLines", methods, m => (m.method, m.executableLines));
File.WriteAllBytes(observedPath, JsonSerializer.SerializeToUtf8Bytes(observed, json));
return passed ? 0 : 1;

bool IsMethodDeclaration(SyntaxReference declaration) =>
    sourcePaths.Contains(declaration.SyntaxTree.FilePath) &&
    compilation.Options.SyntaxTreeOptionsProvider?.IsGenerated(declaration.SyntaxTree, CancellationToken.None) != GeneratedKind.MarkedGenerated &&
    // Roslyn uses the whole type's source span for a primary constructor.
    declaration.GetSyntax() is not TypeDeclarationSyntax;

void Check<T>(string key, IEnumerable<T> rows, Func<T, (string Symbol, long Value)> measure, bool minimum = false)
{
    var limit = (long)baseline[key];
    if (limit < 0 || (minimum && limit > 100))
        throw new InvalidDataException($"Invalid baseline for {key}: {limit}.");
    var values = rows.Select(measure);
    var worst = minimum ? values.MinBy(row => row.Value) : values.MaxBy(row => row.Value);
    observed.Add(key, worst.Value);
    var message = $"{key}: {worst.Value} (baseline {limit}) - {worst.Symbol}";
    if (minimum ? worst.Value < limit : worst.Value > limit)
    {
        Console.Error.WriteLine($"FAIL {message}");
        passed = false;
    }
    else
        Console.WriteLine(message);
}
