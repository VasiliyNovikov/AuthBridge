using AuthBridge.Common;

namespace AuthBridge.Host;

/// <summary>Implements the Win32 dialog reply contract; painting and window lifetime belong to the native host.</summary>
internal sealed class AboutDialogProcedure(Action<nint> applyTheme, Func<nint, nint> background, Action<nint, int> endDialog)
{
    private const uint WmInitDialog = 0x0110;

    public nint Handle(nint dialog, uint message, nint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case WmInitDialog:
                case 0x0015 or 0x001A or 0x031A: // WM_SYSCOLORCHANGE, WM_SETTINGCHANGE, WM_THEMECHANGED
                    applyTheme(dialog);
                    return message == WmInitDialog ? 1 : 0;
                case 0x0136 or 0x0138: // WM_CTLCOLORDLG, WM_CTLCOLORSTATIC
                    return background(wParam);
                // IDOK/IDCANCEL from the keyboard, menu, or a button click, not a control's focus notification.
                case 0x0111 when ((ushort)wParam is 1 or 2) && (lParam == 0 || (wParam >> 16) == 0):
                case 0x0010: // WM_CLOSE
                    endDialog(dialog, 1);
                    return 1;
            }
        }
        catch (Exception e)
        {
            // Managed exceptions must not escape the native callback.
            Log.Error(e, "About dialog failed");
            endDialog(dialog, 0);
        }
        return 0;
    }
}
