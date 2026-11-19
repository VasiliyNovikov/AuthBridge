using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using AuthBridge.Common;
using Microsoft.Win32;

namespace AuthBridge.Host;

internal static class AboutTheme
{
    public static bool UseDarkPalette(Func<bool> highContrast, Func<bool> darkApps)
    {
        try
        {
            return !highContrast() && darkApps();
        }
        catch (Exception e) when (e is Win32Exception or UnauthorizedAccessException or SecurityException or IOException)
        {
            Log.Warn($"could not read About theme settings; using system colors: {e.Message}");
            return false;
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed unsafe partial class AboutDialog
{
    private const uint DarkBackground = 0x202020;
    private const uint WmInitDialog = 0x0110;
    private readonly nint _backgroundBrush;
    private bool _dark;

    private AboutDialog(nint backgroundBrush) => _backgroundBrush = backgroundBrush;

    public static void Show(nint owner, nint instance)
    {
        // The icon control also uses this brush in an off-screen DC, where a stock DC_BRUSH would be white.
        var brush = CreateSolidBrush(DarkBackground);
        if (brush == 0)
            throw new InvalidOperationException("could not create the About background brush");
        try
        {
            var dialog = new AboutDialog(brush);
            fixed (ushort* template = CreateTemplate())
                if (DialogBoxIndirectParamW(instance, template, owner, dialog.HandleMessage, 0) == -1)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            DeleteObject(brush);
        }
    }

    private static ushort[] CreateTemplate()
    {
        // A DLGTEMPLATE in dialog units lets Windows lay out and scale both static controls.
        List<ushort> words = [];
        void Dword(uint value) => words.AddRange([(ushort)value, (ushort)(value >> 16)]);
        void Text(string text)
        {
            foreach (var ch in text)
                words.Add(ch);
            words.Add(0);
        }
        void StaticControl(uint style, ushort x, ushort y, ushort width, ushort height)
        {
            if ((words.Count & 1) != 0)
                words.Add(0); // item headers are DWORD-aligned
            Dword(0x50000000 | style); // WS_CHILD | WS_VISIBLE
            Dword(0);
            words.AddRange([x, y, width, height, 0xFFFF, 0xFFFF, 0x82]); // IDC_STATIC, STATIC class
        }

        Dword(0x80C808C0); // WS_POPUP | WS_CAPTION | WS_SYSMENU | DS_CENTER | DS_MODALFRAME | DS_SETFONT
        Dword(0);
        words.AddRange([2, 0, 0, 218, 66, 0, 0]); // count, bounds, no menu, default dialog class
        Text("About AuthBridge");
        words.Add(10);
        Text("Segoe UI");
        StaticControl(0x43, 14, 13, 28, 24); // SS_ICON | SS_REALSIZECONTROL
        words.AddRange([0xFFFF, 32512, 0]); // ApplicationIcon resource, no creation data
        StaticControl(0, 53, 12, 151, 43);
        Text($"Version {AppVersion.Release}\r\n\r\nSign in on remote Linux machines with your Windows browser.");
        words.Add(0);
        return [.. words];
    }

    private nint HandleMessage(nint dialog, uint message, nint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case WmInitDialog:
                case 0x0015 or 0x001A or 0x031A: // WM_SYSCOLORCHANGE, WM_SETTINGCHANGE, WM_THEMECHANGED
                    ApplyTheme(dialog);
                    return message == WmInitDialog ? 1 : 0;
                case 0x0136 or 0x0138 when _dark: // WM_CTLCOLORDLG, WM_CTLCOLORSTATIC
                    SetTextColor(wParam, 0xF5F5F5);
                    SetBkColor(wParam, DarkBackground);
                    return _backgroundBrush;
                case 0x0111 when (ushort)wParam is 1 or 2: // WM_COMMAND: Enter or Esc
                case 0x0010: // WM_CLOSE
                    EndDialog(dialog, 1);
                    return 1;
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "About dialog failed");
            EndDialog(dialog, 0);
        }
        return 0;
    }

    private void ApplyTheme(nint dialog)
    {
        _dark = AboutTheme.UseDarkPalette(HighContrastEnabled, () =>
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            int dark = _dark ? 1 : 0;
            var hr = DwmSetWindowAttribute(dialog, 20, &dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            if (hr < 0)
                Log.Warn($"could not theme the About title bar (HRESULT 0x{hr:X8})");
        }
        RedrawWindow(dialog, 0, 0, 0x0085); // RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN
    }

    private static bool HighContrastEnabled()
    {
        var contrast = new HighContrast { Size = (uint)sizeof(HighContrast) };
        if (SystemParametersInfoW(0x0042, contrast.Size, &contrast, 0) == 0) // SPI_GETHIGHCONTRAST
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return (contrast.Flags & 1) != 0;
    }

    private struct HighContrast { public uint Size, Flags; public nint Scheme; }
    private delegate nint DialogProc(nint dialog, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint DialogBoxIndirectParamW(nint instance, ushort* template, nint owner, DialogProc proc, nint parameter);
    [LibraryImport("user32.dll")]
    private static partial int EndDialog(nint dialog, nint result);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SystemParametersInfoW(uint action, uint size, HighContrast* value, uint flags);
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint window, uint attribute, int* value, uint size);
    [LibraryImport("user32.dll")]
    private static partial int RedrawWindow(nint window, nint rect, nint region, uint flags);
    [LibraryImport("gdi32.dll")]
    private static partial uint SetTextColor(nint dc, uint color);
    [LibraryImport("gdi32.dll")]
    private static partial uint SetBkColor(nint dc, uint color);
    [LibraryImport("gdi32.dll")]
    private static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")]
    private static partial int DeleteObject(nint obj);
}
