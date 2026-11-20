using System.Text.Json;
using System.Text.Json.Serialization;
using AuthBridge.Common;

namespace AuthBridge.Host;

/// <summary>A remote machine: its name, the ssh arguments that reach it, and its ~/.authbridge path.</summary>
internal sealed record Target(string Name, string[] Ssh, string RemoteDir);

/// <summary>The helper's settings in %USERPROFILE%\.authbridge\config.json.</summary>
internal sealed record HostConfig(Target[] Targets, string[]? BrowserCommand = null)
{
    public static string FilePath => Path.Combine(Paths.StateDir, "config.json");

    public static HostConfig Load()
    {
        try
        {
            var config = JsonSerializer.Deserialize(File.ReadAllBytes(FilePath), HostConfigJson.Default.HostConfig);
            return config is null ? new([]) : config with { Targets = config.Targets ?? [] };
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new([]);
        }
    }

    public void Save()
    {
        Paths.EnsureStateDir();
        File.WriteAllBytes(FilePath + ".tmp", JsonSerializer.SerializeToUtf8Bytes(this, HostConfigJson.Default.HostConfig));
        File.Move(FilePath + ".tmp", FilePath, overwrite: true);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(HostConfig))]
internal sealed partial class HostConfigJson : JsonSerializerContext;
