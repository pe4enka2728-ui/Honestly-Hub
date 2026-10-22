using System.Diagnostics;

namespace VpnZapretSwitch;

internal sealed class SpotifyConnectionForm : Form
{
    private sealed record DeviceChoice(string Id, string Caption)
    { public override string ToString() => Caption; }
    private readonly SpotifyApi api;
    private readonly CancellationTokenSource closing = new();
    private CancellationTokenSource? operation;
    private readonly TextBox clientId = new() { BorderStyle = BorderStyle.None, BackColor = Astra.Input, ForeColor = Astra.Text, MaxLength = 32 };
    private readonly ComboBox devices = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Astra.Input, ForeColor = Astra.Text };
    private readonly Label status = Astra.Label("", 10, Astra.Muted);
    private readonly AstraButton link = new() { Text = "Привязать Spotify", Kind = ButtonKind.Primary, Width = 196 };
    private readonly AstraButton unlink = new() { Text = "Отвязать", Width = 114 };
    private readonly AstraButton cancel = new() { Text = "Отменить", Width = 114, Visible = false };
    private readonly AstraButton refresh = new() { Text = "Обновить", Width = 114 };
    private readonly AstraButton apply = new() { Text = "Выбрать компьютер", Width = 196 };
    private bool busy;

    internal SpotifyConnectionForm(SpotifyApi api)
    {
        this.api = api;
        Text = "Spotify · Honestly TUN"; Icon = HonestlyBrand.LoadIcon();
        BackColor = Astra.Background; ForeColor = Astra.Text; Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None; ClientSize = new Size(704, 654); MinimumSize = new Size(660, 654);
        Padding = new Padding(6); StartPosition = FormStartPosition.CenterParent; DoubleBuffered = true;
        var body = new AstraCanvas { Dock = DockStyle.Fill, BackColor = Astra.Background, Padding = new Padding(26, 10, 26, 20) };
        var layout = new AstraLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 12, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 44, 52, 25, 44, 28, 42, 51, 27, 44, 48, 24 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        void LabelAt(string text, int row, float size = 10, Color? color = null)
        {
            var label = Astra.Label(text, size, color ?? Astra.Text);
            label.Dock = DockStyle.Fill; label.AutoSize = false; layout.Controls.Add(label, 0, row);
        }
        LabelAt("Музыка · Spotify", 0, 22);
        LabelAt("Свяжите Spotify Premium для синхронизации громкости.\nУправление работает в фоне и не поднимает окно Spotify.", 1, 10, Astra.Muted);
        LabelAt("Client ID вашего приложения Spotify for Developers", 2);
        clientId.Text = api.Connection.ClientId; clientId.AccessibleName = "Публичный Client ID Spotify";
        var input = new AstraPanel { Dock = DockStyle.Fill, Fill = Astra.Input, Radius = 8, Padding = new Padding(12, 10, 12, 8), Margin = new Padding(0, 0, 0, 6) };
        clientId.Dock = DockStyle.Fill; input.Controls.Add(clientId); layout.Controls.Add(input, 0, 3);
        LabelAt("В Dashboard добавьте точный Redirect URI (Client Secret не нужен):", 4, 9, Astra.Muted);
        var redirect = new TextBox { Text = SpotifyOAuth.RedirectUri, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Astra.Background,
            ForeColor = Astra.Accent, Dock = DockStyle.Fill, AccessibleName = "Redirect URI Spotify" };
        layout.Controls.Add(redirect, 0, 5);
        var authActions = Actions(link, unlink, cancel); layout.Controls.Add(authActions, 0, 6);
        LabelAt("Компьютер для управления громкостью", 7);
        devices.Dock = DockStyle.Fill; devices.AccessibleName = "Компьютер Spotify"; devices.Margin = new Padding(0, 0, 0, 10);
        layout.Controls.Add(devices, 0, 8); layout.Controls.Add(Actions(refresh, apply), 0, 9);
        LabelAt("Обложка и кнопки воспроизведения работают через медиасеанс Windows.", 10, 9, Astra.Muted);
        status.AutoSize = false; status.Dock = DockStyle.Fill; status.AccessibleName = "Состояние связи Spotify"; layout.Controls.Add(status, 0, 11);
        body.Controls.Add(layout); Controls.Add(body); Controls.Add(new AstraTitleBar(this));
        link.Click += async (_, _) => await RunAsync(async token =>
        {
            SpotifyOAuth.ValidateClientId(clientId.Text.Trim());
            status.Text = "Завершите вход и разрешите доступ в браузере. Ожидаем ответ Spotify…";
            await api.AuthorizeAsync(clientId.Text, uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }), token);
            await LoadDevicesAsync(token); status.Text = "Spotify связан. Выберите компьютер или оставьте автоматический выбор.";
        });
        unlink.Click += async (_, _) => await RunAsync(async token =>
        {
            await api.DisconnectAsync(token); FillDevices([]);
            status.Text = "Связь на этом компьютере удалена. Доступ приложения можно отозвать в аккаунте Spotify.";
        });
        refresh.Click += async (_, _) => await RunAsync(LoadDevicesAsync);
        apply.Click += async (_, _) => await RunAsync(async token =>
        {
            if (devices.SelectedItem is not DeviceChoice choice) return;
            await api.SelectDeviceAsync(choice.Id, token); status.Text = "Компьютер выбран. Регулятор обновится через несколько секунд.";
        });
        cancel.Click += (_, _) => operation?.Cancel();
        Shown += async (_, _) =>
        {
            if (Owner is not null) CenterToParent();
            var area = Screen.FromControl(Owner ?? this).WorkingArea;
            Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
            status.Text = api.Connection.Note; FillDevices([]); UpdateButtons();
            if (api.Connection.Linked) await RunAsync(LoadDevicesAsync);
        };
        FormClosing += (_, _) => { closing.Cancel(); operation?.Cancel(); };
    }
    private static AstraFlowPanel Actions(params AstraButton[] buttons)
    {
        var panel = new AstraFlowPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        foreach (var button in buttons) { button.Height = 36; button.Margin = new Padding(0, 0, 10, 0); panel.Controls.Add(button); }
        return panel;
    }
    private async Task LoadDevicesAsync(CancellationToken token)
    {
        var found = await api.GetDevicesAsync(token);
        if (IsDisposed || closing.IsCancellationRequested) return;
        FillDevices(found);
        status.Text = found.Any(device => device.IsComputer && !device.Restricted && device.SupportsVolume)
            ? "Устройства обновлены. Громкость читается из Spotify каждые несколько секунд."
            : "Включите трек в настольном Spotify и нажмите «Обновить».";
    }
    private void FillDevices(SpotifyDevice[] found)
    {
        devices.Items.Clear(); devices.Items.Add(new DeviceChoice("", "Автоматически · активный компьютер"));
        foreach (var device in found.Where(device => device.IsComputer && !device.Restricted && device.SupportsVolume && device.Volume is not null))
            devices.Items.Add(new DeviceChoice(device.Id, device.ToString()));
        string selected = api.Connection.DeviceId;
        if (selected.Length > 0 && !devices.Items.Cast<DeviceChoice>().Any(item => item.Id == selected))
            devices.Items.Add(new DeviceChoice(selected, "Сохранённый компьютер · сейчас недоступен"));
        devices.SelectedIndex = Math.Max(0, devices.Items.Cast<DeviceChoice>().ToList().FindIndex(item => item.Id == selected));
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (busy || closing.IsCancellationRequested) return;
        busy = true; operation = CancellationTokenSource.CreateLinkedTokenSource(closing.Token); UpdateButtons();
        try { await action(operation.Token); }
        catch (OperationCanceledException) { if (!IsDisposed) status.Text = "Действие отменено."; }
        catch (Exception ex)
        {
            if (!IsDisposed) status.Text = ex is System.Net.Http.HttpRequestException ? "Нет ответа Spotify. Проверьте подключение и повторите." : ex.Message;
        }
        finally
        {
            operation.Dispose(); operation = null; busy = false;
            if (!IsDisposed) UpdateButtons();
        }
    }
    private void UpdateButtons()
    {
        clientId.Enabled = link.Enabled = !busy; unlink.Enabled = refresh.Enabled = apply.Enabled = devices.Enabled = !busy && api.Connection.Linked;
        cancel.Visible = busy; link.Text = api.Connection.Linked ? "Привязать заново" : "Привязать Spotify";
        link.Invalidate();
    }
}
