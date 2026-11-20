namespace AuthBridge.Host;

/// <summary>Routes tray messages and tells the native window procedure whether Windows still needs to handle them.</summary>
internal sealed class TrayWindowProcedure(uint taskbarCreated, Action refresh, Action<int, int> showMenu,
    Action close, Action quit, Action invalidate)
{
    public const uint CloseMessage = 0x0010, CallbackMessage = 0x8001; // WM_CLOSE, WM_APP + 1
    private const uint WmDestroy = 0x0002, WmContextMenu = 0x007B, WmTimer = 0x0113;
    private const uint NinSelect = 0x0400, NinKeySelect = 0x0401;

    public bool Handle(uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmTimer:
                refresh();
                return true;
            case CallbackMessage when ((uint)lParam & 0xFFFF) is WmContextMenu or NinSelect or NinKeySelect:
                showMenu((short)wParam, (short)(wParam >> 16));
                return true;
            case CloseMessage:
                close();
                return false; // DefWindowProc destroys the window.
            case WmDestroy:
                quit();
                return true;
        }
        if (message == taskbarCreated)
        {
            invalidate();
            refresh();
        }
        return false;
    }
}
