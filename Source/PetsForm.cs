using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO;

namespace VpnZapretSwitch;

public sealed class PetsForm : Form
{
    private readonly AstraCanvas content = new() { Dock = DockStyle.Top, BackColor = Astra.Background };
    private readonly Label statusLabel = Astra.Label("Проверяем состояние ThatPet…", 9, Astra.Muted);
    private readonly AstraButton processToggle = new() { Text = "Запустить ThatPet", Kind = ButtonKind.Primary, Size = new Size(160, 34) };
    private readonly AstraButton testCelebrate = new() { Text = "Празднование 🎉", Kind = ButtonKind.Quiet, Size = new Size(145, 34) };
    private readonly AstraButton testWorking = new() { Text = "Кодинг 💻", Kind = ButtonKind.Quiet, Size = new Size(120, 34) };
    private readonly AstraButton openFolder = new() { Text = "Папка ассетов 📁", Kind = ButtonKind.Quiet, Size = new Size(140, 34) };

    private readonly AstraButton wanderToggle = new() { Kind = ButtonKind.Secondary, Size = new Size(150, 32) };
    private readonly AstraButton soundsToggle = new() { Kind = ButtonKind.Secondary, Size = new Size(130, 32) };
    private readonly AstraButton bubbleToggle = new() { Kind = ButtonKind.Secondary, Size = new Size(140, 32) };
    private readonly List<AstraButton> sizeButtons = [];

    private readonly AstraButton connectAntigravity = new() { Text = "Связать Antigravity", Kind = ButtonKind.Secondary, Size = new Size(165, 32) };
    private readonly AstraButton connectCodex = new() { Text = "Связать Codex", Kind = ButtonKind.Secondary, Size = new Size(140, 32) };
    private readonly AstraButton connectAll = new() { Text = "Связать всех агентов", Kind = ButtonKind.Primary, Size = new Size(175, 32) };
    private readonly Label agentsStatus = Astra.Label("Хуки передают события от AI-агентов питомцу на рабочем столе.", 9, Astra.Muted);

    // Hero Showcase
    private readonly AstraPanel heroCard = new() { Fill = Astra.Surface, Radius = 14 };
    private readonly PictureBox heroImage = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
    private readonly Label heroTitle = Astra.Label("", 16, Astra.Text, FontStyle.Bold);
    private readonly Label heroSubtitle = Astra.Label("", 9.5f, Astra.Muted);
    private readonly Label heroBadge = Astra.Label("● Активен на рабочем столе", 9, Astra.Accent);
    private readonly FlowLayoutPanel poseSelector = new() { AutoSize = true, BackColor = Color.Transparent, FlowDirection = FlowDirection.LeftToRight };
    private readonly AstraButton openModelFolderBtn = new() { Text = "Открыть файлы модели", Kind = ButtonKind.Secondary, Size = new Size(165, 30) };

    private readonly Dictionary<string, PetTile> tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> cachedThumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> cachedPoses = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 1500 };
    private readonly ToolTip hints = new();

    private List<PetPack> packs = [];
    private string activePetId = "opus";
    private string currentPreviewPose = "idle";
    private int collectionScrollPos;
    private Action<int>? applyCollectionScroll;
    private bool released;

    public PetsForm()
    {
        Text = "Питомцы · ThatPet";
        Icon = HonestlyBrand.LoadIcon();
        BackColor = Astra.Background;
        ForeColor = Astra.Text;
        Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(900, 740);
        MinimumSize = new Size(740, 560);
        Padding = new Padding(6);
        StartPosition = FormStartPosition.CenterParent;
        DoubleBuffered = true;

        var body = new AstraCanvas { Dock = DockStyle.Fill, BackColor = Astra.Background, Padding = new Padding(24, 14, 24, 18) };

        // Heading
        var heading = new AstraCanvas { Dock = DockStyle.Top, Height = 118, BackColor = Astra.Background };
        var title = Astra.Label("Питомцы", 22, style: FontStyle.Bold);
        title.Location = new Point(0, 0);

        var description = Astra.Label("Интерактивные персонажи ThatPet на рабочем столе · реакция на AI-агентов и действия в коде", 10, Astra.Muted);
        description.AutoSize = false;
        description.SetBounds(0, 40, 820, 24);
        description.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;

        processToggle.Location = new Point(0, 72);
        processToggle.Click += (_, _) => ToggleThatPetProcess();

        testCelebrate.Location = new Point(170, 72);
        testCelebrate.Click += async (_, _) => await TriggerTestReaction("celebrate", "🎉 Питомец празднует успех!");
        hints.SetToolTip(testCelebrate, "Отправить питомцу событие праздника/успеха для мгновенной анимации");

        testWorking.Location = new Point(325, 72);
        testWorking.Click += async (_, _) => await TriggerTestReaction("Stop", "💻 Питомец перешёл в рабочий режим!");
        hints.SetToolTip(testWorking, "Отправить событие кодинга / работы за ноутбуком");

        openFolder.Location = new Point(455, 72);
        openFolder.Click += (_, _) => OpenThatPetFolder();
        hints.SetToolTip(openFolder, "Открыть общую папку установки ThatPet в Проводнике");

        heading.Controls.Add(title);
        heading.Controls.Add(description);
        heading.Controls.Add(processToggle);
        heading.Controls.Add(testCelebrate);
        heading.Controls.Add(testWorking);
        heading.Controls.Add(openFolder);

        // Status banner at bottom
        statusLabel.Dock = DockStyle.Bottom;
        statusLabel.AutoSize = false;
        statusLabel.Height = 32;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;

        // Content layout inside scrollhost
        var page = new AstraCanvas { BackColor = Astra.Background, AutoSize = true };
        page.Controls.Add(content);

        var scroll = new AstraScrollHost(page, Astra.Background, alwaysVisible: true);

        body.Controls.Add(scroll);
        body.Controls.Add(statusLabel);
        body.Controls.Add(heading);

        Controls.Add(body);
        Controls.Add(new AstraTitleBar(this));

        refreshTimer.Tick += (_, _) => UpdateProcessStatus();

        Shown += (_, _) =>
        {
            LoadData();
            UpdateProcessStatus();
            if (!released) refreshTimer.Start();
        };
    }

    private void LoadData()
    {
        activePetId = ThatPetService.GetActiveCharacter();
        packs = ThatPetService.LoadPacks();
        BuildContentLayout();
        UpdateHeroCard();
    }

    private void BuildContentLayout()
    {
        content.Controls.Clear();
        int D(int n) => (int)Math.Round(n * DeviceDpi / 96d);
        int y = 0;
        int cardWidth = Math.Max(D(640), ClientSize.Width - D(70));

        // 1. ACTIVE PET HERO CARD (Preview of currently selected model & poses gallery)
        heroCard.Location = new Point(0, y);
        heroCard.Size = new Size(cardWidth, D(175));
        heroCard.Padding = new Padding(D(16));
        heroCard.Controls.Clear();

        heroImage.Location = new Point(D(16), D(16));
        heroImage.Size = new Size(D(135), D(135));

        heroTitle.Location = new Point(D(165), D(14));
        heroTitle.AutoSize = true;

        heroSubtitle.Location = new Point(D(166), D(46));
        heroSubtitle.AutoSize = true;

        heroBadge.Location = new Point(D(166), D(70));
        heroBadge.AutoSize = true;

        // Pose buttons row
        var posesLabel = Astra.Label("Позы и ассеты:", 9, Astra.Muted);
        posesLabel.Location = new Point(D(166), D(98));
        posesLabel.AutoSize = true;

        poseSelector.Location = new Point(D(165), D(122));
        poseSelector.Size = new Size(D(480), D(38));

        openModelFolderBtn.Location = new Point(cardWidth - D(185), D(14));
        openModelFolderBtn.Click += (_, _) => OpenActiveModelFolder();
        hints.SetToolTip(openModelFolderBtn, "Открыть папку с ассетами текущей выбранной модели");

        heroCard.Controls.Add(heroImage);
        heroCard.Controls.Add(heroTitle);
        heroCard.Controls.Add(heroSubtitle);
        heroCard.Controls.Add(heroBadge);
        heroCard.Controls.Add(posesLabel);
        heroCard.Controls.Add(poseSelector);
        heroCard.Controls.Add(openModelFolderBtn);

        content.Controls.Add(heroCard);
        y += heroCard.Height + D(20);

        // 2. PETS COLLECTION WITH SCROLLBAR (ПОЛЗУНОК)
        var charHeader = Astra.Label("Коллекция питомцев", 11, Astra.Text, FontStyle.Bold);
        charHeader.Location = new Point(0, y);
        charHeader.AutoSize = true;

        var scrollHint = Astra.Label("перетаскивайте ползунок внизу или крутите колесо мыши", 9, Astra.Muted);
        scrollHint.Location = new Point(D(160), y + D(2));
        scrollHint.AutoSize = true;

        var scrollLeftBtn = new AstraButton { Text = "◄", Kind = ButtonKind.Secondary, Size = new Size(D(32), D(24)), Location = new Point(cardWidth - D(72), y) };
        var scrollRightBtn = new AstraButton { Text = "►", Kind = ButtonKind.Secondary, Size = new Size(D(32), D(24)), Location = new Point(cardWidth - D(34), y) };

        content.Controls.Add(charHeader);
        content.Controls.Add(scrollHint);
        content.Controls.Add(scrollLeftBtn);
        content.Controls.Add(scrollRightBtn);
        y += D(30);

        int tileW = D(172);
        int tileH = D(198);
        int gap = D(12);
        int totalStripW = packs.Count * tileW + Math.Max(0, packs.Count - 1) * gap;

        var viewport = new AstraCanvas
        {
            Location = new Point(0, y),
            Size = new Size(cardWidth, tileH + D(4)),
            BackColor = Astra.Background
        };

        var cardsStrip = new AstraCanvas
        {
            Location = new Point(-collectionScrollPos, 0),
            Size = new Size(Math.Max(cardWidth, totalStripW), tileH),
            BackColor = Astra.Background
        };

        tiles.Clear();
        int tx = 0;
        foreach (var pack in packs)
        {
            if (!cachedThumbnails.TryGetValue(pack.Id, out var bmp) && File.Exists(pack.IdleImagePath))
            {
                try { bmp = new Bitmap(pack.IdleImagePath); cachedThumbnails[pack.Id] = bmp; }
                catch { }
            }

            bool isActive = pack.Id.Equals(activePetId, StringComparison.OrdinalIgnoreCase);
            var tile = new PetTile(pack, bmp, isActive);
            tile.Location = new Point(tx, 0);
            tile.Size = new Size(tileW, tileH);
            tile.Click += (_, _) => SelectPet(pack.Id);
            tiles[pack.Id] = tile;
            cardsStrip.Controls.Add(tile);
            hints.SetToolTip(tile, $"{pack.Name}\nАвтор: {pack.Author}\nПапка: {pack.FolderName}\nАссетов: {pack.TotalPoses} PNG\nНажмите для выбора");
            tx += tileW + gap;
        }

        viewport.Controls.Add(cardsStrip);
        content.Controls.Add(viewport);
        y += viewport.Height + D(6);

        var hBar = new AstraHorizontalScrollBar
        {
            Location = new Point(0, y),
            Size = new Size(cardWidth, D(18)),
            BackColor = Astra.Background,
            SmallChange = tileW + gap
        };

        int maxScroll = Math.Max(0, totalStripW - cardWidth);
        applyCollectionScroll = pos =>
        {
            collectionScrollPos = Math.Clamp(pos, 0, maxScroll);
            cardsStrip.Location = new Point(-collectionScrollPos, 0);
            hBar.SetRange(maxScroll, cardWidth, collectionScrollPos);
        };

        hBar.SetRange(maxScroll, cardWidth, collectionScrollPos);
        hBar.PositionRequested += (pos, _) => applyCollectionScroll(pos);
        hBar.StepRequested += delta => applyCollectionScroll(collectionScrollPos + delta);
        hBar.WheelRequested += delta => applyCollectionScroll(collectionScrollPos - Math.Sign(delta) * (tileW + gap));

        viewport.MouseWheel += (_, e) => applyCollectionScroll(collectionScrollPos - Math.Sign(e.Delta) * (tileW + gap));
        cardsStrip.MouseWheel += (_, e) => applyCollectionScroll(collectionScrollPos - Math.Sign(e.Delta) * (tileW + gap));

        scrollLeftBtn.Click += (_, _) => applyCollectionScroll(collectionScrollPos - (tileW + gap));
        scrollRightBtn.Click += (_, _) => applyCollectionScroll(collectionScrollPos + (tileW + gap));

        content.Controls.Add(hBar);
        y += hBar.Height + D(20);

        // 3. SETTINGS CARD
        var settingsCard = new AstraPanel
        {
            Location = new Point(0, y),
            Size = new Size(cardWidth, D(135)),
            Fill = Astra.Surface,
            Radius = 14,
            Padding = new Padding(D(16), D(12), D(16), D(12))
        };

        var settingsTitle = Astra.Label("Параметры ThatPet", 11, Astra.Text, FontStyle.Bold);
        settingsTitle.Location = new Point(D(16), D(12));
        settingsCard.Controls.Add(settingsTitle);

        // Size selector
        var sizeLabel = Astra.Label("Размер:", 9, Astra.Muted);
        sizeLabel.Location = new Point(D(16), D(42));
        settingsCard.Controls.Add(sizeLabel);

        int bx = D(80);
        double currentSize = ThatPetService.GetSize();
        sizeButtons.Clear();
        foreach (var (val, lbl) in new[] { (0.35, "35%"), (0.5, "50%"), (0.75, "75%"), (1.0, "100%") })
        {
            var btn = new AstraButton
            {
                Text = lbl,
                Kind = Math.Abs(currentSize - val) < 0.05 ? ButtonKind.Primary : ButtonKind.Secondary,
                Location = new Point(bx, D(38)),
                Size = new Size(D(56), D(28))
            };
            double targetVal = val;
            btn.Click += (_, _) => SetPetSize(targetVal);
            sizeButtons.Add(btn);
            settingsCard.Controls.Add(btn);
            bx += D(62);
        }

        // Toggles row
        UpdateToggles();
        wanderToggle.Location = new Point(D(16), D(80));
        wanderToggle.Click += (_, _) =>
        {
            bool next = !ThatPetService.GetBoolSetting("wander", true);
            ThatPetService.SetBoolSetting("wander", next);
            UpdateToggles();
            statusLabel.Text = "Блуждание по экрану: " + (next ? "включено" : "выключено");
        };

        soundsToggle.Location = new Point(D(176), D(80));
        soundsToggle.Click += (_, _) =>
        {
            bool next = !ThatPetService.GetBoolSetting("sounds", true);
            ThatPetService.SetBoolSetting("sounds", next);
            UpdateToggles();
            statusLabel.Text = "Звуковые эффекты: " + (next ? "включены" : "выключены");
        };

        bubbleToggle.Location = new Point(D(316), D(80));
        bubbleToggle.Click += (_, _) =>
        {
            bool next = !ThatPetService.GetBoolSetting("bubble", true);
            ThatPetService.SetBoolSetting("bubble", next);
            UpdateToggles();
            statusLabel.Text = "Текстовые реплики: " + (next ? "включены" : "выключены");
        };

        settingsCard.Controls.Add(wanderToggle);
        settingsCard.Controls.Add(soundsToggle);
        settingsCard.Controls.Add(bubbleToggle);

        content.Controls.Add(settingsCard);
        y += settingsCard.Height + D(16);

        // 4. AGENTS CARD
        var agentsCard = new AstraPanel
        {
            Location = new Point(0, y),
            Size = new Size(cardWidth, D(120)),
            Fill = Astra.Surface,
            Radius = 14,
            Padding = new Padding(D(16), D(12), D(16), D(12))
        };

        var agentsTitle = Astra.Label("Связь с AI-агентами (Хуки)", 11, Astra.Text, FontStyle.Bold);
        agentsTitle.Location = new Point(D(16), D(12));
        agentsCard.Controls.Add(agentsTitle);

        agentsStatus.Location = new Point(D(16), D(38));
        agentsStatus.Size = new Size(D(680), D(22));
        agentsCard.Controls.Add(agentsStatus);

        connectAntigravity.Location = new Point(D(16), D(68));
        connectAntigravity.Click += async (_, _) =>
        {
            string res = await ThatPetService.RunCommandAsync("connect antigravity");
            agentsStatus.Text = "Antigravity: " + (res.Length > 0 ? res : "подключён");
        };

        connectCodex.Location = new Point(D(190), D(68));
        connectCodex.Click += async (_, _) =>
        {
            string res = await ThatPetService.RunCommandAsync("connect codex");
            agentsStatus.Text = "Codex: " + (res.Length > 0 ? res : "подключён");
        };

        connectAll.Location = new Point(D(340), D(68));
        connectAll.Click += async (_, _) =>
        {
            string res = await ThatPetService.RunCommandAsync("connect all");
            agentsStatus.Text = "Агенты: " + (res.Length > 0 ? res : "все подключены");
        };

        agentsCard.Controls.Add(connectAntigravity);
        agentsCard.Controls.Add(connectCodex);
        agentsCard.Controls.Add(connectAll);

        content.Controls.Add(agentsCard);
        y += agentsCard.Height + D(20);

        content.Height = y;
    }

    private void UpdateHeroCard()
    {
        var pack = packs.FirstOrDefault(p => p.Id.Equals(activePetId, StringComparison.OrdinalIgnoreCase))
            ?? packs.FirstOrDefault();

        if (pack is null) return;

        heroTitle.Text = pack.Name;
        heroSubtitle.Text = $"Автор: {pack.Author}  ·  {pack.TotalPoses} анимированных ассетов  ·  Папка: {pack.FolderName}";

        bool running = ThatPetService.IsRunning;
        heroBadge.Text = running ? "● Активен на рабочем столе" : "○ Выбран в настройках (ThatPet остановлен)";
        heroBadge.ForeColor = running ? Astra.Green : Astra.Muted;

        // Populate pose selector
        poseSelector.Controls.Clear();
        int D(int n) => (int)Math.Round(n * DeviceDpi / 96d);

        var poses = new (string Key, string Title, string FilePath)[]
        {
            ("idle", "Покой", pack.IdleImagePath),
            ("working", "Кодинг", pack.WorkingImagePath),
            ("thinking", "Размышление", pack.ThinkingImagePath),
            ("celebrate", "Успех", pack.CelebrateImagePath),
            ("signature", "Фирменная", pack.SignatureImagePath),
            ("sleep", "Сон", pack.SleepImagePath)
        };

        string currentPath = pack.IdleImagePath;
        foreach (var (key, title, path) in poses)
        {
            if (!File.Exists(path)) continue;
            bool isCurrent = key.Equals(currentPreviewPose, StringComparison.OrdinalIgnoreCase);
            if (isCurrent) currentPath = path;

            var poseBtn = new AstraButton
            {
                Text = title,
                Kind = isCurrent ? ButtonKind.Primary : ButtonKind.Secondary,
                Size = new Size(D(76), D(28)),
                Margin = new Padding(0, 0, D(6), 0)
            };
            string capturedKey = key;
            string capturedPath = path;
            poseBtn.Click += (_, _) =>
            {
                currentPreviewPose = capturedKey;
                ShowPoseInHero(capturedPath);
                UpdateHeroCard();
            };
            poseSelector.Controls.Add(poseBtn);
        }

        ShowPoseInHero(currentPath);
    }

    private void ShowPoseInHero(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return;

        try
        {
            if (!cachedPoses.TryGetValue(imagePath, out var bmp))
            {
                bmp = new Bitmap(imagePath);
                cachedPoses[imagePath] = bmp;
            }
            heroImage.Image = bmp;
        }
        catch { }
    }

    private void EnsureTileVisible(string petId)
    {
        int index = packs.FindIndex(p => p.Id.Equals(petId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        int D(int n) => (int)Math.Round(n * DeviceDpi / 96d);
        int tileW = D(172);
        int gap = D(12);
        int cardWidth = Math.Max(D(640), ClientSize.Width - D(70));
        int tileLeft = index * (tileW + gap);
        int tileRight = tileLeft + tileW;

        if (tileLeft < collectionScrollPos)
            applyCollectionScroll?.Invoke(tileLeft);
        else if (tileRight > collectionScrollPos + cardWidth)
            applyCollectionScroll?.Invoke(tileRight - cardWidth);
    }

    private void SelectPet(string petId)
    {
        activePetId = ThatPetService.NormalizeCharacterId(petId);
        currentPreviewPose = "idle";

        // Save setting and restart ThatPet
        ThatPetService.SetActiveCharacter(activePetId);

        // Update tile highlight states
        foreach (var (id, tile) in tiles)
        {
            tile.SetActive(id.Equals(activePetId, StringComparison.OrdinalIgnoreCase));
        }

        // Scroll tile into view if partly hidden
        EnsureTileVisible(activePetId);

        // Update hero showcase
        UpdateHeroCard();

        var pack = packs.FirstOrDefault(p => p.Id.Equals(activePetId, StringComparison.OrdinalIgnoreCase));
        string name = pack?.Name ?? activePetId;
        statusLabel.Text = $"Выбран питомец: {name}. Модель успешно переключена на рабочем столе.";
        statusLabel.ForeColor = Astra.Green;
    }

    private void OpenActiveModelFolder()
    {
        var pack = packs.FirstOrDefault(p => p.Id.Equals(activePetId, StringComparison.OrdinalIgnoreCase));
        string folder = pack is not null ? Path.Combine(pack.DirectoryPath, "poses") : ThatPetService.PacksFolder;
        try
        {
            if (Directory.Exists(folder))
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex) { statusLabel.Text = "Ошибка: " + ex.Message; }
    }

    private void OpenThatPetFolder()
    {
        try
        {
            if (Directory.Exists(ThatPetService.AppFolder))
                Process.Start(new ProcessStartInfo { FileName = ThatPetService.AppFolder, UseShellExecute = true });
        }
        catch (Exception ex) { statusLabel.Text = "Ошибка открытия папки: " + ex.Message; }
    }

    private void SetPetSize(double size)
    {
        ThatPetService.SetSize(size);
        for (int i = 0; i < sizeButtons.Count; i++)
        {
            var btn = sizeButtons[i];
            bool match = btn.Text switch
            {
                "35%" => Math.Abs(size - 0.35) < 0.05,
                "50%" => Math.Abs(size - 0.5) < 0.05,
                "75%" => Math.Abs(size - 0.75) < 0.05,
                "100%" => Math.Abs(size - 1.0) < 0.05,
                _ => false
            };
            btn.Kind = match ? ButtonKind.Primary : ButtonKind.Secondary;
            btn.Invalidate();
        }
        statusLabel.Text = $"Размер питомца изменён на {(int)(size * 100)}%";
    }

    private void UpdateToggles()
    {
        bool wander = ThatPetService.GetBoolSetting("wander", true);
        bool sounds = ThatPetService.GetBoolSetting("sounds", true);
        bool bubble = ThatPetService.GetBoolSetting("bubble", true);

        wanderToggle.Text = "Бродить: " + (wander ? "Вкл" : "Выкл");
        wanderToggle.Kind = wander ? ButtonKind.Primary : ButtonKind.Secondary;

        soundsToggle.Text = "Звуки: " + (sounds ? "Вкл" : "Выкл");
        soundsToggle.Kind = sounds ? ButtonKind.Primary : ButtonKind.Secondary;

        bubbleToggle.Text = "Реплики: " + (bubble ? "Вкл" : "Выкл");
        bubbleToggle.Kind = bubble ? ButtonKind.Primary : ButtonKind.Secondary;
    }

    private void ToggleThatPetProcess()
    {
        if (ThatPetService.IsRunning)
        {
            ThatPetService.Stop();
            statusLabel.Text = "ThatPet остановлен.";
            statusLabel.ForeColor = Astra.Muted;
        }
        else
        {
            if (ThatPetService.Start())
            {
                statusLabel.Text = "ThatPet запускается…";
                statusLabel.ForeColor = Astra.Green;
            }
            else
            {
                statusLabel.Text = "Не удалось запустить ThatPet.";
                statusLabel.ForeColor = Astra.Red;
            }
        }
        UpdateProcessStatus();
        UpdateHeroCard();
    }

    private async Task TriggerTestReaction(string eventName, string successMessage)
    {
        statusLabel.Text = "Отправляем тестовое событие…";
        string msg = await ThatPetService.TriggerEventAsync(eventName);
        statusLabel.Text = msg.Contains("Ошибка") ? msg : successMessage;
    }

    private void UpdateProcessStatus()
    {
        bool running = ThatPetService.IsRunning;
        int? pid = ThatPetService.RunningPid;

        if (running)
        {
            processToggle.Text = "Остановить";
            processToggle.Kind = ButtonKind.Quiet;
            var currentPack = packs.FirstOrDefault(p => p.Id.Equals(activePetId, StringComparison.OrdinalIgnoreCase));
            string name = currentPack?.Name ?? activePetId;
            statusLabel.Text = $"● ThatPet активен (PID: {pid})  ·  Питомец на экране: {name}";
            statusLabel.ForeColor = Astra.Green;
            heroBadge.Text = "● Активен на рабочем столе";
            heroBadge.ForeColor = Astra.Green;
        }
        else
        {
            processToggle.Text = "Запустить ThatPet";
            processToggle.Kind = ButtonKind.Primary;
            statusLabel.Text = "○ ThatPet не запущен  ·  Нажмите «Запустить ThatPet»";
            statusLabel.ForeColor = Astra.Muted;
            heroBadge.Text = "○ Готов к запуску";
            heroBadge.ForeColor = Astra.Muted;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !released)
        {
            released = true;
            refreshTimer.Dispose();
            hints.Dispose();
            foreach (var img in cachedThumbnails.Values) img.Dispose();
            cachedThumbnails.Clear();
            foreach (var img in cachedPoses.Values) img.Dispose();
            cachedPoses.Clear();
        }
        base.Dispose(disposing);
    }
}

internal sealed class PetTile : Control, IButtonControl
{
    internal PetPack Pack { get; }
    private Bitmap? icon;
    private bool active;
    private bool hovered, pressed;
    [System.ComponentModel.DefaultValue(DialogResult.None)]
    public DialogResult DialogResult { get; set; }

    internal PetTile(PetPack pack, Bitmap? image, bool isActive)
    {
        Pack = pack;
        icon = image;
        active = isActive;
        BackColor = Astra.Background;
        Font = new Font("Segoe UI", 9);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleName = $"{pack.Name} ({pack.FolderName})" + (active ? " - Активен" : "");
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
    }

    internal void SetActive(bool value)
    {
        if (active == value) return;
        active = value;
        AccessibleName = $"{Pack.Name} ({Pack.FolderName})" + (active ? " - Активен" : "");
        Invalidate();
    }

    public void NotifyDefault(bool value) { }
    public void PerformClick() { if (Enabled && Visible) OnClick(EventArgs.Empty); }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Focus(); pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnMouseCaptureChanged(EventArgs e) { pressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) { pressed = true; Invalidate(); e.Handled = true; e.SuppressKeyPress = true; }
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) { bool click = pressed; pressed = false; Invalidate(); if (click) PerformClick(); e.Handled = true; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using var background = new SolidBrush(BackColor);
        g.FillRectangle(background, ClientRectangle);
        if (Width < 2 || Height < 2) return;

        float s = DeviceDpi / 96f;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var shape = Astra.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 14 * s);
        using var fill = new SolidBrush(active ? Astra.ActiveSurface : hovered || pressed ? Astra.Input : Astra.Surface);
        g.FillPath(fill, shape);

        using var pen = new Pen(active ? Astra.Accent : hovered || Focused ? Astra.Border : Color.FromArgb(40, Astra.Border), active ? 2f : 1f);
        g.DrawPath(pen, shape);

        // Pet image
        if (icon is not null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float imgSize = Math.Min(Width - 24 * s, 88 * s);
            g.DrawImage(icon, new RectangleF((Width - imgSize) / 2, 12 * s, imgSize, imgSize));
        }

        // Pet name
        using var nameFont = new Font(Font.FontFamily, 9.5f, FontStyle.Bold);
        TextRenderer.DrawText(g, Pack.Name, nameFont,
            new Rectangle((int)(6 * s), (int)(106 * s), Math.Max(1, Width - (int)(12 * s)), (int)(36 * s)),
            Astra.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // Assets badge and status
        using var smallFont = new Font(Font.FontFamily, 8f);
        if (active)
        {
            TextRenderer.DrawText(g, "● Активен", smallFont,
                new Rectangle((int)(6 * s), (int)(146 * s), Math.Max(1, Width - (int)(12 * s)), (int)(18 * s)),
                Astra.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
        }
        else
        {
            TextRenderer.DrawText(g, $"{Pack.TotalPoses} ассетов", smallFont,
                new Rectangle((int)(6 * s), (int)(146 * s), Math.Max(1, Width - (int)(12 * s)), (int)(18 * s)),
                Astra.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
        }

        using var tinyFont = new Font(Font.FontFamily, 7.5f);
        TextRenderer.DrawText(g, Pack.FolderName, tinyFont,
            new Rectangle((int)(6 * s), (int)(166 * s), Math.Max(1, Width - (int)(12 * s)), (int)(18 * s)),
            Color.FromArgb(130, Astra.Muted), TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
    }
}
