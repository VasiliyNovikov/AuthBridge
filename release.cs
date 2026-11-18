#!/usr/bin/env dotnet
// Publishes self-contained, single-file binaries and release packages to ./artifacts.
// Usage: dotnet release.cs [version]
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

var version = args is [var value] ? value : "0.0.0-dev";
var root = (string)AppContext.GetData("EntryPointFileDirectoryPath")!;
// The dotnet whose runtime runs this script; started in the repository, it uses the SDK from global.json.
var dotnet = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
var artifacts = Path.Combine(root, "artifacts");
var staging = Path.Combine(artifacts, "staging");
if (Directory.Exists(artifacts))
    Directory.Delete(artifacts, recursive: true);

// Linux clients, which the Windows packages also carry for 'authbridge install-remote'.
List<string> clients = [];
foreach (var rid in new[] { "linux-x64", "linux-arm64" })
{
    clients.Add(Path.Combine(artifacts, $"authbridge-{rid}"));
    File.Copy(Path.Combine(Publish(rid), "authbridge"), clients[^1]);
}

foreach (var rid in new[] { "win-x64", "win-arm64" })
{
    var output = Publish(rid);
    // authbridgew.exe is the same program marked as a GUI app (PE subsystem 2), so the helper runs without a console window.
    var exe = File.ReadAllBytes(Path.Combine(output, "authbridge.exe"));
    exe[BitConverter.ToInt32(exe, 0x3C) + 0x5C] = 2;
    File.WriteAllBytes(Path.Combine(output, "authbridgew.exe"), exe);
    foreach (var file in clients.Concat([Path.Combine(root, "README.md"), Path.Combine(root, "LICENSE")]))
        File.Copy(file, Path.Combine(output, Path.GetFileName(file)));
    ZipFile.CreateFromDirectory(output, Path.Combine(artifacts, $"authbridge-{rid}.zip"));
}
Directory.Delete(staging, recursive: true);

string Publish(string rid)
{
    var output = Path.Combine(staging, rid);
    string[] arguments = ["publish", "src/AuthBridge", "-c", "Release", "-r", rid, "-o", output, $"-p:Version={version}"];
    using var publish = Process.Start(new ProcessStartInfo(dotnet, arguments) { WorkingDirectory = root })!;
    publish.WaitForExit();
    if (publish.ExitCode != 0)
        throw new InvalidOperationException($"Publishing {rid} failed.");
    return output;
}
