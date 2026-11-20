using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AuthBridge.Common;

namespace AuthBridge.Host;

/// <summary>A notification area icon that shows whether the tunnels are connected.</summary>
/// <remarks>Closing its window, from its Exit command or through <see cref="RequestExit"/>, stops the helper.</remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class TrayIcon : IDisposable
{
    private const string WindowClassName = "AuthBridgeTray";
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;

    // Window procedures are static, and the helper shows a single icon.
    private static TrayIcon? s_current;

    private readonly Func<TunnelStatus[]> _tunnels;
    private readonly TrayMenu _menu;
    private readonly Action _exit;
    private readonly TrayNotification _notification;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _created = new();
    private readonly TrayWindowProcedure _procedure;
    private nint _window;
    private nint _icon;
    private uint _color;
    private Process? _updater;
    private bool _canThemeMenus = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362);

    private TrayIcon(Func<TunnelStatus[]> tunnels, Func<bool> updateAvailable, Action exit)
    {
        _tunnels = tunnels;
        _exit = exit;
        _menu = new TrayMenu(OpenLog, () => _updater is null && updateAvailable(), StartUpdate,
            () => AboutDialog.Show(_window, GetModuleHandleW(null)), () => PostMessageW(_window, TrayWindowProcedure.CloseMessage, 0, 0));
        _notification = new TrayNotification(tip => Notify(NimModify, tip), name => Notify(NimAdd, name));
        _procedure = new TrayWindowProcedure(RegisterWindowMessageW("TaskbarCreated"), Refresh, ShowMenu, OnClose,
            () => PostQuitMessage(0), _notification.Invalidate);
        _thread = new Thread(Run) { IsBackground = true, Name = "Tray icon" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Shows the icon until disposed; <paramref name="exit"/> runs on the thread pool once its window closes.</summary>
    public static TrayIcon Start(Func<TunnelStatus[]> tunnels, Func<bool> updateAvailable, Action exit)
    {
        var tray = s_current = new TrayIcon(tunnels, updateAvailable, exit);
        tray._thread.Start();
        tray._created.Wait();
        return tray;
    }

    /// <summary>Asks a helper process to exit by closing its icon's window; false if it has none.</summary>
    public static bool RequestExit(int processId)
    {
        var requested = false;
        for (nint window = 0; (window = FindWindowExW(0, window, WindowClassName, null)) != 0;)
        {
            if (GetWindowThreadProcessId(window, out var owner) != 0 && owner == processId)
                requested |= PostMessageW(window, TrayWindowProcedure.CloseMessage, 0, 0) != 0;
        }
        return requested;
    }

    public void Dispose()
    {
        PostMessageW(_window, TrayWindowProcedure.CloseMessage, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        try
        {
            // Per-monitor DPI awareness keeps the menu sharp on high-DPI screens.
            SetThreadDpiAwarenessContext(-4);
            fixed (char* name = WindowClassName)
            {
                var windowClass = new WindowClass { Size = sizeof(WindowClass), WindowProc = &WindowProc, Instance = GetModuleHandleW(null), ClassName = name };
                RegisterClassExW(&windowClass);
                // Hidden rather than message-only, because message-only windows miss the TaskbarCreated broadcast.
                _window = CreateWindowExW(0, name, name, 0, 0, 0, 0, 0, 0, 0, windowClass.Instance, 0);
            }
            _created.Set();
            Refresh();
            SetTimer(_window, 1, 2000, 0);
            nint* message = stackalloc nint[8]; // room for a MSG
            while (GetMessageW(message, 0, 0, 0) > 0)
                DispatchMessageW(message);
            DestroyIcon(_icon);
        }
        catch (Exception e)
        {
            Log.Error(e, "tray icon failed");
        }
        finally
        {
            _updater?.Dispose();
            _created.Set();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (s_current!._procedure.Handle(message, wParam, lParam))
                return 0;
        }
        catch (Exception e)
        {
            Log.Error(e, "tray icon failed");
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    private void OnClose()
    {
        Notify(NimDelete);
        // Shutdown waits for the window thread.
        ThreadPool.QueueUserWorkItem(_ => _exit());
    }

    private void Refresh()
    {
        ReapUpdater();
        var status = TrayNotification.Describe(_tunnels());
        if (status.Color != _color)
        {
            var icon = CreateStatusIcon(status.Color);
            if (icon == 0)
                throw new InvalidOperationException("could not create the tray status icon");
            DestroyIcon(_icon);
            _icon = icon;
            _color = status.Color;
        }
        _notification.Publish(status);
    }

    private bool Notify(uint operation, string tip = "")
    {
        var data = new NotifyIconData
        {
            Size = sizeof(NotifyIconData),
            Window = _window,
            Id = 1,
            Flags = 0x01 | 0x02 | 0x04 | 0x80, // NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP
            CallbackMessage = TrayWindowProcedure.CallbackMessage,
            Icon = _icon,
            Version = 4, // NOTIFYICON_VERSION_4
        };
        tip.CopyTo(new Span<char>(data.Tip, 127));
        return Shell_NotifyIconW(operation, &data) != 0 && (operation != NimAdd || Shell_NotifyIconW(NimSetVersion, &data) != 0);
    }

    private void ShowMenu(int x, int y)
    {
        var popup = GetLastActivePopup(_window);
        SetForegroundWindow(popup); // Raise the open dialog, or let a click elsewhere dismiss the menu.
        if (popup != _window)
            return;
        ApplyMenuTheme();
        _menu.Show(labels => TrackMenu(labels, x, y));
    }

    private int TrackMenu(IReadOnlyList<string> labels, int x, int y)
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            for (var i = 0; i < labels.Count; i++)
            {
                if (AppendMenuW(menu, 0, (nuint)(i + 1), labels[i]) == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            return TrackPopupMenuEx(menu, 0x0100 | 0x0002, x, y, _window, 0); // TPM_RETURNCMD | TPM_RIGHTBUTTON
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void ApplyMenuTheme()
    {
        if (!_canThemeMenus)
            return;
        try
        {
            // Since Windows 10 1903, UXTheme ordinal 135 accepts AllowDark (1): Windows still chooses the palette.
            RefreshImmersiveColorPolicyState();
            SetPreferredAppMode(1);
            FlushMenuThemes();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _canThemeMenus = false;
            Log.Warn($"Windows menu theming is unavailable: {e.Message}");
        }
    }

    private static void OpenLog()
    {
        if (Log.FilePath is { } log)
            Process.Start(new ProcessStartInfo(log) { UseShellExecute = true })?.Dispose();
    }

    private void StartUpdate()
    {
        var start = new InstalledPackage(AppContext.BaseDirectory).TrayUpdateStartInfo();
        _updater = Process.Start(start) ?? throw new InvalidOperationException("could not start authbridge update");
    }

    private void ReapUpdater()
    {
        if (_updater is not { HasExited: true } finished)
            return;
        if (finished.ExitCode == TrayUpdate.LogUnavailable)
            Log.Warn($"tray update could not write {TrayUpdate.LogFileName}");
        else if (finished.ExitCode != 0)
            Log.Warn($"authbridge update exited with code {finished.ExitCode}; check {TrayUpdate.LogFileName}");
        finished.Dispose();
        _updater = null;
    }

    /// <summary>The application icon with a dot in the given color at its bottom right.</summary>
    private static nint CreateStatusIcon(uint color)
    {
        var image = AppIconImage(GetSystemMetrics(49)); // SM_CXSMICON
        var size = BitConverter.ToInt32(image, 4);
        var pixels = MemoryMarshal.Cast<byte, uint>(image.AsSpan(40, size * size * 4));
        float radius = size * 0.19f, center = size - radius, gap = Math.Max(1, size / 16f);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // Rows are bottom-up. The icon is cut away around the dot, which keeps it visible on any background.
                var distance = MathF.Sqrt(((x + 0.5f - center) * (x + 0.5f - center)) + ((y + 0.5f - radius) * (y + 0.5f - radius)));
                var dot = Math.Clamp(radius + 0.5f - distance, 0, 1);
                var keep = Math.Clamp(distance - radius - gap + 0.5f, 0, 1);
                ref var pixel = ref pixels[(y * size) + x];
                pixel = dot > 0 ? ((uint)(dot * 255) << 24) | color : ((uint)((pixel >> 24) * keep) << 24) | (pixel & 0xFFFFFF);
            }
        }
        fixed (byte* resource = image)
            return CreateIconFromResourceEx(resource, (uint)image.Length, 1, 0x00030000, size, size, 0);
    }

    /// <summary>The image in AuthBridge.ico closest to and at least the given size, as an icon resource: a bitmap
    /// header, 32-bit pixels and a mask.</summary>
    private static byte[] AppIconImage(int size)
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("AuthBridge.ico")!;
        var ico = new byte[stream.Length];
        stream.ReadExactly(ico);
        // A 6-byte header with the image count at 4, then 16-byte entries with the width at 0 (0 for the 256-pixel PNG),
        // the image length at 8, and its offset at 12.
        var entry = Enumerable.Range(0, BitConverter.ToUInt16(ico, 4)).Select(index => 6 + (16 * index)).Where(offset => ico[offset] != 0)
            .OrderBy(offset => ico[offset] < size).ThenBy(offset => Math.Abs(ico[offset] - size)).First();
        return ico.AsSpan(BitConverter.ToInt32(ico, entry + 12), BitConverter.ToInt32(ico, entry + 8)).ToArray();
    }

    private struct WindowClass
    {
        public int Size;
        public uint Style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    private struct NotifyIconData
    {
        public int Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint Version;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(WindowClass* windowClass);

    [LibraryImport("user32.dll")]
    private static partial nint CreateWindowExW(uint extendedStyle, char* className, char* windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint parent, nint after, string className, string? windowName);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial int PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(nint* message, nint window, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(nint* message);

    [LibraryImport("user32.dll")]
    private static partial nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconFromResourceEx(byte* bits, uint size, int isIcon, uint version, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int DestroyIcon(nint icon);

    [LibraryImport("uxtheme.dll", EntryPoint = "#104")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void RefreshImmersiveColorPolicyState();

    [LibraryImport("uxtheme.dll", EntryPoint = "#135")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetPreferredAppMode(int mode);

    [LibraryImport("uxtheme.dll", EntryPoint = "#136")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void FlushMenuThemes();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int AppendMenuW(nint menu, uint flags, nuint id, string text);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

    [LibraryImport("user32.dll")]
    private static partial int DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    private static partial nint GetLastActivePopup(nint window);

    [LibraryImport("user32.dll")]
    private static partial int SetForegroundWindow(nint window);

    [LibraryImport("shell32.dll")]
    private static partial int Shell_NotifyIconW(uint message, NotifyIconData* data);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);
}
