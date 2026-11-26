using AuthBridge.Common;

namespace AuthBridge.Host;

internal static class AboutDialog
{
    public static Form Create(Icon icon)
    {
        var form = new Form
        {
            Text = "About AuthBridge",
            Icon = icon,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterScreen,
            KeyPreview = true,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96, 96),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var layout = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(16) };
        var image = icon.ToBitmap();
        var picture = new PictureBox { Image = image, SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 0, 16, 0) };
        picture.Disposed += (_, _) => image.Dispose();
        layout.Controls.Add(picture);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(250, 0),
            Text = $"Version {AppVersion.Release}\r\n\r\nSign in on remote Linux machines with your Windows browser.",
        });
        form.Controls.Add(layout);
        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Escape or Keys.Enter)
                form.Close();
        };
        return form;
    }
}
