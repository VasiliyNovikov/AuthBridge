#!/usr/bin/env dotnet
#:include eng/Repository.cs
#:package Microsoft.CodeAnalysis.CSharp
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var baseline = Repository.LoadBaseline();
var minTokens = (int)baseline["minCloneTokens"];
if (minTokens <= 0)
    throw new InvalidDataException("The clone size threshold must be positive.");
var output = Repository.Artifact("duplication", "report.json");

// All C# in the repository as the Windows build sees it, which leaves out only the Linux branch of Program.Main.
string[] ignoredDirectories = ["bin", "obj", "artifacts", "StrykerOutput"];
var parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview).WithPreprocessorSymbols("WINDOWS");
var files = Directory.EnumerateFiles(Repository.Root, "*.cs", SearchOption.AllDirectories)
    .Select(path => Path.GetRelativePath(Repository.Root, path).Replace('\\', '/'))
    .Where(path => !path.Split('/').Any(part => part.StartsWith('.') || ignoredDirectories.Contains(part)))
    .Order(StringComparer.Ordinal).ToArray();

// Copies are equal token sequences, so whitespace, comments and line breaks cannot hide one. Using directives are left
// out: the same imports are not duplicated logic.
List<int> ids = [];
List<(int File, int StartLine, int EndLine)> places = [];
var fileStarts = new int[files.Length + 1];
var texts = new Dictionary<string, int>(StringComparer.Ordinal);
foreach (var (index, file) in files.Index())
{
    fileStarts[index] = ids.Count;
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(Repository.Root, file)), parse, file);
    foreach (var token in tree.GetRoot().DescendantTokens())
    {
        if (token.IsKind(SyntaxKind.EndOfFileToken) || token.Parent?.FirstAncestorOrSelf<UsingDirectiveSyntax>() is not null)
            continue;
        if (!texts.TryGetValue(token.Text, out var id))
            texts.Add(token.Text, id = texts.Count);
        var lines = token.GetLocation().GetLineSpan();
        ids.Add(id);
        places.Add((index, lines.StartLinePosition.Line + 1, lines.EndLinePosition.Line + 1));
    }
}
fileStarts[^1] = ids.Count;
if (ids.Count == 0)
    throw new InvalidDataException("No C# tokens found in the repository.");

int FileStart(int position) => fileStarts[places[position].File];
int FileEnd(int position) => fileStarts[places[position].File + 1];

// A block of minCloneTokens tokens may not appear twice. Two equal windows of that length start a pair of copies,
// unless the tokens before both copies match as well: then the same pair starts one token earlier.
var windows = new Dictionary<int, List<int>>(new Window(ids, minTokens));
for (var file = 0; file < files.Length; file++)
{
    for (var start = fileStarts[file]; start + minTokens <= fileStarts[file + 1]; start++)
    {
        if (windows.TryGetValue(start, out var starts))
            starts.Add(start);
        else
            windows.Add(start, [start]);
    }
}

var covered = new bool[ids.Count];
List<(int Length, SortedSet<int> Starts)> clones = [];
var cloneOf = new Dictionary<(int Length, int Start), int>();
foreach (var starts in windows.Values.Where(starts => starts.Count > 1))
{
    for (var i = 0; i < starts.Count; i++)
    {
        for (var j = i + 1; j < starts.Count; j++)
        {
            var (a, b) = (starts[i], starts[j]);
            var sameFile = places[a].File == places[b].File;
            // Overlapping matches in one file are one stretch of repetitive code, not two copies.
            if ((sameFile && a + minTokens > b) || (a > FileStart(a) && b > FileStart(b) && ids[a - 1] == ids[b - 1]))
                continue;
            var length = minTokens;
            while (a + length < FileEnd(a) && b + length < FileEnd(b) && ids[a + length] == ids[b + length] && (!sameFile || a + length < b))
                length++;
            Array.Fill(covered, true, a, length);
            Array.Fill(covered, true, b, length);
            // A third copy matches both earlier ones; all copies of the same length form one clone.
            if (!cloneOf.TryGetValue((length, a), out var clone) && !cloneOf.TryGetValue((length, b), out clone))
            {
                clone = clones.Count;
                clones.Add((length, []));
            }
            clones[clone].Starts.UnionWith([a, b]);
            cloneOf[(length, a)] = cloneOf[(length, b)] = clone;
        }
    }
}

var report = clones
    .Select(clone => new
    {
        tokens = clone.Length,
        copies = clone.Starts.Select(start => new
        {
            file = files[places[start].File],
            startLine = places[start].StartLine,
            endLine = places[start + clone.Length - 1].EndLine,
        }).ToArray(),
    })
    .OrderByDescending(clone => clone.tokens * (clone.copies.Length - 1))
    .ThenBy(clone => clone.copies[0].file, StringComparer.Ordinal).ThenBy(clone => clone.copies[0].startLine)
    .ToArray();
File.WriteAllBytes(output, JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true }));
foreach (var clone in report)
    Console.WriteLine($"{clone.tokens} tokens in {clone.copies.Length} places: {string.Join(", ", clone.copies.Select(copy => $"{copy.file}:{copy.startLine}-{copy.endLine}"))}");

var duplicated = covered.Count(token => token);
Console.WriteLine($"Duplicated tokens: {duplicated} in {report.Length} clones of at least {minTokens} tokens; {ids.Count} tokens in {files.Length} files.");
if (report.Length > 0)
{
    Console.Error.WriteLine("No code may be duplicated. See artifacts/duplication/report.json for every copy.");
    return 1;
}
return 0;

/// <summary>Compares the token sequences of one length that start at two positions.</summary>
sealed class Window(List<int> ids, int length) : IEqualityComparer<int>
{
    public bool Equals(int first, int second)
    {
        for (var k = 0; k < length; k++)
        {
            if (ids[first + k] != ids[second + k])
                return false;
        }
        return true;
    }

    public int GetHashCode(int start)
    {
        var hash = new HashCode();
        for (var k = 0; k < length; k++)
            hash.Add(ids[start + k]);
        return hash.ToHashCode();
    }
}
