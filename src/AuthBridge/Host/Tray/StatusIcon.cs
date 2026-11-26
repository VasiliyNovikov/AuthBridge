using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using AuthBridge.Common;

namespace AuthBridge.Host;

internal sealed partial class StatusIcon : IDisposable
{
    private readonly NotifyIcon _notification;
    private readonly Icon _base;
    private uint _color;

    public StatusIcon(Icon icon, ContextMenuStrip menu, TrayControlWindow window)
    {
        _base = new Icon(icon, SystemInformation.SmallIconSize);
        _notification = new NotifyIcon { Text = "AuthBridge", ContextMenuStrip = menu };
        _notification.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                window.Activate();
                menu.Show(Cursor.Position);
            }
        };
    }

    public void Show(TunnelStatus[] tunnels)
    {
        var (tip, color) = TrayNotification.Describe(tunnels);
        if (color != _color)
        {
            var old = _notification.Icon;
            _notification.Icon = WithDot(color);
            old?.Dispose();
            _color = color;
        }
        // Windows 11 uses the first tooltip as the icon name. Publish the status only after it is visible.
        _notification.Visible = true;
        _notification.Text = tip;
    }

    private Icon WithDot(uint color)
    {
        using var bitmap = _base.ToBitmap();
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.CompositingMode = CompositingMode.SourceCopy;
            var diameter = bitmap.Width * 0.38f;
            var left = bitmap.Width - diameter;
            var gap = Math.Max(1f, bitmap.Width / 16f);
            graphics.FillEllipse(Brushes.Transparent, left - gap, left - gap, diameter + 2 * gap, diameter + 2 * gap);
            using var brush = new SolidBrush(Color.FromArgb(unchecked((int)(0xFF000000 | color))));
            graphics.FillEllipse(brush, left, left, diameter, diameter);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        var icon = _notification.Icon;
        _notification.Dispose();
        icon?.Dispose();
        _base.Dispose();
    }

    [LibraryImport("user32.dll")]
    private static partial int DestroyIcon(nint icon);
}
