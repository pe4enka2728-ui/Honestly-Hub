using System.Drawing.Drawing2D;

namespace VpnZapretSwitch;

internal sealed class IncyStatsCard : AstraPanel
{
    private IncyDisplayData? data;
    private readonly ToolTip hint = new();
    public void SetData(IncyDisplayData value)
    {
        data = value;
        hint.SetToolTip(this, value.Note);
        AccessibleName = $"VPN: отдача {value.Upload}; время {value.Elapsed}; приём {value.Download}; сервер {value.ServerName}. {value.Note}";
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float scale = DeviceDpi / 96f;
        int pad = (int)(14 * scale), third = (Width - pad * 2) / 3;
        using var small = new Font("Segoe UI", 8);
        using var body = new Font("Segoe UI", 9, FontStyle.Bold);
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
            | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
        string[] titles = ["↑ Отдача", "Время", "↓ Приём"];
        string[] values = [data?.Upload ?? "—", data?.Elapsed ?? "—", data?.Download ?? "—"];
        for (int i = 0; i < 3; i++)
        {
            int x = pad + i * third;
            TextRenderer.DrawText(e.Graphics, titles[i], small, new Rectangle(x, (int)(9 * scale), third - 3, (int)(18 * scale)), Astra.Muted, flags);
            TextRenderer.DrawText(e.Graphics, values[i], body, new Rectangle(x, (int)(28 * scale), third - 3, (int)(22 * scale)), Astra.Text, flags);
        }
        TextRenderer.DrawText(e.Graphics, data?.ServerName ?? "—", body,
            new Rectangle(pad, (int)(56 * scale), Width - pad * 2, (int)(25 * scale)), Astra.Accent, flags);
    }
    protected override void Dispose(bool disposing) { if (disposing) hint.Dispose(); base.Dispose(disposing); }
}

internal sealed class IncyServersPanel : AstraPanel
{
    private readonly AstraServerList list = new();
    private readonly Label empty = Astra.Label("Загружаем серверы…", 9, Astra.Muted);
    private readonly AstraButton reload = new() { Text = "Обновить", AccessibleName = "Обновить список серверов", Dock = DockStyle.Right, Width = 106, Height = 32 };
    private readonly Label title = Astra.Label("Серверы подключения", 11, style: FontStyle.Bold);
    public event Action<IncyServer>? ServerRequested;
    public event EventHandler? RefreshRequested;
    public IncyServersPanel()
    {
        Padding = new Padding(16, 12, 16, 10);
        var header = new AstraCanvas { Dock = DockStyle.Top, Height = 38, BackColor = Astra.Surface };
        title.Dock = DockStyle.Fill; title.AutoSize = false; title.TextAlign = ContentAlignment.MiddleLeft;
        header.Controls.Add(title); header.Controls.Add(reload);
        empty.Dock = DockStyle.Fill; empty.AutoSize = false; empty.TextAlign = ContentAlignment.MiddleCenter;
        empty.Padding = new Padding(16);
        var body = new AstraCanvas { Dock = DockStyle.Fill, BackColor = Astra.Surface };
        body.Controls.Add(new AstraScrollHost(list, Astra.Surface)); body.Controls.Add(empty);
        Controls.Add(body); Controls.Add(header);
        list.ItemHeight = 62;
        list.DrawItem += DrawServer;
        list.ServerClicked += server => ServerRequested?.Invoke(server);
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || list.SelectedItem is not IncyServer server) return;
            e.Handled = true; e.SuppressKeyPress = true; ServerRequested?.Invoke(server);
        };
        reload.Click += (_, e) => RefreshRequested?.Invoke(this, e);
    }
    public void SetClient(string client)
    {
        title.Text = "Серверы " + client;
        list.AccessibleName = "Серверы " + client;
        reload.AccessibleName = "Обновить серверы " + client;
    }
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        list.ItemHeight = (int)(62 * DeviceDpi / 96f);
    }
    public void SetData(IncyDisplayData data)
    {
        var values = data.Servers.ToArray();
        list.SetData(values);
        empty.Visible = values.Length == 0;
        empty.Text = data.Note.Length > 0 ? data.Note : "Нет серверов. Добавьте подписку через карточку VPN или раздел настроек.";
        list.Visible = values.Length > 0;
    }
    private void DrawServer(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var server = (IncyServer)list.Items[e.Index];
        float scale = DeviceDpi / 96f;
        using var fill = new SolidBrush(server.Selected ? Astra.AccentTint : Astra.Surface);
        e.Graphics.FillRectangle(fill, e.Bounds);
        int pad = (int)(10 * scale), right = (int)(92 * scale);
        CountryFlag.Draw(e.Graphics, new Rectangle(pad, e.Bounds.Y + (int)(15 * scale), (int)(32 * scale), (int)(32 * scale)), server.Name);
        pad += (int)(40 * scale);
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
            | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
        using var name = new Font("Segoe UI", 10, server.Selected ? FontStyle.Bold : FontStyle.Regular);
        using var detail = new Font("Segoe UI", 8.5f);
        TextRenderer.DrawText(e.Graphics, CountryFlag.DisplayName(server.Name), name,
            new Rectangle(pad, e.Bounds.Y + (int)(7 * scale), e.Bounds.Width - pad - right, (int)(24 * scale)), server.Selected ? Astra.Accent : Astra.Text, flags);
        string subtitle = string.Join(" · ", new[] { server.Subscription, server.Description }.Where(s => s.Length > 0));
        TextRenderer.DrawText(e.Graphics, subtitle, detail,
            new Rectangle(pad, e.Bounds.Y + (int)(32 * scale), e.Bounds.Width - pad - right, (int)(21 * scale)), Astra.Muted, flags);
        string ping = server.Selected ? "Выбран" : server.Latency is > 0 ? server.Latency + " мс" : "—";
        TextRenderer.DrawText(e.Graphics, ping, detail,
            new Rectangle(e.Bounds.Right - right, e.Bounds.Y + (int)(18 * scale), right - pad, (int)(25 * scale)), server.Selected ? Astra.Accent : Astra.Muted, flags | TextFormatFlags.Right);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }
}
