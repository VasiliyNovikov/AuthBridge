using AuthBridge.Common;

namespace AuthBridge.Host;

internal sealed class StatusDialog : IDisposable
{
    private readonly int _port;
    private readonly Label _summary = new()
    {
        AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8), UseMnemonic = false,
    };
    private readonly ListView _connections = new()
    {
        AccessibleName = "Tunnel connections",
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        ShowItemToolTips = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };

    public Form Window { get; }

    public StatusDialog(Icon icon, int port, Func<PingResult> getStatus)
    {
        _port = port;
        Window = new Form
        {
            Text = "AuthBridge Status",
            Icon = icon,
            ShowInTaskbar = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96, 96),
            ClientSize = new Size(740, 320),
            MinimumSize = new Size(480, 220),
            Padding = new Padding(12),
        };
        _connections.Columns.Add("Machine", 130);
        _connections.Columns.Add("State", 100);
        _connections.Columns.Add("Since (local)", 150);
        _connections.Columns.Add("Client version", 105);
        _connections.Columns.Add("Details", 210);
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel, Margin = new Padding(0, 8, 0, 0) };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        footer.Controls.Add(close);
        Window.CancelButton = close;
        Window.Controls.Add(_connections);
        Window.Controls.Add(footer);
        Window.Controls.Add(_summary);
        Window.VisibleChanged += (_, _) =>
        {
            if (Window.Visible)
                Update(getStatus());
        };
    }

    public void Dispose() => Window.Dispose();

    public void Update(PingResult status)
    {
        var tunnels = status.Tunnels;
        var connections = tunnels.Length == 0 ? "No machines configured" : $"{tunnels.Count(t => t.State == "connected")}/{tunnels.Length} connected";
        _summary.Text = $"AuthBridge {AppVersion.Release} on {status.Machine}  |  PID {status.Pid}  |  127.0.0.1:{_port}\r\n" +
            $"{connections}  |  Active relay sessions: {status.Sessions}";

        for (var i = 0; i < tunnels.Length; i++)
        {
            var row = i < _connections.Items.Count ? _connections.Items[i] : _connections.Items.Add("");
            if (!Equals(row.Tag, tunnels[i]))
                UpdateRow(row, tunnels[i]);
        }
        while (_connections.Items.Count > tunnels.Length)
            _connections.Items.RemoveAt(_connections.Items.Count - 1);
    }

    private static void UpdateRow(ListViewItem row, TunnelStatus tunnel)
    {
        if (row.Text != tunnel.Name)
            row.Selected = false;
        row.Tag = tunnel;
        row.SubItems.Clear();
        row.Text = tunnel.Name;
        row.SubItems.AddRange([tunnel.State, tunnel.Since.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"), tunnel.Client, tunnel.Detail]);
        row.ToolTipText = tunnel.Detail;
    }
}
