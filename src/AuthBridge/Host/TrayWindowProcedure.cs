namespace AuthBridge.Host;

/// <summary>Routes tray messages. A null reply delegates to DefWindowProc.</summary>
internal sealed class TrayWindowProcedure(uint taskbarCreated, Action refresh, Action<int, int> showMenu,
    Action close, Action quit, Action invalidate, Func<int, bool> requestIdleExit)
{
    public const uint CloseMessage = 0x0010, CallbackMessage = 0x8001; // WM_CLOSE, WM_APP + 1
    public const uint AutomaticUpdateReady = 0x8002;
    private const uint WmDestroy = 0x0002, WmContextMenu = 0x007B, WmTimer = 0x0113;
    private const uint NinSelect = 0x0400, NinKeySelect = 0x0401;

    public nint? Handle(uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmTimer:
                refresh();
                return 0;
            case AutomaticUpdateReady:
                return requestIdleExit((int)wParam) ? 1 : 0;
            case CallbackMessage when ((uint)lParam & 0xFFFF) is WmContextMenu or NinSelect or NinKeySelect:
                showMenu((short)wParam, (short)(wParam >> 16));
                return 0;
            case CloseMessage:
                close();
                return null; // DefWindowProc destroys the window.
            case WmDestroy:
                quit();
                return 0;
        }
        if (message == taskbarCreated)
        {
            invalidate();
            refresh();
        }
        return null;
    }
}
