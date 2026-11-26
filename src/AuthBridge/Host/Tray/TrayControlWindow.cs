using System.Runtime.InteropServices;

namespace AuthBridge.Host;

/// <summary>The hidden window used by the stop command and the updater, independent of Explorer's tray icon.</summary>
internal sealed partial class TrayControlWindow : NativeWindow, IDisposable
{
    private const string WindowCaption = "AuthBridgeTray";
    private const int CloseMessage = 0x0010, AutomaticUpdateReady = 0x8002;
    private const uint AbortIfHung = 0x0002;
    private readonly TrayUpdater _updates;
    private readonly Action _close;

    public TrayControlWindow(TrayUpdater updates, Action close)
    {
        _updates = updates;
        _close = close;
        CreateHandle(new CreateParams { Caption = WindowCaption });
    }

    public void Dispose() => DestroyHandle();

    public bool PostClose() => PostClose(Handle);
    public void Activate() => SetForegroundWindow(Handle);

    protected override void WndProc(ref Message message)
    {
        switch (message.Msg)
        {
            case CloseMessage:
                _close();
                return;
            case AutomaticUpdateReady:
                message.Result = _updates.TryRequestIdleRestart((int)message.WParam, PostClose) ? 1 : 0;
                return;
            default:
                base.WndProc(ref message);
                return;
        }
    }

    public static bool RequestExit(int processId) => Request(processId, PostClose);

    public static bool RequestIdleExit(int processId) => Request(processId, window =>
        SendMessageTimeoutW(window, AutomaticUpdateReady, Environment.ProcessId, 0, AbortIfHung, 5000, out var accepted) != 0 && accepted != 0);

    private static bool Request(int processId, Func<nint, bool> send)
    {
        // The pre-WinForms helper has the same caption and messages, but a different window class.
        for (nint window = 0; (window = FindWindowExW(0, window, null, WindowCaption)) != 0;)
        {
            if (GetWindowThreadProcessId(window, out var owner) != 0 && owner == processId)
                return send(window);
        }
        return false;
    }

    private static bool PostClose(nint window) => window != 0 && PostMessageW(window, CloseMessage, 0, 0) != 0;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint parent, nint after, string? className, string windowName);
    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
    [LibraryImport("user32.dll")]
    private static partial int PostMessageW(nint window, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll")]
    private static partial nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [LibraryImport("user32.dll")]
    private static partial int SetForegroundWindow(nint window);
}
