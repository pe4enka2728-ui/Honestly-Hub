using System.Diagnostics;

namespace VpnZapretSwitch;

public sealed class MainForm : Form
{
    private Settings settings;
    private readonly TextBox incyPath = MakeTextBox();
    private readonly TextBox zapretFolder = MakeTextBox();
    private readonly AstraComboBox strategy = new() { Dock = DockStyle.Fill, AccessibleName = "Стратегия Zapret" };
    private readonly AstraComboBox zapretMode = new() { Dock = DockStyle.Fill, AccessibleName = "Режим запуска Zapret" };
    private readonly AstraComboBox vpnMode = new() { Dock = DockStyle.Fill, AccessibleName = "VPN-клиент" };
    private readonly AstraComboBox dns = new() { Dock = DockStyle.Fill, AccessibleName = "DNS встроенного VPN" };
    private readonly TextBox subscriptionSummary = MakeTextBox();
    private readonly AstraButton internetCheck = new() { Text = "Проверка интернета: включена", Size = new Size(335, 34) };
    private bool verifyInternet;
    private readonly AstraButton toggle = new() { Kind = ButtonKind.Primary, Width = 258, Height = 46, Text = "Включить VPN", Font = new Font("Segoe UI", 11, FontStyle.Bold) };
    private readonly AstraButton save = new() { Kind = ButtonKind.Primary, Text = "Сохранить настройки", Size = new Size(214, 44) };
    private readonly AstraButton openIncy = new() { Text = "Открыть", AccessibleName = "Открыть INCY", Font = new Font("Segoe UI", 9) };
    private readonly AstraButton openZapret = new() { Text = "Открыть", AccessibleName = "Открыть service.bat", Font = new Font("Segoe UI", 9) };
    private readonly AstraButton vpnPower = new() { Text = "Включить", AccessibleName = "Включить только VPN", Font = new Font("Segoe UI", 9) };
    private readonly AstraButton zapretPower = new() { Text = "Включить", AccessibleName = "Включить только Zapret", Font = new Font("Segoe UI", 9) };
    private readonly ZapretInfoPanel zapretInfo = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, Radius = 12 };
    private readonly AstraEmblem emblem = new() { Size = new Size(64, 64), Anchor = AnchorStyles.None };
    private readonly Label heroTitle = Astra.Label("Готово к подключению", 21, style: FontStyle.Bold);
    private readonly Label heroHint = Astra.Label("Проверяем состояние приложений…", 10, Astra.Muted);
    private readonly Label overviewSubtitle = Astra.Label("", 10, Astra.Muted);
    private readonly IncyStatsCard stats = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, Radius = 12 };
    private readonly IncyServersPanel servers = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Label modeHint = Astra.Label("", 9, Astra.Muted);
    private readonly AstraStatusCard incyCard = new() { Title = "INCY VPN", State = "Проверка…", Dock = DockStyle.Fill };
    private readonly AstraStatusCard zapretCard = new() { Title = "ZAPRET", State = "Проверка…", Dock = DockStyle.Fill };
    private readonly AstraLogBox log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.None, BackColor = Astra.Surface, ForeColor = Astra.Muted,
        Font = new Font("Consolas", 10), AccessibleName = "Журнал переключений"
    };
    private readonly Panel content = new AstraCanvas { Dock = DockStyle.Fill, BackColor = Astra.Background, Padding = new Padding(32, 22, 32, 24) };
    private readonly List<Control> configurationControls = [];
    private readonly Dictionary<string, Panel> pages = [];
    private readonly Dictionary<string, AstraButton> navigation = [];
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 2500 };
    private bool busy;
    private bool returnToZapret;
    private bool checking;
    private bool startupCompleted;
    private RuntimeStatus? runtime;
    private bool Embedded => settings.VpnMode == "embedded";
    private bool closingAfterDisconnect;
    private bool closingAfterEpicSave;
    private bool switchingClient;
    private readonly SpotifyPanel music = new() { Dock = DockStyle.Fill };
    private readonly EpicAccountsPage accounts = new();
    private ApplicationsForm? applications;
    private PetsForm? pets;

    public MainForm()
    {
        string? settingsError = null;
        try { settings = Settings.Load(); }
        catch (Exception ex) { settings = new Settings(); settingsError = ex.Message; }
        Text = "Honestly TUN";
        Icon = HonestlyBrand.LoadIcon();
        Font = new Font("Segoe UI", 10);
        ForeColor = Astra.Text;
        BackColor = Astra.Background;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(1080, 800);
        MinimumSize = new Size(960, 740);
        Padding = new Padding(6);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        toggle.Enabled = false;
        toggle.Text = "Проверяем состояние…";
        var shell = new AstraLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Astra.Background };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.Controls.Add(BuildSidebar(), 0, 0);
        shell.Controls.Add(content, 1, 0);
        Controls.Add(shell);
        Controls.Add(new AstraTitleBar(this));
        AddPage("overview", BuildOverview());
        AddPage("settings", BuildSettings());
        AddPage("journal", BuildJournal());
        AddPage("accounts", accounts);
        ShowPage("overview");
        incyPath.Text = settings.IncyExe;
        incyPath.AccessibleName = "Путь к приложению INCY";
        zapretFolder.Text = settings.ZapretFolder;
        zapretFolder.AccessibleName = "Папка Zapret";
        ReloadStrategies(settings.StrategyFile);
        zapretMode.Items.AddRange([".bat", "service.bat"]);
        zapretMode.SelectedIndex = settings.ZapretMode == "service" ? 1 : 0;
        zapretMode.SelectedIndexChanged += (_, _) => UpdateModeHint();
        vpnMode.Items.AddRange(["Встроенный Honestly VPN", "Внешнее приложение INCY"]);
        vpnMode.SelectedIndex = Embedded ? 0 : 1;
        verifyInternet = settings.VerifyInternet;
        dns.Items.AddRange(VpnDns.Names.Cast<object>().ToArray());
        dns.SelectedIndex = Math.Max(0, Array.IndexOf(VpnDns.Keys, settings.DnsProvider));
        internetCheck.Text = "Проверка интернета: " + (verifyInternet ? "включена" : "выключена");
        internetCheck.Click += (_, _) => { verifyInternet = !verifyInternet; internetCheck.Text = "Проверка интернета: " + (verifyInternet ? "включена" : "выключена"); };
        subscriptionSummary.ReadOnly = true;
        ApplyBackend();
        vpnMode.Enabled = false;
        vpnMode.SelectedIndexChanged += async (_, _) => await ChangeVpnClientAsync();
        UpdateModeHint();
        toggle.Click += async (_, _) => await ToggleAsync();
        vpnPower.Click += async (_, _) => await ToggleComponentAsync(true);
        zapretPower.Click += async (_, _) => await ToggleComponentAsync(false);
        save.Click += async (_, _) => { if (SaveSettings()) { await RefreshStateAsync(); await RefreshDisplayAsync(); } };
        openIncy.Click += OpenIncy;
        openZapret.Click += OpenZapret;
        servers.RefreshRequested += async (_, _) => await RefreshServersAsync();
        servers.ServerRequested += async server => await SelectServerAsync(server);
        refresh.Tick += async (_, _) => { if (!busy) { await RefreshStateAsync(); await RefreshDisplayAsync(); } };
        Shown += async (_, _) =>
        {
            string introNotice = StartupVideoForm.ShowIntro(this);
            music.Start();
            if (introNotice.Length > 0) AppendLog(introNotice);
            if (settingsError is not null)
                AppendLog("Не удалось прочитать settings.json: " + settingsError + ". Загружены стандартные пути.");
            else AppendLog("Настройки загружены. Проверяем VPN и Zapret…");
            await RefreshStateAsync(initial: true);
            _ = RefreshDisplayAsync();
            if (!IsDisposed && !Disposing) refresh.Start();
        };
        FormClosing += async (_, e) =>
        {
            if (accounts.IsBusy) { e.Cancel = true; AppendLog("Дождитесь завершения операции с аккаунтами Epic перед закрытием."); return; }
            if (closingAfterDisconnect) return;
            if (busy) { e.Cancel = true; AppendLog("Дождитесь завершения операции перед закрытием."); return; }
            if (!closingAfterEpicSave)
            {
                e.Cancel = true;
                busy = true; SetActionsEnabled(false);
                try { await accounts.FlushSessionBeforeCloseAsync(); closingAfterEpicSave = true; }
                finally { busy = false; if (closingAfterEpicSave) Close(); else SetActionsEnabled(true); }
                return;
            }
            if (!EmbeddedVpnHost.Shared.HasSession) return;
            e.Cancel = true; busy = true; SetActionsEnabled(false);
            AppendLog("Отключаем встроенный VPN перед закрытием…");
            try { await EmbeddedVpnHost.Shared.DisconnectAsync(); closingAfterDisconnect = true; }
            catch (Exception ex) { ShowError(ex); }
            finally { busy = false; if (closingAfterDisconnect) Close(); else SetActionsEnabled(true); }
        };
    }

    private Panel BuildSidebar()
    {
        var sidebar = new AstraCanvas { Dock = DockStyle.Fill, BackColor = Astra.Sidebar, Margin = Padding.Empty, Padding = new Padding(16, 22, 16, 22) };
        var layout = new AstraLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Astra.Sidebar, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, music.DesiredHeight));
        var brand = new AstraCanvas { Dock = DockStyle.Fill, BackColor = Astra.Sidebar };
        brand.Controls.Add(new AstraEmblem { Compact = true, Location = new Point(0, 3), Size = new Size(39, 39) });
        var wordmark = Astra.Label("Honestly", 16, style: FontStyle.Bold);
        wordmark.Location = new Point(45, 0);
        brand.Controls.Add(wordmark);
        var wordmarkSuffix = Astra.Label("TUN", 10, Astra.Accent, FontStyle.Bold);
        wordmarkSuffix.Location = new Point(47, 30);
        brand.Controls.Add(wordmarkSuffix);
        var description = Astra.Label("Ваш маршрут", 9, Astra.Muted);
        description.Location = new Point(8, 62);
        brand.Controls.Add(description);
        layout.Controls.Add(brand, 0, 0);
        var nav = new AstraLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 6, Height = 300, Margin = Padding.Empty, Padding = Padding.Empty };
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var item in new[] { ("overview", "Обзор", NavigationIcon.Overview), ("accounts", "Аккаунты", NavigationIcon.Accounts), ("settings", "Настройки", NavigationIcon.Settings), ("journal", "Журнал", NavigationIcon.Journal) })
        {
            string page = item.Item1;
            var button = new AstraButton { Text = item.Item2, Icon = item.Item3, Kind = ButtonKind.Navigation, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6), AccessibleName = item.Item2 };
            button.Click += (_, _) => ShowPage(page);
            int row = navigation.Count;
            nav.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            nav.Controls.Add(button, 0, row);
            navigation.Add(page, button);
        }
        var apps = new AstraButton { Text = "Приложения", Icon = NavigationIcon.Applications, Kind = ButtonKind.Navigation,
            Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6), AccessibleName = "Приложения" };
        apps.Click += (_, _) => OpenApplications();
        nav.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        nav.Controls.Add(apps, 0, 4);
        var petsButton = new AstraButton { Text = "Питомцы", Icon = NavigationIcon.Pets, Kind = ButtonKind.Navigation,
            Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6), AccessibleName = "Питомцы" };
        petsButton.Click += (_, _) => OpenPets();
        nav.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        nav.Controls.Add(petsButton, 0, 5);
        layout.Controls.Add(nav, 0, 1);
        layout.Controls.Add(music, 0, 4);
        sidebar.Controls.Add(layout);
        sidebar.AutoScroll = true; layout.MinimumSize = new Size(0, music.DesiredHeight == 232 ? 738 : 702);
        return new AstraScrollHost(sidebar, Astra.Sidebar, alwaysVisible: false);
    }

    private void OpenApplications()
    {
        if (applications is null || applications.IsDisposed)
        {
            applications = new ApplicationsForm();
            applications.Show(this);
        }
        else
        {
            if (applications.WindowState == FormWindowState.Minimized) applications.WindowState = FormWindowState.Normal;
            applications.Activate();
        }
    }

    private void OpenPets()
    {
        if (pets is null || pets.IsDisposed)
        {
            pets = new PetsForm();
            pets.Show(this);
        }
        else
        {
            if (pets.WindowState == FormWindowState.Minimized) pets.WindowState = FormWindowState.Normal;
            pets.Activate();
        }
    }

    private Panel BuildOverview()
    {
        var page = NewPage();
        page.AutoScroll = true;
        var layout = NewTable(7);
        layout.Dock = DockStyle.Top;
        layout.Height = 890;
        foreach (int height in new[] { 76, 238, 14, 274, 14, 264 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(PageHeader("Ваше подключение", "", overviewSubtitle), 0, 0);
        var hero = new AstraPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 12, 24, 12), Margin = Padding.Empty };
        var heroLayout = NewTable(4);
        heroLayout.BackColor = Color.Transparent;
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        heroLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        heroLayout.Controls.Add(emblem, 0, 0);
        heroTitle.AutoSize = false; heroTitle.Dock = DockStyle.Fill; heroTitle.TextAlign = ContentAlignment.MiddleCenter;
        heroHint.AutoSize = false; heroHint.Dock = DockStyle.Fill; heroHint.TextAlign = ContentAlignment.TopCenter;
        heroHint.Padding = new Padding(10, 3, 10, 0);
        heroLayout.Controls.Add(heroTitle, 0, 1); heroLayout.Controls.Add(heroHint, 0, 2);
        var actions = new AstraFlowPanel { Anchor = AnchorStyles.None, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, BackColor = Color.Transparent };
        toggle.Margin = Padding.Empty;
        actions.Controls.Add(toggle);
        heroLayout.Controls.Add(actions, 0, 3);
        hero.Controls.Add(heroLayout);
        layout.Controls.Add(hero, 0, 1);
        var cards = new AstraLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); cards.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14)); cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        incyCard.Margin = Padding.Empty; zapretCard.Margin = Padding.Empty;
        var incyStack = NewTable(3);
        incyStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 164));
        incyStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
        incyStack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        incyCard.AddOpenButton(openIncy);
        zapretCard.AddOpenButton(openZapret);
        incyCard.AddPowerButton(vpnPower); zapretCard.AddPowerButton(zapretPower);
        incyStack.Controls.Add(incyCard, 0, 0); incyStack.Controls.Add(stats, 0, 2);
        var zapretStack = NewTable(3);
        zapretStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 164));
        zapretStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
        zapretStack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        zapretStack.Controls.Add(zapretCard, 0, 0);
        zapretStack.Controls.Add(zapretInfo, 0, 2);
        cards.Controls.Add(incyStack, 0, 0); cards.Controls.Add(zapretStack, 2, 0);
        layout.Controls.Add(cards, 0, 3);
        layout.Controls.Add(servers, 0, 5);
        page.Controls.Add(layout);
        page.Visible = true;
        return new AstraScrollHost(page, Astra.Background) { Visible = false };
    }

    private Panel BuildSettings()
    {
        var page = NewPage();
        page.AutoScroll = true;
        var layout = NewTable(11);
        layout.Dock = DockStyle.Top; layout.Height = 1100;
        foreach (int height in new[] { 88, 108, 108, 108, 108, 108, 108, 108, 132, 56 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(PageHeader("Настройки", "Добавьте подписку, выберите VPN-клиент и режим Zapret."), 0, 0);
        var browseIncy = new AstraButton { Text = "Обзор…", Width = 94 };
        browseIncy.Click += BrowseIncy;
        var browseZapret = new AstraButton { Text = "Обзор…", Width = 94 };
        browseZapret.Click += BrowseZapret;
        var reload = new AstraButton { Text = "Обновить", Width = 94 };
        reload.Click += (_, _) => ReloadStrategies(strategy.Text);
        var subscriptionButton = new AstraButton { Text = "Добавить" };
        subscriptionButton.Click += async (_, _) => await ImportSubscriptionAsync();
        layout.Controls.Add(SettingsField("VPN-клиент", "Выбор сохраняется сразу; главная страница обновляется автоматически", vpnMode, new AstraButton { Visible = false }), 0, 1);
        layout.Controls.Add(SettingsField("Подписка встроенного клиента", "Персональная HTTPS-ссылка или локальный файл ключей", subscriptionSummary, subscriptionButton), 0, 2);
        layout.Controls.Add(SettingsField("Папка Zapret", "Содержит service.bat и папку bin", zapretFolder, browseZapret), 0, 3);
        layout.Controls.Add(SettingsField("Стратегия запуска", "По умолчанию — general (ALT).bat", strategy, reload), 0, 4);
        var modeField = SettingsField("Режим Zapret", "Способ запуска при возврате из VPN", zapretMode, new AstraButton { Visible = false });
        layout.Controls.Add(modeField, 0, 5);
        layout.Controls.Add(SettingsField("INCY (для внешнего режима)", "Не требуется для встроенного VPN; путь также используется для проверки конфликта", incyPath, browseIncy), 0, 6);
        layout.Controls.Add(SettingsField("DNS встроенного VPN", "DNS-over-HTTPS через VPN; применяется при следующем подключении. DNS INCY меняется в INCY.", dns, new AstraButton { Visible = false }), 0, 7);
        modeHint.AutoSize = false; modeHint.Dock = DockStyle.Fill; modeHint.Padding = new Padding(2, 4, 0, 0);
        var options = new AstraLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        options.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); options.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        internetCheck.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        options.Controls.Add(internetCheck, 0, 0); options.Controls.Add(modeHint, 0, 1);
        layout.Controls.Add(options, 0, 8);
        var actions = new AstraFlowPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        save.Margin = Padding.Empty;
        actions.Controls.Add(save);
        var spotifySettings = new AstraButton { Text = "Музыка · Spotify", Width = 184, Height = 40, Margin = new Padding(12, 0, 0, 0) };
        spotifySettings.Click += (_, _) => music.OpenConnection(this); actions.Controls.Add(spotifySettings);
        layout.Controls.Add(actions, 0, 9);
        page.Controls.Add(layout);
        configurationControls.AddRange([incyPath, zapretFolder, strategy, zapretMode, vpnMode, dns, subscriptionButton, internetCheck, browseIncy, browseZapret, reload, save]);
        page.Visible = true;
        return new AstraScrollHost(page, Astra.Background) { Visible = false };
    }

    private Control SettingsField(string title, string description, Control field, AstraButton browse)
    {
        var group = new AstraLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = new Padding(0, 0, 0, 16) };
        group.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); group.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        group.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); group.RowStyles.Add(new RowStyle(SizeType.Absolute, 23)); group.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        var caption = Astra.Label(title, 10, style: FontStyle.Bold);
        caption.AutoSize = false; caption.Dock = DockStyle.Fill; caption.AutoEllipsis = true;
        var explanation = Astra.Label(description, 9, Astra.Muted);
        explanation.AutoSize = false; explanation.Dock = DockStyle.Fill; explanation.AutoEllipsis = true;
        group.Controls.Add(caption, 0, 0); group.SetColumnSpan(caption, 2);
        group.Controls.Add(explanation, 0, 1); group.SetColumnSpan(explanation, 2);
        var input = new AstraPanel { Fill = Astra.Input, Dock = DockStyle.Fill, Radius = 9, Margin = Padding.Empty, Padding = new Padding(12, 9, 12, 8) };
        field.Dock = DockStyle.Fill;
        input.Controls.Add(field);
        group.Controls.Add(input, 0, 2);
        browse.Dock = DockStyle.Fill; browse.Margin = new Padding(10, 0, 0, 0);
        group.Controls.Add(browse, 1, 2);
        return group;
    }

    private Panel BuildJournal()
    {
        var page = NewPage();
        var layout = NewTable(3);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.Controls.Add(PageHeader("Журнал", "Ход переключений и сообщения текущего сеанса."), 0, 0);
        var card = new AstraPanel { Dock = DockStyle.Fill, Padding = new Padding(18), Margin = Padding.Empty };
        card.Controls.Add(new AstraScrollHost(log, Astra.Surface));
        layout.Controls.Add(card, 0, 1);
        var clear = new AstraButton { Text = "Очистить журнал", Kind = ButtonKind.Quiet, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, Size = new Size(168, 38), Margin = Padding.Empty };
        clear.Click += (_, _) => log.Clear();
        layout.Controls.Add(clear, 0, 2);
        page.Controls.Add(layout);
        return page;
    }

    private static Panel PageHeader(string title, string subtitle, Label? descriptionLabel = null)
    {
        var header = new AstraLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 43)); header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = Astra.Label(title, 24, style: FontStyle.Bold);
        heading.AutoSize = false; heading.Dock = DockStyle.Fill; heading.AutoEllipsis = true;
        var description = descriptionLabel ?? Astra.Label(subtitle, 10, Astra.Muted);
        description.AutoSize = false; description.Dock = DockStyle.Fill; description.AutoEllipsis = true;
        header.Controls.Add(heading, 0, 0);
        header.Controls.Add(description, 0, 1);
        return header;
    }
    private static Panel NewPage() => new AstraCanvas { Dock = DockStyle.Fill, BackColor = Astra.Background, Margin = Padding.Empty, Visible = false };
    private static TableLayoutPanel NewTable(int rows)
    {
        var table = new AstraLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.Transparent };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }
    private static TextBox MakeTextBox() => new() { BorderStyle = BorderStyle.None, BackColor = Astra.Input, ForeColor = Astra.Text, Font = new Font("Segoe UI", 10) };
    private void AddPage(string name, Panel page) { pages.Add(name, page); content.Controls.Add(page); }
    private void ShowPage(string name)
    {
        foreach (var page in pages) page.Value.Visible = page.Key == name;
        foreach (var button in navigation) { button.Value.Selected = button.Key == name; button.Value.Invalidate(); }
        pages[name].BringToFront();
    }
    private void BrowseIncy(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog { Filter = "Приложение INCY|incy.exe|Программы|*.exe", FileName = incyPath.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK) incyPath.Text = dialog.FileName;
    }
    private void BrowseZapret(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Папка, содержащая service.bat и каталог bin", SelectedPath = zapretFolder.Text };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        zapretFolder.Text = dialog.SelectedPath;
        ReloadStrategies(strategy.Text);
    }
    private void ReloadStrategies(string preferred)
    {
        strategy.Items.Clear();
        try
        {
            if (Directory.Exists(zapretFolder.Text))
                strategy.Items.AddRange(Directory.GetFiles(zapretFolder.Text, "*.bat")
                    .Select(Path.GetFileName).Where(n => n is not null && !n.StartsWith("service", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray());
            int index = strategy.FindStringExact(preferred);
            if (index < 0) index = strategy.FindStringExact("general (ALT).bat");
            strategy.SelectedIndex = index;
        }
        catch (Exception ex) { AppendLog(ex.Message); }
    }
    private Settings ReadSettings() => new()
    {
        IncyExe = string.IsNullOrWhiteSpace(incyPath.Text) ? new Settings().IncyExe : incyPath.Text.Trim(), ZapretFolder = zapretFolder.Text.Trim(),
        StrategyFile = strategy.SelectedItem as string ?? "",
        ServiceName = settings.ServiceName, TimeoutSeconds = settings.TimeoutSeconds,
        ZapretMode = zapretMode.SelectedIndex == 1 ? "service" : "bat",
        VpnMode = vpnMode.SelectedIndex == 0 ? "embedded" : "incy", VerifyInternet = verifyInternet,
        DnsProvider = VpnDns.Keys[Math.Max(0, dns.SelectedIndex)]
    };
    private bool SaveSettings(bool validate = true)
    {
        try
        {
            var candidate = ReadSettings();
            if (candidate.VpnMode != settings.VpnMode && (EmbeddedVpnHost.Shared.HasSession || new WindowsPlatform(settings).IsVpnRunning()))
                throw new InvalidOperationException("Отключите VPN перед сменой клиента.");
            if (validate) new WindowsPlatform(candidate).Validate();
            candidate.Save();
            settings = candidate;
            ApplyBackend();
            AppendLog("Настройки сохранены.");
            return true;
        }
        catch (Exception ex) { ShowError(ex); return false; }
    }
    private async void OpenIncy(object? sender, EventArgs e)
    {
        if (Embedded) { await ImportSubscriptionAsync(); return; }
        try
        {
            var start = new ProcessStartInfo(incyPath.Text.Trim()) { UseShellExecute = true };
            start.ArgumentList.Add("incy://status");
            using var process = Process.Start(start);
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void OpenZapret(object? sender, EventArgs e)
    {
        try
        {
            string script = Path.Combine(zapretFolder.Text.Trim(), "service.bat");
            if (!File.Exists(script)) throw new FileNotFoundException("В выбранной папке нет service.bat.");
            if (script.IndexOfAny(['\"', '%', '!', '\r', '\n']) >= 0) throw new InvalidOperationException("Недопустимые символы в пути service.bat.");
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(script),
                Arguments = "/d /v:off /s /c \"\"" + script + "\" admin\""
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void UpdateModeHint() => modeHint.Text = zapretMode.SelectedIndex == 1
        ? "service.bat: установить службу с выбранной стратегией → Enter → обновить IPSet (пункт 7) → закрыть консоль. Служба запускается автоматически при старте Windows."
        : ".bat: прежнее поведение. Если служба уже установлена, запускается она с её стратегией. Иначе запускается выбранный .bat.";

    private void ApplyBackend()
    {
        incyCard.Title = Embedded ? "Honestly VPN" : "INCY VPN";
        overviewSubtitle.Text = Embedded ? "Встроенный Honestly VPN и Zapret в одном окне." : "Внешний INCY VPN и Zapret в одном окне.";
        servers.SetClient(Embedded ? "Honestly VPN" : "INCY");
        openIncy.Text = Embedded ? "Подписка" : "Открыть";
        openIncy.AccessibleName = Embedded ? "Добавить подписку встроенного VPN" : "Открыть INCY";
        subscriptionSummary.Text = EmbeddedVpnHost.Shared.Summary;
        incyCard.Invalidate();
    }
    private async Task ChangeVpnClientAsync()
    {
        if (switchingClient) return;
        string selected = vpnMode.SelectedIndex == 0 ? "embedded" : "incy";
        if (selected == settings.VpnMode) return;
        if (busy || !startupCompleted)
        {
            switchingClient = true;
            try { vpnMode.SelectedIndex = Embedded ? 0 : 1; }
            finally { switchingClient = false; }
            return;
        }
        switchingClient = true; busy = true; SetActionsEnabled(false); refresh.Stop();
        try
        {
            var oldSettings = settings;
            var oldStatus = await Task.Run(() => new WindowsPlatform(oldSettings).ReadIncyStatus());
            if (EmbeddedVpnHost.Shared.HasSession || oldStatus.IsRunning)
                throw new InvalidOperationException("Отключите VPN перед сменой клиента.");
            if (oldStatus.State == VpnState.Unknown)
                throw new InvalidOperationException("Сначала дождитесь проверки состояния текущего VPN-клиента.");
            var candidate = ReadSettings();
            await Task.Run(() => new WindowsPlatform(candidate).ValidateVpn());
            candidate.Save();
            settings = candidate; returnToZapret = false;
            ApplyBackend();
            string client = Embedded ? "Honestly VPN" : "INCY";
            var loading = new IncyDisplayData([], "—", "—", "—", "—", "Загружаем серверы " + client + "…");
            stats.SetData(loading); servers.SetData(loading);
            runtime = new(new(false, false, VpnState.Unknown, "Проверяем " + client + "…"),
                runtime?.Zapret ?? new(null, false, "Проверка…", "Проверяем Zapret…"));
            ApplyRuntimeStatus(runtime);
            AppendLog("Выбран VPN-клиент: " + client + ". Обновляем главную страницу…");
        }
        catch (Exception ex)
        {
            vpnMode.SelectedIndex = Embedded ? 0 : 1;
            ShowError(ex);
        }
        finally
        {
            busy = false; switchingClient = false; SetActionsEnabled(true);
            await RefreshStateAsync(); await RefreshDisplayAsync();
            if (!IsDisposed && !Disposing) refresh.Start();
        }
    }
    private async Task ImportSubscriptionAsync()
    {
        if (busy || EmbeddedVpnHost.Shared.AnyProcess)
        {
            if (!busy) ShowError(new InvalidOperationException("Отключите встроенный VPN перед изменением подписки."));
            return;
        }
        using var dialog = new SubscriptionDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await ChangeSubscriptionAsync(() => EmbeddedVpnHost.Shared.ImportAsync(dialog.Content, dialog.FromFile));
    }
    private async Task RefreshServersAsync()
    {
        if (busy) return;
        if (!Embedded) { await RefreshStateAsync(); await RefreshDisplayAsync(); return; }
        if (!EmbeddedVpnHost.Shared.HasProfiles) { await ImportSubscriptionAsync(); return; }
        if (EmbeddedVpnHost.Shared.AnyProcess)
        {
            ShowError(new InvalidOperationException("Для загрузки обновления подписки сначала отключите встроенный VPN. Статистика обновляется автоматически."));
            return;
        }
        await ChangeSubscriptionAsync(EmbeddedVpnHost.Shared.UpdateAsync);
    }
    private async Task ChangeSubscriptionAsync(Func<Task> change)
    {
        busy = true; SetActionsEnabled(false); refresh.Stop();
        try
        {
            AppendLog("Импортируем подписку…");
            await change();
            ApplyBackend();
            AppendLog("Подписка обновлена: " + EmbeddedVpnHost.Shared.Summary +
                (EmbeddedVpnHost.Shared.Skipped > 0 ? $". Пропущено неподдерживаемых записей: {EmbeddedVpnHost.Shared.Skipped}." : "."));
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            busy = false; SetActionsEnabled(true);
            await RefreshStateAsync(); await RefreshDisplayAsync();
            if (!IsDisposed && !Disposing) refresh.Start();
        }
    }

    private async Task SelectServerAsync(IncyServer server)
    {
        if (busy || checking || !startupCompleted) return;
        if (!SaveSettings()) return;
        busy = true;
        SetActionsEnabled(false);
        refresh.Stop();
        try
        {
            var current = new WindowsPlatform(settings).ReadRuntimeStatus();
            if (current.Incy.State is VpnState.Starting or VpnState.Unknown)
                throw new InvalidOperationException("Дождитесь завершения подключения VPN перед выбором сервера.");
            if (current.Incy.IsRunning && current.Zapret.Running != false)
                throw new InvalidOperationException("Сначала оставьте активным только VPN, затем выберите сервер.");
            AppendLog("Выбор сервера: " + server.Name + "…");
            if (Embedded) await EmbeddedVpnHost.Shared.SelectAsync(server.Id, settings, AppendLog);
            else await IncyAccessibility.SelectServerAsync(settings, server);
            AppendLog("Выбран сервер: " + server.Name);
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            busy = false; SetActionsEnabled(true);
            await RefreshStateAsync(); await RefreshDisplayAsync(); refresh.Start();
        }
    }
    private void SetActionsEnabled(bool enabled)
    {
        toggle.Enabled = enabled && startupCompleted;
        vpnPower.Enabled = enabled && startupCompleted; zapretPower.Enabled = enabled && startupCompleted;
        openIncy.Enabled = enabled; openZapret.Enabled = enabled; servers.Enabled = enabled;
        foreach (var control in configurationControls) control.Enabled = enabled;
    }
    private async Task ToggleAsync()
    {
        if (busy || checking || !startupCompleted) return;
        if (Embedded && !EmbeddedVpnHost.Shared.HasProfiles) { await ImportSubscriptionAsync(); return; }
        if (runtime?.Incy.State == VpnState.Unknown || runtime?.Zapret.Running is null)
        {
            await RefreshStateAsync();
            return;
        }
        if (!SaveSettings()) return;
        if (Embedded && !returnToZapret && !EmbeddedVpnHost.Shared.AnyProcess)
        {
            try { new WindowsPlatform(settings).EnsureExternalVpnOff(); }
            catch (Exception ex) { ShowError(ex); return; }
        }
        busy = true;
        SetActionsEnabled(false);
        emblem.Busy = true;
        refresh.Stop();
        try
        {
            returnToZapret |= new WindowsPlatform(settings).IsVpnRunning();
            toggle.Text = "Переключаем…";
            heroTitle.Text = "Меняем маршрут";
            Action<string> report = message =>
            {
                heroHint.Text = message;
                AppendLog(message);
            };
            var controller = new SwitchController(new WindowsPlatform(settings, report), report);
            returnToZapret = await controller.ToggleAsync(returnToZapret);
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            busy = false;
            SetActionsEnabled(true);
            emblem.Busy = false;
            await RefreshStateAsync();
            _ = RefreshDisplayAsync();
            if (!IsDisposed && !Disposing) refresh.Start();
        }
    }
    private async Task ToggleComponentAsync(bool vpn)
    {
        if (busy || checking || !startupCompleted) return;
        if (vpn && Embedded && !EmbeddedVpnHost.Shared.HasProfiles) { await ImportSubscriptionAsync(); return; }
        if (!SaveSettings(validate: false)) return;
        busy = true; SetActionsEnabled(false); refresh.Stop();
        try
        {
            var platform = new WindowsPlatform(settings, AppendLog);
            if (vpn)
            {
                if (platform.IsVpnRunning())
                {
                    AppendLog("Отключение только VPN…"); await platform.DisconnectVpnAsync(); returnToZapret = false;
                }
                else
                {
                    platform.ValidateVpn();
                    if (platform.IsZapretRunning()) throw new InvalidOperationException("Сначала выключите Zapret его кнопкой или используйте общее переключение.");
                    AppendLog("Подключение только VPN…");
                    try { await platform.ConnectVpnAsync(); }
                    catch { await platform.DisconnectVpnAsync(); throw; }
                    returnToZapret = true;
                }
            }
            else if (platform.IsZapretRunning())
            { AppendLog("Остановка только Zapret…"); await platform.StopZapretAsync(); }
            else
            {
                platform.ValidateZapret();
                if (platform.IsVpnRunning()) throw new InvalidOperationException("Сначала выключите VPN его кнопкой или используйте общее переключение.");
                AppendLog("Запуск только Zapret…"); await platform.StartZapretAsync(); returnToZapret = false;
            }
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            busy = false; SetActionsEnabled(true);
            await RefreshStateAsync(); await RefreshDisplayAsync();
            if (!IsDisposed && !Disposing) refresh.Start();
        }
    }
    private bool readingDisplay;
    private bool displayRefreshRequested;
    private string lastDisplayWarning = "";
    private async Task RefreshDisplayAsync()
    {
        if (busy || IsDisposed || runtime is null) return;
        if (readingDisplay) { displayRefreshRequested = true; return; }
        readingDisplay = true;
        displayRefreshRequested = false;
        var observed = settings;
        var state = runtime.Incy.State;
        try
        {
            var data = observed.VpnMode == "embedded" ? await EmbeddedVpnHost.Shared.ReadDisplayAsync()
                : await IncyAccessibility.ReadDisplayAsync(observed, state);
            if (IsDisposed || Disposing || busy || !ReferenceEquals(settings, observed) || runtime?.Incy.State != state) return;
            stats.SetData(data); servers.SetData(data);
            if (data.Note.Contains("недоступна:", StringComparison.Ordinal))
            {
                if (lastDisplayWarning != data.Note) AppendLog(data.Note);
                lastDisplayWarning = data.Note;
            }
            else lastDisplayWarning = "";
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing && !busy && ReferenceEquals(settings, observed) && runtime?.Incy.State == state)
            {
                var data = new IncyDisplayData([], "—", "—", "—", "—", "Не удалось прочитать VPN: " + ex.Message);
                stats.SetData(data); servers.SetData(data);
            }
        }
        finally
        {
            readingDisplay = false;
            if (displayRefreshRequested && !busy && !IsDisposed && !Disposing)
            { displayRefreshRequested = false; _ = RefreshDisplayAsync(); }
        }
    }
    private bool refreshRequested;
    private async Task RefreshStateAsync(bool initial = false)
    {
        if (busy || IsDisposed) return;
        if (checking) { refreshRequested = true; return; }
        checking = true;
        refreshRequested = false;
        Settings observedSettings = settings;
        try
        {
            var result = await Task.Run(() => new WindowsPlatform(observedSettings).ReadRuntimeStatus());
            if (IsDisposed || Disposing) return;
            if (busy || !ReferenceEquals(settings, observedSettings)) { refreshRequested = true; return; }
            runtime = result;
            if (initial) returnToZapret = result.Incy.IsRunning;
            ApplyRuntimeStatus(result);
            if (initial)
                AppendLog($"Начальная проверка: VPN — {incyCard.State}, {result.Incy.Detail}; Zapret — {result.Zapret.StateText}.");
        }
        catch (Exception ex)
        {
            if (IsDisposed || Disposing) return;
            if (busy || !ReferenceEquals(settings, observedSettings)) { refreshRequested = true; return; }
            runtime = new(new(false, false, VpnState.Unknown, ex.Message), new(null, false, "Неизвестно", ex.Message));
            ApplyRuntimeStatus(runtime);
            AppendLog("Не удалось завершить проверку: " + ex.Message);
        }
        finally
        {
            checking = false;
            startupCompleted |= initial;
            if (!IsDisposed && !Disposing)
            {
                toggle.Enabled = !busy && startupCompleted;
                vpnPower.Enabled = !busy && startupCompleted; zapretPower.Enabled = !busy && startupCompleted;
                vpnMode.Enabled = !busy && startupCompleted;
                if (refreshRequested && !busy) _ = RefreshStateAsync();
            }
        }
    }

    private void ApplyRuntimeStatus(RuntimeStatus result)
    {
        string client = Embedded ? "Honestly VPN" : "INCY";
        var incy = result.Incy;
        bool vpn = incy.IsRunning;
        bool zapret = result.Zapret.Running == true;
        bool unknown = incy.State == VpnState.Unknown || result.Zapret.Running is null;
        toggle.Text = unknown ? "Повторить проверку" : returnToZapret || vpn ? "Вернуться к Zapret" : "Включить VPN";
        if (Embedded && !EmbeddedVpnHost.Shared.HasProfiles && !vpn && !unknown && !returnToZapret) toggle.Text = "Добавить подписку";
        heroTitle.Text = incy.State switch
        {
            VpnState.Active => Embedded ? "Honestly VPN подключён" : "Режим INCY",
            VpnState.Starting => "VPN подключается",
            VpnState.Unknown => incy.ApplicationRunning ? client + " открыт" : "Нужна проверка статуса",
            _ => zapret ? "Zapret работает" : incy.ApplicationRunning ? client + " открыт · VPN отключён" : "Готово к подключению"
        };
        heroHint.Text = incy.State switch
        {
            VpnState.Active => Embedded ? "Системный туннель активен. При закрытии окна встроенный VPN отключается." : "Подключение INCY активно. Доступность сервера смотрите в INCY.",
            VpnState.Starting => "Ядро активно. Ожидается готовность туннеля.",
            VpnState.Unknown => incy.Detail,
            _ => returnToZapret ? "VPN отключён. Нажмите кнопку, чтобы вернуть Zapret."
                : Embedded ? EmbeddedVpnHost.Shared.HasProfiles ? "Подключимся к выбранному серверу, а Zapret будет остановлен." : "Добавьте полную персональную ссылку подписки, затем выберите сервер."
                    : "INCY подключится к выбранному серверу, а Zapret будет остановлен."
        };
        if (vpn && zapret) { heroTitle.Text = "Оба приложения активны"; heroHint.Text = "Нажмите кнопку, чтобы отключить VPN и оставить Zapret."; }
        if (result.Zapret.Running is null) heroHint.Text = "Не удалось проверить Zapret: " + result.Zapret.Detail;
        incyCard.Active = incy.State == VpnState.Active;
        vpnPower.Text = vpn ? "Выключить" : "Включить";
        vpnPower.AccessibleName = vpnPower.Text + " только VPN";
        zapretPower.Text = zapret ? "Выключить" : "Включить";
        zapretPower.AccessibleName = zapretPower.Text + " только Zapret";
        zapretInfo.SetData(result.Zapret);
        incyCard.State = incy.State switch
        {
            VpnState.Active => "VPN активно", VpnState.Starting => "Подключается…",
            VpnState.Unknown => "Статус неизвестен", _ => "VPN отключён"
        };
        incyCard.Detail = incy.Detail;
        incyCard.AccessibleName = client + ": " + incyCard.State + ". " + incy.Detail;
        zapretCard.Active = zapret;
        zapretCard.State = result.Zapret.StateText;
        zapretCard.Detail = result.Zapret.Detail;
        zapretCard.AccessibleName = "Zapret: " + zapretCard.State;
        emblem.Active = vpn || zapret;
        emblem.Invalidate(); incyCard.Invalidate(true); zapretCard.Invalidate(true);
    }
    private void AppendLog(string message)
    {
        string stamp = DateTime.Now.ToString("HH:mm:ss");
        log.AppendEntry($"[{stamp}] {message}{Environment.NewLine}{Environment.NewLine}");
    }
    private void ShowError(Exception ex)
    {
        AppendLog("Ошибка: " + ex.Message);
        MessageBox.Show(this, ex.Message, "Honestly TUN", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    protected override void WndProc(ref Message m)
    {
        const int hitTest = 0x84;
        base.WndProc(ref m);
        if (m.Msg != hitTest || WindowState != FormWindowState.Normal || (int)m.Result != 1) return;
        var point = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
        int grip = Math.Max(5, 6 * DeviceDpi / 96);
        bool left = point.X < grip, right = point.X >= ClientSize.Width - grip;
        bool top = point.Y < grip, bottom = point.Y >= ClientSize.Height - grip;
        m.Result = left && top ? 13 : right && top ? 14 : left && bottom ? 16 : right && bottom ? 17
            : left ? 10 : right ? 11 : top ? 12 : bottom ? 15 : 1;
    }
    internal void ToggleWindowSize()
    {
        MaximizedBounds = Screen.FromControl(this).WorkingArea;
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Astra.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) refresh.Dispose();
        base.Dispose(disposing);
    }
}


