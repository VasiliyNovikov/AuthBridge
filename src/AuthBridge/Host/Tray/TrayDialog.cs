namespace AuthBridge.Host;

/// <summary>The window settings that the tray's dialogs share.</summary>
internal static class TrayDialog
{
    /// <summary>A DPI-scaled dialog in the middle of the screen, without a taskbar button or a minimize box.</summary>
    public static Form Create(string title, Icon icon) => new()
    {
        Text = title,
        Icon = icon,
        MinimizeBox = false,
        ShowInTaskbar = false,
        StartPosition = FormStartPosition.CenterScreen,
        AutoScaleMode = AutoScaleMode.Dpi,
        AutoScaleDimensions = new SizeF(96, 96),
    };
}
