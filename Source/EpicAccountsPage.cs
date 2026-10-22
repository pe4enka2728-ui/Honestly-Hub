namespace VpnZapretSwitch;

internal sealed class EpicAccountsPage : Panel
{
    private readonly EpicAccountController controller;
    private readonly IEpicFriends friends;
    private readonly AstraPanel friendsCard = new() { Radius = 12 };
    private readonly TextBox friendName = new() { BorderStyle = BorderStyle.None, BackColor = Astra.Input, ForeColor = Astra.Text, MaxLength = 64, AccessibleName = "Точный ник друга в Epic Games" };
    private readonly AstraButton friendCancel = new() { Text = "Остановить", Visible = false, Width = 140 };
    private readonly AstraButton checkFriendStatus;
    private CancellationTokenSource? friendCancellation;
    private readonly AstraCanvas canvas = new() { BackColor = Astra.Background };
    private readonly AstraCanvas profiles = new() { BackColor = Astra.Background };
    private readonly AstraPanel settingsCard = new() { Radius = 14, Padding = new Padding(16) };
    private readonly TextBox launcher = new() { BorderStyle = BorderStyle.None, BackColor = Astra.Input, ForeColor = Astra.Text, AccessibleName = "Путь к EpicGamesLauncher.exe" };
    private readonly AstraButton autoStart = new() { Text = "Автозапуск Epic: включён", Width = 260 };
    private readonly AstraButton recover = new() { Text = "Восстановить Epic", Kind = ButtonKind.Primary, Width = 196, Visible = false };
    private readonly Label message = Astra.Label("Здесь будут профили Epic Games.", 10, Astra.Muted);
    private readonly Label localStatus = Astra.Label("Epic Games · локальные профили", 10, Astra.Muted);
    private readonly AstraLogBox journal = new() { ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, BackColor = Astra.Surface,
        ForeColor = Astra.Muted, Font = new Font("Consolas", 9), AccessibleName = "Журнал аккаунтов Epic" };
    private readonly Dictionary<string, EpicProfileCard> cards = [];
    private readonly List<Control> actions = [];
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 3000 };
    private readonly System.Windows.Forms.Timer countdownTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer sessionTimer = new() { Interval = 2000 };
    private bool sessionRefreshing, sessionNoticeShown;
    private readonly EpicFriendHistoryPanel friendHistory = new();
    private readonly AstraFlowPanel topActions = new() { WrapContents = true, Margin = Padding.Empty };
    private readonly AstraPanel statusCard = new() { Radius = 12 };
    private readonly AstraPanel journalCard = new() { Radius = 12, Padding = new Padding(14) };
    private readonly Label empty = Astra.Label("Войдите в Epic Games Launcher и нажмите «Сохранить текущий».\nДля следующего аккаунта используйте «Добавить новый».", 11, Astra.Muted);
    private bool initialized, autoStartValue = true, reading, loaded, recoveryPending;
    private string? currentId;
    private bool running;
    private EpicLoginObservation login = EpicLoginObservation.Unknown;
    internal bool IsBusy { get; private set; }
    internal EpicAccountsPage(EpicAccountController? controller = null, IEpicFriends? friends = null)
    {
        this.controller = controller ?? new(new EpicProfileStore(), new EpicWindows());
        this.friends = friends ?? new EpicDesktopFriends();
        Dock = DockStyle.Fill; BackColor = Astra.Background; Margin = Padding.Empty;
        SetStyle(ControlStyles.Selectable, true); TabStop = true;
        settingsCard.Size = new Size(700, 192); friendsCard.Size = new Size(700, 178);
        var title = Astra.Label("Аккаунты", 24, style: FontStyle.Bold); title.Location = new Point(0, 0);
        localStatus.AutoSize = false; localStatus.SetBounds(1, 47, 700, 25); localStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        canvas.Controls.Add(title); canvas.Controls.Add(localStatus);
        var pathCaption = Astra.Label("Epic Games Launcher", 11, style: FontStyle.Bold); pathCaption.Location = new Point(16, 14); settingsCard.Controls.Add(pathCaption);
        var input = new AstraPanel { Fill = Astra.Input, Radius = 8, Location = new Point(16, 45), Size = new Size(430, 37), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Padding = new Padding(11, 9, 11, 6) };
        launcher.Dock = DockStyle.Fill; input.Controls.Add(launcher); settingsCard.Controls.Add(input); actions.Add(launcher);
        var browse = Button("Обзор…", 90, (_, _) => BrowseLauncher()); browse.SetBounds(456, 45, 90, 37); browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        var apply = Button("Сохранить", 110, async (_, _) => await RunAsync(async () => { await this.controller.ConfigureAsync(launcher.Text, autoStartValue); return "Настройки Epic сохранены."; }));
        apply.SetBounds(556, 45, 110, 37); apply.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        settingsCard.Controls.Add(browse); settingsCard.Controls.Add(apply);
        autoStart.SetBounds(16, 94, 260, 35); actions.Add(autoStart); settingsCard.Controls.Add(autoStart);
        autoStart.Click += (_, _) => { autoStartValue = !autoStartValue; SetAutoStartText(); };
        var open = Button("Открыть Epic", 160, async (_, _) => await RunAsync(async () => { await this.controller.ConfigureAsync(launcher.Text, autoStartValue); await this.controller.OpenAsync(); return "Лаунчер Epic запущен."; }));
        open.SetBounds(288, 94, 160, 35); settingsCard.Controls.Add(open);
        var tools = new AstraFlowPanel { Location = new Point(16, 142), Size = new Size(650, 38), Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right, WrapContents = false };
        tools.Controls.Add(Button("Импорт TcNo", 142, async (_, _) => await ImportTcNoAsync()));
        tools.Controls.Add(Button("Резервная копия", 170, async (_, _) => await ExportAsync()));
        tools.Controls.Add(Button("Импорт копии", 152, async (_, _) => await ImportBackupAsync()));
        settingsCard.Controls.Add(tools); canvas.Controls.Add(settingsCard);
        topActions.Controls.Add(Button("Сохранить текущий", 212, async (_, _) => await SaveCurrentAsync(), ButtonKind.Primary));
        topActions.Controls.Add(Button("Добавить новый", 190, async (_, _) => await SwitchAsync(null)));
        topActions.Controls.Add(Button("Обновить", 126, async (_, _) => await ReloadAsync()));
        recover.Click += async (_, _) => await RunAsync(async () => { await this.controller.RecoverAsync(); return "Прежние локальные данные Epic восстановлены. Лаунчер можно открыть вручную."; });
        topActions.Controls.Add(recover); canvas.Controls.Add(topActions);
        var friendTitle = Astra.Label("Добавить друга со всех аккаунтов", 12, style: FontStyle.Bold);
        friendTitle.Location = new Point(16, 12); friendsCard.Controls.Add(friendTitle);
        var checkSearch = Button("Проверить поиск", 174, async (_, _) => await CheckFriendSearchAsync(), ButtonKind.Quiet);
        checkSearch.SetBounds(510, 8, 174, 34); checkSearch.Anchor = AnchorStyles.Top | AnchorStyles.Right; friendsCard.Controls.Add(checkSearch);
        var friendHint = Astra.Label("Введите точный ник Epic. Во время обработки окно Epic будет активно.", 9, Astra.Muted);
        friendHint.AutoSize = false; friendHint.SetBounds(16, 40, 650, 30); friendHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        friendsCard.Controls.Add(friendHint);
        var friendInput = new AstraPanel { Fill = Astra.Input, Radius = 8, Location = new Point(16, 74), Size = new Size(650, 37), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Padding = new Padding(11, 9, 11, 6) };
        friendName.Dock = DockStyle.Fill; friendInput.Controls.Add(friendName); friendsCard.Controls.Add(friendInput); actions.Add(friendName);
        var sendFriends = Button("Добавить со всех аккаунтов", 286, async (_, _) => await AddFriendsAsync(), ButtonKind.Primary);
        sendFriends.Location = new Point(16, 124); friendsCard.Controls.Add(sendFriends);
        checkFriendStatus=Button("Проверить заявку",226,async (_,_)=>await CheckFriendStatusAsync(),ButtonKind.Quiet);
        checkFriendStatus.Location=new Point(314,124); friendsCard.Controls.Add(checkFriendStatus);
        friendCancel.Location = new Point(314, 124); friendsCard.Controls.Add(friendCancel);
        friendCancel.Click += (_, _) => { friendCancellation?.Cancel(); friendCancel.Enabled = false; friendCancel.Text = "Останавливаем…"; };
        canvas.Controls.Add(friendsCard);
        message.AutoSize = false; message.Dock = DockStyle.Fill; message.Padding = new Padding(16, 12, 16, 8); statusCard.Controls.Add(message); canvas.Controls.Add(statusCard);
        canvas.Controls.Add(friendHistory);
        friendHistory.DeleteRequested+=async key=>await RunAsync(async ()=>
        {await this.controller.RemoveTrackedFriendAsync(key);return "Ник удалён из списка таймеров.";});
        empty.AutoSize = false; empty.SetBounds(16, 14, 650, 64); profiles.Controls.Add(empty); canvas.Controls.Add(profiles);
        journal.Dock = DockStyle.Fill; journalCard.Controls.Add(new AstraScrollHost(journal, Astra.Surface)); canvas.Controls.Add(journalCard);
        Controls.Add(new AstraScrollHost(canvas, Astra.Background, alwaysVisible: false));
        canvas.Resize += (_, _) => Reflow();
        VisibleChanged += async (_, _) =>
        {
            if (Visible) { if (!loaded) await ReloadAsync();if(IsDisposed || !Visible) return; timer.Start(); countdownTimer.Start();friendHistory.Tick(DateTimeOffset.UtcNow); }
            else {timer.Stop();countdownTimer.Stop();}
        };
        timer.Tick += async (_, _) => await RefreshLocalAsync();
        countdownTimer.Tick+=(_,_)=>friendHistory.Tick(DateTimeOffset.UtcNow);
        sessionTimer.Tick += async (_, _) => await FlushSessionAsync();
        sessionTimer.Start(); // Keep rotated Epic credentials even while another tab is visible.
        initialized = true; Reflow();
    }
    internal async Task FlushSessionAsync()
    {
        if (sessionRefreshing || IsBusy || IsDisposed) return;
        sessionRefreshing = true;
        try
        {
            bool saved = await Task.Run(controller.RefreshSavedSessionAsync);
            if (saved && !IsDisposed) { Log("Epic: обновлённые данные подтверждённого входа сохранены автоматически."); sessionNoticeShown = false; }
        }
        catch
        {
            if (!sessionNoticeShown && !IsDisposed)
            { Log("Epic: не удалось обновить сохранённый вход. Прежняя копия сохранена; проверьте доступ к хранилищу профилей."); sessionNoticeShown = true; }
        }
        finally { sessionRefreshing = false; }
    }
    internal async Task FlushSessionBeforeCloseAsync()
    {
        while (sessionRefreshing) await Task.Delay(50);
        await FlushSessionAsync();
    }
    private AstraButton Button(string text, int width, EventHandler click, ButtonKind kind = ButtonKind.Secondary)
    {
        var button = new AstraButton { Text = text, AccessibleName = text, Kind = kind, Width = width, Height = 36, Margin = new Padding(0, 0, 10, 10) };
        button.Click += click; actions.Add(button); return button;
    }
    private void Reflow()
    {
        if (!initialized) return;
        int D(int n) => (int)Math.Round(n * DeviceDpi / 96d);
        int width = Math.Max(D(600), canvas.ClientSize.Width);
        localStatus.Width = width;
        settingsCard.SetBounds(0, D(82), width, D(192));
        topActions.SetBounds(0, D(290), width, D(recoveryPending ? 94 : 48));
        int top = topActions.Bottom + D(8); friendsCard.SetBounds(0, top, width, D(178));
        top = friendsCard.Bottom + D(12); statusCard.SetBounds(0, top, width, D(90)); top = statusCard.Bottom + D(16);
        friendHistory.SetBounds(0,top,width,D(friendHistory.PreferredHeight));top=friendHistory.Bottom+D(16);
        profiles.SetBounds(0, top, width, D(Math.Max(92, cards.Count * 130)));
        int row = 0;
        foreach (var card in cards.Values.OrderBy(card => card.TabIndex)) card.SetBounds(0, D(row++ * 130), width, D(118));
        empty.Width = Math.Max(1, width - D(32));
        journalCard.SetBounds(0, profiles.Bottom + D(16), width, D(150));
        canvas.MinimumSize = new Size(0, journalCard.Bottom + D(16));
    }
    private async Task ReloadAsync()
    {
        if (IsBusy || reading || IsDisposed) return;
        reading = true;
        try
        {
            var library = await Task.Run(controller.ReadLibrary);
            if (IsDisposed) return;
            if (!loaded)
            {
                launcher.Text = library.LauncherPath;
                if (launcher.Text.Length == 0)
                    try { launcher.Text = await Task.Run(() => controller.Platform.ResolveLauncher("")); } catch { }
                if (IsDisposed) return;
                autoStartValue = library.AutoStart; SetAutoStartText(); loaded = true;
            }
            SyncCards(library);
            try
            {
                var history=await Task.Run(controller.ReadFriendHistory);
                if(IsDisposed) return;
                friendHistory.Update(history,library.Profiles.Select(p=>p.AccountId).ToArray(),DateTimeOffset.UtcNow);
            }
            catch {if(!IsDisposed) friendHistory.SetError();}
            bool pending = controller.Store.NeedsRecovery; recoveryPending = pending; recover.Visible = pending;
            if (pending) SetMessage("Незавершённое переключение: нажмите «Восстановить Epic». Прежние данные сохранены в зашифрованной копии.", error: true);
            else if (cards.Count == 0) SetMessage("Сначала войдите в Epic и сохраните текущий профиль. Первый вход и подтверждение выполняются в самом лаунчере.");
            else if (message.Text == "Здесь будут профили Epic Games.") SetMessage(library.Profiles.Any(profile => !profile.HasRememberedLogin)
                ? "Есть профили с неполными данными входа. Войдите в них в Epic с опцией «Запомнить меня» и нажмите «Сохранить текущий»."
                : "Выберите профиль для переключения. Полноценные данные текущего входа сохраняются перед заменой. Для нового аккаунта используйте «Добавить новый», без выхода из аккаунта в Epic.");
            Reflow();
        }
        catch (Exception ex) { SetMessage(Explain(ex), error: true); }
        finally { reading = false; }
        await RefreshLocalAsync();
    }
    private void SyncCards(EpicLibrary library)
    {
        foreach (var key in cards.Keys.Except(library.Profiles.Select(profile => profile.Key)).ToArray())
        { var card = cards[key]; profiles.Controls.Remove(card); card.Dispose(); cards.Remove(key); }
        int order = 0;
        foreach (var profile in library.Profiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!cards.TryGetValue(profile.Key, out var card))
            {
                card = new EpicProfileCard(); cards.Add(profile.Key, card); profiles.Controls.Add(card);
                var item = card;
                item.SwitchRequested += async () => await SwitchAsync(item.Profile!.Key);
                item.RenameRequested += async () => await RenameAsync(item.Profile!);
                item.RemoveRequested += async () => await RemoveAsync(item.Profile!);
            }
            card.TabIndex = order++; card.Update(profile, currentId, running, login); card.SetActionsEnabled(!IsBusy);
        }
        empty.Visible = cards.Count == 0;
    }
    private async Task RefreshLocalAsync()
    {
        if (reading || IsBusy || IsDisposed || !Visible) return;
        reading = true;
        try
        {
            var state = await Task.Run(() => (Id: controller.Platform.CurrentAccountId, Running: controller.Platform.IsRunning, Login: controller.Platform.ReadLogin()));
            if (IsDisposed) return;
            currentId = state.Id; running = state.Running; login = state.Login;
            localStatus.Text = "Epic Games · " + (running ? login.Matches(currentId) ? "вход подтверждён" : login.Status is EpicLoginStatus.Rejected or EpicLoginStatus.Failed ? "требуется вход" : "ожидаем подтверждение входа" : "лаунчер закрыт") + " · профилей: " + cards.Count;
            foreach (var card in cards.Values) card.Update(card.Profile!, currentId, running, login);
        }
        catch { if (!IsDisposed) localStatus.Text = "Epic Games · состояние лаунчера недоступно"; }
        finally { reading = false; }
    }
    private async Task RunAsync(Func<Task<string>> action)
    {
        if (IsBusy || IsDisposed) return;
        IsBusy = true; Focus(); SetEnabled(false); SetMessage("Выполняем операцию Epic…");
        try
        {
            while (sessionRefreshing) await Task.Delay(25);
            string result = await Task.Run(action);
            if (!IsDisposed) { SetMessage(result); Log(result); }
        }
        catch (Exception ex) { if (!IsDisposed) { string note = Explain(ex); SetMessage(note, true); Log("Ошибка: " + note); } }
        finally
        {
            IsBusy = false;
            if (!IsDisposed) { SetEnabled(true); await ReloadAsync(); }
        }
    }
    private void SetEnabled(bool enabled)
    {
        foreach (var action in actions) action.Enabled = enabled;
        recover.Enabled = enabled;
        foreach (var card in cards.Values) card.SetActionsEnabled(enabled);
        friendHistory.SetBusy(!enabled);
    }
    private void SetAutoStartText() { autoStart.Text = "Автозапуск Epic: " + (autoStartValue ? "включён" : "выключен"); autoStart.Invalidate(); }
    private void SetMessage(string text, bool error = false) { message.Text = text; message.ForeColor = error ? Astra.Red : Astra.Muted; }
    private void Log(string text)
    {
        if (journal.TextLength > 24000) journal.Clear();
        journal.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine + Environment.NewLine);
    }
    private static string Explain(Exception ex) => ex is OperationCanceledException ? "Операция отменена." : ex is System.Text.Json.JsonException ? "Файл профилей имеет неверный формат. Исходные файлы Epic не изменены." : ex.Message;
    private void BrowseLauncher()
    {
        using var dialog = new OpenFileDialog { Filter = "Epic Games Launcher|EpicGamesLauncher.exe", Title = "Выберите EpicGamesLauncher.exe", CheckFileExists = true, RestoreDirectory = true };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) launcher.Text = dialog.FileName;
    }
    private async Task SaveCurrentAsync()
    {
        string? name = EpicPrompt.Ask(FindForm(), "Сохранить текущий", "Сначала войдите в Epic с опцией «Запомнить меня» и дождитесь библиотеки. Задайте имя профиля. Пароль вводится только в Epic.", "Мой Epic");
        if (name is null) return;
        await RunAsync(async () => { await controller.SaveCurrentAsync(name); return "Профиль сохранён. Его доступность подтверждает сам Epic при входе."; });
    }
    private async Task SwitchAsync(string? key)
    {
        if (IsBusy) return;
        string text = key is null ? "Лаунчер Epic будет закрыт для нового входа. Полноценные данные текущего входа будут сохранены. Войдите с опцией «Запомнить меня», дождитесь библиотеки и сохраните новый профиль." : "Лаунчер Epic будет закрыт для смены профиля. Завершите операции в лаунчере перед переключением. Автозапуск зависит от выбранной настройки.";
        if (!EpicPrompt.Confirm(FindForm(), key is null ? "Добавить аккаунт Epic" : "Сменить аккаунт Epic", text, "Продолжить")) return;
        // Persist the visible settings before the transaction; validation happens before launcher shutdown.
        await RunAsync(async () => { await controller.ConfigureAsync(launcher.Text, autoStartValue); return await controller.SwitchAsync(key); });
    }
    private async Task AddFriendsAsync()
    {
        if (IsBusy) return;
        string nickname;
        try { nickname = EpicFriendNames.Validate(friendName.Text); }
        catch (Exception ex) { SetMessage(ex.Message, true); return; }
        EpicLibrary library;
        try { library = await Task.Run(controller.ReadLibrary); }
        catch (Exception ex) { SetMessage(Explain(ex), true); return; }
        if (library.Profiles.Length == 0) { SetMessage("Сначала сохраните аккаунты Epic.", true); return; }
        if (!EpicPrompt.Confirm(FindForm(), "Заявки в друзья Epic", "Отправить заявки игроку «" + nickname + "» со всех сохранённых аккаунтов (" + library.Profiles.Length + ")? Неполные профили будут пропущены. Затем вернётся исходный аккаунт.", "Отправить заявки")) return;
        using var cancellation = new CancellationTokenSource(); SetFriendCancellation(cancellation);
        IProgress<EpicFriendProgress> progress = new Progress<EpicFriendProgress>(item =>
        {
            if (IsDisposed) return;
            string note = (item.Index > 0 ? "[" + item.Index + "/" + item.Total + "] " : "") + item.ProfileName + ": " + item.Detail;
            SetMessage(note); Log(note);
        });
        try
        {
            await RunAsync(async () =>
            {
                await controller.ConfigureAsync(launcher.Text, autoStartValue);
                var result = await controller.AddFriendFromAllAsync(nickname, friends, progress, cancellation.Token);
                foreach (var item in result.Items) progress.Report(new(0, result.Items.Length, item.ProfileName, item.Detail));
                if (!result.Restored) throw new InvalidOperationException(result.Summary);
                return result.Summary;
            });
        }
        finally { SetFriendCancellation(null); }
    }
    private async Task CheckFriendSearchAsync()
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource(); SetFriendCancellation(cancellation);
        string configured = launcher.Text;
        try { await RunAsync(() => controller.CheckFriendSearchAsync(friends, configured, cancellation.Token)); }
        finally { SetFriendCancellation(null); }
    }
    private async Task CheckFriendStatusAsync()
    {
        if(IsBusy) return;
        string nickname;
        try { nickname=EpicFriendNames.Validate(friendName.Text); }
        catch(Exception ex) { SetMessage(ex.Message,true); return; }
        string configured=launcher.Text;
        using var cancellation=new CancellationTokenSource(); SetFriendCancellation(cancellation);
        try { await RunAsync(()=>controller.CheckFriendStatusAsync(friends,configured,nickname,cancellation.Token)); }
        finally { SetFriendCancellation(null); }
    }
    private void SetFriendCancellation(CancellationTokenSource? cancellation)
    {
        friendCancellation=cancellation; friendCancel.Visible=cancellation is not null;
        friendCancel.Enabled=true; friendCancel.Text="Остановить"; checkFriendStatus.Visible=cancellation is null;
    }
    private async Task RenameAsync(EpicProfile profile)
    {
        if (IsBusy) return;
        string? name = EpicPrompt.Ask(FindForm(), "Имя профиля Epic", "Изменяется только подпись в Honestly TUN.", profile.Name);
        if (name is null) return;
        await RunAsync(async () => { await controller.RenameAsync(profile.Key, name); return "Имя профиля изменено."; });
    }
    private async Task RemoveAsync(EpicProfile profile)
    {
        if (IsBusy || !EpicPrompt.Confirm(FindForm(), "Убрать профиль", "Удалить сохранённый профиль «" + profile.Name + "» из Honestly TUN? Текущий вход в Epic сохранится.", "Удалить")) return;
        await RunAsync(async () => { await controller.RemoveAsync(profile.Key); return "Сохранённый профиль удалён из Honestly TUN."; });
    }
    private async Task ImportTcNoAsync()
    {
        using var dialog = new FolderBrowserDialog { Description = @"Выберите папку TcNo Account Switcher\LoginCache\Epic Games, содержащую ids.json", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        await RunAsync(async () => "Импортировано профилей TcNo: " + await controller.ImportTcNoAsync(dialog.SelectedPath) + ". Исходная папка сохранена.");
    }
    private async Task ExportAsync()
    {
        using var dialog = new SaveFileDialog { Filter = "Копия профилей Honestly TUN|*.ht-epic", FileName = "Honestly-Epic-" + DateTime.Now.ToString("yyyy-MM-dd") + ".ht-epic", RestoreDirectory = true };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        await RunAsync(async () => { await controller.ExportAsync(dialog.FileName); return "Копия создана. Для её открытия нужен этот же пользователь Windows на этом компьютере."; });
    }
    private async Task ImportBackupAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "Копия профилей Honestly TUN|*.ht-epic", CheckFileExists = true, RestoreDirectory = true };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        await RunAsync(async () => "Импортировано профилей из копии: " + await controller.ImportBackupAsync(dialog.FileName) + ". Данные в Epic не изменялись.");
    }
    protected override void Dispose(bool disposing) { if (disposing) { friendCancellation?.Cancel(); timer.Dispose(); countdownTimer.Dispose(); sessionTimer.Dispose(); } base.Dispose(disposing); }
}

internal sealed class EpicProfileCard : AstraPanel
{
    private readonly Label name = Astra.Label("", 12, style: FontStyle.Bold);
    private readonly Label description = Astra.Label("", 9, Astra.Muted);
    private readonly Label state = Astra.Label("", 9, Astra.Green);
    internal EpicProfile? Profile { get; private set; }
    internal event Func<Task>? SwitchRequested, RenameRequested, RemoveRequested;
    internal EpicProfileCard()
    {
        Radius = 12;
        name.AutoSize = description.AutoSize = state.AutoSize = false;
        Controls.Add(name); Controls.Add(description); Controls.Add(state);
        int x = 16;
        foreach (var item in new[] { ("Переключить", 160, ButtonKind.Primary), ("Переименовать", 154, ButtonKind.Quiet), ("Удалить", 108, ButtonKind.Quiet) })
        {
            var button = new AstraButton { Text = item.Item1, Width = item.Item2, Height = 32, Kind = item.Item3, Location = new Point(x, 74) };
            x += item.Item2 + 8; Controls.Add(button);
            button.Click += async (_, _) =>
            {
                var handler = item.Item1 == "Переключить" ? SwitchRequested : item.Item1 == "Переименовать" ? RenameRequested : RemoveRequested;
                if (handler is not null) await handler();
            };
        }
        Resize += (_, _) => Arrange();
    }
    private void Arrange()
    {
        int D(int value) => (int)Math.Round(value * DeviceDpi / 96d);
        name.SetBounds(D(16), D(10), Math.Max(1, Width - D(238)), D(28)); name.AutoEllipsis = true;
        description.SetBounds(D(16), D(40), Math.Max(1, Width - D(32)), D(25)); description.AutoEllipsis = true;
        state.SetBounds(Math.Max(D(16), Width - D(206)), D(14), D(190), D(26)); state.TextAlign = ContentAlignment.MiddleRight;
    }
    internal void Update(EpicProfile profile, string? currentId, bool running, EpicLoginObservation? login = null)
    {
        Profile = profile; bool complete = profile.HasRememberedLogin;
        bool current = complete && profile.AccountId.Equals(currentId, StringComparison.OrdinalIgnoreCase);
        name.Text = profile.Name; description.Text = "ID …" + profile.AccountId[^8..] + "  ·  Сохранён " + profile.SavedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        bool authenticated = current && running && login?.Matches(currentId) == true;
        bool rejected = current && running && login?.Status is EpicLoginStatus.Rejected or EpicLoginStatus.Failed;
        string label = !complete ? "Обновите вход в Epic" : rejected ? "Нужен вход в Epic" : authenticated ? "Вход подтверждён" : current ? "Выбран локально" : "Сохранён";
        state.Text = label; state.ForeColor = !complete || rejected ? Astra.Red : authenticated ? Astra.Green : Astra.Muted;
        Fill = authenticated ? Astra.ActiveSurface : Astra.Surface;
        foreach (var control in new[] { name, description, state }) control.BackColor = Fill;
        string accessible = "Профиль Epic: " + profile.Name + ". " + label;
        if (AccessibleName != accessible) { AccessibleName = accessible; if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.NameChange, -1); }
        Arrange(); Invalidate(invalidateChildren: true);
    }
    internal void SetActionsEnabled(bool enabled)
    { foreach (var button in Controls.OfType<AstraButton>()) button.Enabled = enabled; }
}

internal sealed class EpicPrompt : Form
{
    private readonly TextBox input = new() { BorderStyle = BorderStyle.None, BackColor = Astra.Input, ForeColor = Astra.Text, MaxLength = 64, Dock = DockStyle.Fill };
    private EpicPrompt(string title, string description, string? value, string action)
    {
        Text = title; Icon = HonestlyBrand.LoadIcon(); Font = new Font("Segoe UI", 10); ForeColor = Astra.Text; BackColor = Astra.Background;
        FormBorderStyle = FormBorderStyle.None; ClientSize = new Size(580, 288); Padding = new Padding(6); StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        var body = new AstraCanvas { Dock = DockStyle.Fill, Padding = new Padding(22, 14, 22, 18), BackColor = Astra.Background };
        var explanation = Astra.Label(description, 10, Astra.Muted); explanation.AutoSize = false; explanation.SetBounds(22, 16, 524, 70); body.Controls.Add(explanation);
        if (value is not null)
        {
            var field = new AstraPanel { Fill = Astra.Input, Radius = 8, Location = new Point(22, 92), Size = new Size(524, 42), Padding = new Padding(12, 10, 12, 8) };
            input.Text = value; input.AccessibleName = "Имя профиля Epic"; field.Controls.Add(input); body.Controls.Add(field);
        }
        var ok = new AstraButton { Text = action, Kind = ButtonKind.Primary, Location = new Point(22, 158), Size = new Size(174, 40) };
        ok.Click += (_, _) => { if (value is not null) try { _ = EpicProfileStore.CleanName(input.Text); } catch (Exception ex) { explanation.Text = ex.Message; return; } DialogResult = DialogResult.OK; Close(); };
        var cancel = new AstraButton { Text = "Отмена", Location = new Point(208, 158), Size = new Size(130, 40), DialogResult = DialogResult.Cancel };
        body.Controls.Add(ok); body.Controls.Add(cancel); Controls.Add(body); Controls.Add(new AstraTitleBar(this)); AcceptButton = ok; CancelButton = cancel;
        Shown += (_, _) => { if (value is not null) { input.Focus(); input.SelectAll(); } };
    }
    internal static string? Ask(Form? owner, string title, string description, string value)
    {
        using var prompt = new EpicPrompt(title, description, value, "Сохранить");
        return prompt.ShowDialog(owner) == DialogResult.OK ? EpicProfileStore.CleanName(prompt.input.Text) : null;
    }
    internal static bool Confirm(Form? owner, string title, string description, string action)
    {
        using var prompt = new EpicPrompt(title, description, null, action); return prompt.ShowDialog(owner) == DialogResult.OK;
    }
}
