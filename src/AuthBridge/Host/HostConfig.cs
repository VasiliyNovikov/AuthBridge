using System.Text.Json;
using System.Text.Json.Serialization;
using AuthBridge.Common;

namespace AuthBridge.Host;

/// <summary>A remote machine: its name, the ssh arguments that reach it, and its ~/.authbridge path.</summary>
internal sealed record Target(string Name, string[] Ssh, string RemoteDir);

/// <summary>The helper's settings in %USERPROFILE%\.authbridge\config.json.</summary>
internal sealed record HostConfig(Target[] Targets, string[]? BrowserCommand = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool UpdateAutomatically = false)
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

    public static HostConfig Edit(string path, Func<HostConfig, HostConfig> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // The tray and install-remote are separate writers. Read the latest settings while holding the writer lock.
        using var writer = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var config = change(Load(path));
        var temp = path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(config, HostConfigJson.Default.HostConfig));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
        return config;
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
    private readonly Lock _gate = new();

    public HostConfig Current => _current ?? throw new InvalidOperationException($"No valid configuration loaded from '{path}'.");
    public bool UpdateAutomatically => _current?.UpdateAutomatically == true;

    public void SetUpdateAutomatically(bool enabled)
    {
        lock (_gate)
        {
            _current = HostConfig.Edit(path, config => config with { UpdateAutomatically = enabled });
            _lastWrite = null; // The maintenance loop must still apply any tunnel edits picked up from disk.
        }
    }

    public HostConfig? Reload()
    {
        lock (_gate)
            return ReloadIfChanged();
    }

    private HostConfig? ReloadIfChanged()
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
