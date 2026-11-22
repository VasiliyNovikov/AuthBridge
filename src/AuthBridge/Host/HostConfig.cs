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

    public static HostConfig Load() => Load(FilePath);

    internal static HostConfig Load(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var config = JsonSerializer.Deserialize(stream, HostConfigJson.Default.HostConfig) ?? throw new JsonException("Expected a settings object.");
            var targets = config.Targets ?? [];
            if (targets.Any(InvalidTarget))
                throw new JsonException("Each target needs a name, a remote directory and SSH arguments.");
            if (config.BrowserCommand is [var browser, ..] && (string.IsNullOrWhiteSpace(browser) || config.BrowserCommand.Contains(null)))
                throw new JsonException("browserCommand must contain an executable followed by string arguments.");
            return config with { Targets = targets };
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Invalid AuthBridge configuration in '{path}': {e.Message}", e);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new([]);
        }
    }

    private static bool InvalidTarget(Target? target) => target is null || string.IsNullOrWhiteSpace(target.Name)
        || string.IsNullOrWhiteSpace(target.RemoteDir) || target.Ssh is not { Length: > 0 } || target.Ssh.Contains(null);

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

/// <summary>Publishes only complete, valid settings while the user is editing the file.</summary>
internal sealed class HostSettings(string path, Action<string> warn)
{
    private DateTime? _lastWrite;
    private string? _lastError;
    private volatile HostConfig? _current;

    public HostConfig Current => _current ?? throw new InvalidOperationException($"No valid configuration loaded from '{path}'.");

    public HostConfig? Reload()
    {
        try
        {
            var write = File.GetLastWriteTimeUtc(path);
            if (write == _lastWrite && _lastError is null)
                return null;
            var config = HostConfig.Load(path);
            _current = config;
            _lastWrite = write;
            _lastError = null;
            return config;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            if (_lastError != e.Message)
                warn($"cannot reload '{path}': {e.Message}");
            _lastError = e.Message;
            return null;
        }
    }
}
