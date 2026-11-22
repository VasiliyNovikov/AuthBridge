using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AuthBridge.Client;

[SupportedOSPlatform("linux")]
internal static unsafe partial class Posix
{
    /// <summary>The home directory from the user database, regardless of $HOME.</summary>
    public static string AccountHome()
    {
        var entry = getpwuid(getuid());
        return entry is not null && entry->Dir is not null
            ? Marshal.PtrToStringUTF8((nint)entry->Dir)!
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>Leaves the caller's session, so signals for its terminal or process group do not stop background work.</summary>
    public static void Detach() => setsid();

    /// <summary>Replaces the current process with a program found on PATH, like exec in a shell.</summary>
    public static void Exec(string[] command, IReadOnlyDictionary<string, string> environmentOverrides)
    {
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value as string ?? "");
        foreach (var (name, value) in environmentOverrides)
            environment[name] = value;
        var allocated = new List<nint>();
        try
        {
            var argv = ToNative(command, allocated);
            var envp = ToNative([.. environment.Select(entry => $"{entry.Key}={entry.Value}")], allocated);
            fixed (nint* args = argv, env = envp)
                execvpe((byte*)argv[0], (byte**)args, (byte**)env);
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"cannot run {command[0]}");
        }
        finally
        {
            allocated.ForEach(Marshal.FreeCoTaskMem);
        }
    }

    /// <summary>Parent process names for diagnostics, without arguments that can carry sign-in secrets.</summary>
    public static string ParentChain(int depth = 3)
    {
        var chain = new List<string>();
        var pid = Environment.ProcessId;
        try
        {
            while (chain.Count < depth)
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                pid = int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1]);
                if (pid <= 1)
                    break;
                chain.Add(File.ReadAllText($"/proc/{pid}/comm").TrimEnd('\n'));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            // A parent may have exited or its proc entry may be restricted.
        }
        return string.Join(" <- ", chain);
    }

    /// <summary>The path of an executable found on PATH.</summary>
    public static string? Which(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
        .Select(directory => Path.Combine(directory, name))
        .FirstOrDefault(path => File.Exists(path) && access(path, 1) == 0); // X_OK for the invoking user

    /// <summary>NULL-terminated array of UTF-8 strings.</summary>
    private static nint[] ToNative(string[] values, List<nint> allocated)
    {
        var pointers = new nint[values.Length + 1];
        for (var i = 0; i < values.Length; i++)
            allocated.Add(pointers[i] = Marshal.StringToCoTaskMemUTF8(values[i]));
        return pointers;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Passwd
    {
        public byte* Name;
        public byte* Password;
        public uint Uid;
        public uint Gid;
        public byte* Gecos;
        public byte* Dir;
        public byte* Shell;
    }

    [LibraryImport("libc")]
    private static partial uint getuid();

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int access(string path, int mode);

    [LibraryImport("libc")]
    private static partial Passwd* getpwuid(uint uid);

    [LibraryImport("libc")]
    private static partial int setsid();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int execvpe(byte* file, byte** argv, byte** envp);
}
