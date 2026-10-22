namespace VpnZapretSwitch;

internal interface IEpicPlatform
{
    EpicLocalState Capture();
    string? CurrentAccountId { get; }
    void Apply(EpicLocalState state);
    string ResolveLauncher(string configured);
    bool IsRunning { get; }
    Task StopAsync(string launcher);
    Task StartAsync(string launcher);
    EpicLoginObservation ReadLogin() => EpicLoginObservation.Unknown;
}
internal sealed class EpicAccountController(EpicProfileStore store, IEpicPlatform platform, Func<DateTimeOffset>? clock = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    internal EpicProfileStore Store => store;
    internal IEpicPlatform Platform => platform;
    internal EpicLibrary ReadLibrary() => store.Load();
    internal EpicFriendHistory ReadFriendHistory() => store.LoadFriendHistory();
    internal Task RemoveTrackedFriendAsync(string key) => Operation(async () =>
    {
        store.SaveFriendHistory(store.LoadFriendHistory().Remove(key));await Task.CompletedTask;return true;
    },allowRecovery:true);
    private async Task<T> Operation<T>(Func<Task<T>> action, bool allowRecovery = false)
    {
        if (!await gate.WaitAsync(0).ConfigureAwait(false)) throw new InvalidOperationException("Дождитесь завершения операции Epic.");
        try
        {
            using var locked = store.Lock();
            if (store.NeedsRecovery && !allowRecovery) throw new InvalidOperationException("Сначала восстановите Epic после незавершённого переключения.");
            return await action().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    internal Task SaveCurrentAsync(string name) => Operation(async () =>
    {
        var library = store.Load(); var state = await CaptureStableAsync().ConfigureAwait(false);
        RequireAuthenticated(state.AccountId);
        store.Save(EpicProfileStore.Upsert(library, EpicProfileStore.FromState(state, name)));
        await Task.CompletedTask; return true;
    });
    // This runs on every tab. It only refreshes an existing profile after Epic itself
    // confirms the same account; it never opens/closes the launcher or restores files.
    internal async Task<bool> RefreshSavedSessionAsync()
    {
        if (!await gate.WaitAsync(0).ConfigureAwait(false)) return false;
        try
        {
            using var locked = store.Lock();
            if (store.NeedsRecovery) return false;
            return await SaveAuthenticatedSessionAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    private async Task<bool> SaveAuthenticatedSessionAsync(string? expectedId = null)
    {
        var state = await CaptureStableAsync().ConfigureAwait(false);
        if (!EpicRememberedLogin.CanRestore(state.ActiveIni) || !platform.ReadLogin().Matches(state.AccountId) ||
            expectedId is not null && !string.Equals(expectedId, state.AccountId, StringComparison.OrdinalIgnoreCase)) return false;
        var library = store.Load();
        var known = library.Profiles.FirstOrDefault(p => p.AccountId.Equals(state.AccountId, StringComparison.OrdinalIgnoreCase));
        if (known is null || known.Ini.AsSpan().SequenceEqual(state.ActiveIni)) return false;
        store.Save(EpicProfileStore.Upsert(library, EpicProfileStore.FromState(state, known.Name, known.Key)));
        return true;
    }
    private void RequireAuthenticated(string? accountId)
    {
        var login = platform.ReadLogin();
        if (login.Matches(accountId)) return;
        throw new InvalidOperationException(login.Status == EpicLoginStatus.Rejected
            ? "Epic отклонил сохранённый вход (invalid_refresh_token). Войдите в нужный аккаунт вручную с опцией «Запомнить меня», дождитесь библиотеки и сохраните профиль заново. Прежняя копия не заменена."
            : "Epic ещё не подтвердил вход в этот аккаунт. Дождитесь библиотеки после ручного входа и повторите сохранение. Прежняя копия не заменена.");
    }
    private async Task ConfirmAndSaveLoginAsync(string accountId, CancellationToken cancellation = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            var login = platform.ReadLogin();
            if (login.Matches(accountId))
            {
                // The launcher writes RememberMe after OnLoginComplete, not at process startup.
                await Task.Delay(750, cancellation).ConfigureAwait(false);
                await SaveAuthenticatedSessionAsync(accountId).ConfigureAwait(false);
                var confirmed = platform.Capture();
                if (platform.ReadLogin().Matches(accountId) && string.Equals(confirmed.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
                    EpicRememberedLogin.CanRestore(confirmed.ActiveIni)) return;
            }
            if (login.Status is EpicLoginStatus.Rejected or EpicLoginStatus.Failed)
            { RequireAuthenticated(accountId); }
            if (login.Status == EpicLoginStatus.Authenticated && !login.Matches(accountId))
                throw new InvalidOperationException("Epic вошёл в другой аккаунт. Операция остановлена, сохранённые профили не заменены.");
            await Task.Delay(350, cancellation).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Epic не подтвердил вход за 45 секунд. Проверьте окно лаунчера. Если открыт экран входа, войдите вручную с опцией «Запомнить меня» и сохраните профиль заново.");
    }
    internal Task ConfigureAsync(string path, bool autoStart) => Operation(async () =>
    {
        string launcher = platform.ResolveLauncher(path);
        store.Save(store.Load() with { LauncherPath = launcher, AutoStart = autoStart });
        await Task.CompletedTask; return true;
    });
    internal Task RenameAsync(string key, string name) => Operation(async () =>
    {
        name = EpicProfileStore.CleanName(name); var library = store.Load();
        if (!library.Profiles.Any(profile => profile.Key == key)) throw new InvalidOperationException("Профиль больше не существует.");
        store.Save(library with { Profiles = library.Profiles.Select(profile => profile.Key == key ? profile with { Name = name } : profile).ToArray() });
        await Task.CompletedTask; return true;
    });
    internal Task RemoveAsync(string key) => Operation(async () =>
    {
        var library = store.Load(); store.Save(library with { Profiles = library.Profiles.Where(profile => profile.Key != key).ToArray() });
        await Task.CompletedTask; return true;
    });
    internal Task<int> ImportTcNoAsync(string folder) => Operation(async () =>
    {
        var profiles = EpicProfileStore.ReadTcNo(folder); var library = store.Load();
        foreach (var profile in profiles) library = EpicProfileStore.Upsert(library, profile);
        store.Save(library); await Task.CompletedTask; return profiles.Length;
    });
    internal Task<int> ImportBackupAsync(string path) => Operation(async () =>
    {
        var imported = store.ReadBackup(path); var library = store.Load();
        foreach (var profile in imported.Profiles) library = EpicProfileStore.Upsert(library, profile);
        store.Save(library); await Task.CompletedTask; return imported.Profiles.Length;
    });
    internal Task ExportAsync(string path) => Operation(async () => { store.Export(path); await Task.CompletedTask; return true; }, allowRecovery: true);
    internal Task<string> SwitchAsync(string? key) => Operation(() => SwitchCoreAsync(key));
    private async Task<string> SwitchCoreAsync(string? key, bool forceStart = false, EpicRecovery? batchRecovery = null, CancellationToken cancellation = default)
    {
        var library = store.Load();
        EpicProfile? target = key is null ? null : library.Profiles.SingleOrDefault(profile => profile.Key == key)
            ?? throw new InvalidOperationException("Выбранный профиль больше не существует.");
        if (target is not null && !target.HasRememberedLogin)
            throw new InvalidOperationException("В профиле «" + target.Name + "» нет полноценного сохранённого входа. Войдите в этот аккаунт в Epic с опцией «Запомнить меня» и нажмите «Сохранить текущий». Лаунчер и текущий вход не изменены.");
        string launcher = platform.ResolveLauncher(library.LauncherPath); // Validate before closing or changing anything.
        var beforeStop = await CaptureStableAsync().ConfigureAwait(false);
        var beforeLogin = platform.ReadLogin();
        await platform.StopAsync(launcher).ConfigureAwait(false);
        var original = platform.Capture(); EpicProfileStore.ValidateState(original);
        // Closing a launcher can flush a logged-out placeholder. Never replace a useful saved
        // login with that placeholder; keep a complete pre-close snapshot of the same account.
        var currentState = EpicRememberedLogin.CanRestore(original.ActiveIni) ? original :
            string.Equals(beforeStop.AccountId, original.AccountId, StringComparison.OrdinalIgnoreCase) && EpicRememberedLogin.CanRestore(beforeStop.ActiveIni) ? beforeStop : original;
        bool autoSaved = false;
        if (beforeLogin.Matches(currentState.AccountId) && EpicRememberedLogin.CanRestore(currentState.ActiveIni))
        {
            var current = library.Profiles.FirstOrDefault(profile => profile.AccountId.Equals(currentState.AccountId, StringComparison.OrdinalIgnoreCase));
            string name = current?.Name ?? "Epic · " + currentState.AccountId![^8..];
            library = EpicProfileStore.Upsert(library, EpicProfileStore.FromState(currentState, name, current?.Key));
            store.Save(library); autoSaved = current is null;
            // When selecting the current profile, keep the freshly captured authorization data.
            if (target?.AccountId.Equals(currentState.AccountId, StringComparison.OrdinalIgnoreCase) == true)
            {
                string targetId = target.AccountId;
                target = library.Profiles.Single(profile => profile.AccountId.Equals(targetId, StringComparison.OrdinalIgnoreCase));
            }
        }
        var desired = EpicProfileStore.Target(original, target);
        store.SaveRecovery(batchRecovery ?? new(1, currentState, launcher));
        try
        {
            platform.Apply(desired); Verify(desired);
            if (library.AutoStart || forceStart) await platform.StartAsync(launcher).ConfigureAwait(false);
            if (batchRecovery is null) store.ClearRecovery();
        }
        catch (Exception failure)
        {
            try
            {
                // A partially started launcher must stop before restoring its files.
                await platform.StopAsync(launcher).ConfigureAwait(false);
                platform.Apply(currentState); Verify(currentState);
                if (batchRecovery is null) store.ClearRecovery();
            }
            catch
            {
                throw new InvalidOperationException("Переключение Epic не завершено; автоматическое восстановление не удалось. Резервное состояние сохранено. Нажмите «Восстановить Epic» перед следующим переключением.");
            }
            throw new InvalidOperationException("Переключение Epic отменено, прежние локальные данные восстановлены. " + SafeReason(failure));
        }
        // A rejected authorization is not a failed file transaction. Leave this account's
        // login window open for manual recovery instead of restoring another stale token.
        if (target is not null && (library.AutoStart || forceStart))
            await ConfirmAndSaveLoginAsync(target.AccountId, cancellation).ConfigureAwait(false);
        return (autoSaved ? "Текущий аккаунт автоматически сохранён. " : "") +
            (target is null ? "Подготовлен новый вход. Войдите в Epic и нажмите «Сохранить текущий»." : "Профиль «" + target.Name + "» подготовлен. ") +
            (library.AutoStart || forceStart ? target is null ? " Лаунчер запущен для ручного входа." : " Epic подтвердил вход; обновлённые данные сохраняются автоматически." : " Лаунчер можно открыть кнопкой «Открыть Epic».");
    }
    internal Task<EpicFriendBatchResult> AddFriendFromAllAsync(string nickname, IEpicFriends friends,
        IProgress<EpicFriendProgress>? progress = null, CancellationToken cancellation = default) => Operation(async () =>
    {
        nickname = EpicFriendNames.Validate(nickname);
        var library = store.Load(); var profiles = library.Profiles.ToArray();
        if (profiles.Length == 0) throw new InvalidOperationException("Сначала сохраните аккаунты Epic.");
        cancellation.ThrowIfCancellationRequested();
        var items = new List<EpicFriendItem>(); bool cancelled = false, restored = true;
        string restoreNote = "";
        if (!profiles.Any(profile => profile.HasRememberedLogin))
            return new EpicFriendBatchResult(profiles.Select(p => new EpicFriendItem(p.Name, EpicFriendStatus.Skipped, "Обновите сохранённый вход в Epic.")).ToArray(), true, false);
        await friends.CheckAvailableAsync(cancellation).ConfigureAwait(false);
        string launcher = platform.ResolveLauncher(library.LauncherPath);
        bool wasRunning = platform.IsRunning;
        var original = await CaptureStableAsync().ConfigureAwait(false);
        var recovery = new EpicRecovery(1, original, launcher);
        store.SaveRecovery(recovery);
        try
        {
            for (int index = 0; index < profiles.Length; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                var profile = profiles[index];
                if (!profile.HasRememberedLogin)
                { items.Add(new(profile.Name, EpicFriendStatus.Skipped, "Обновите сохранённый вход в Epic.")); continue; }
                progress?.Report(new(index + 1, profiles.Length, profile.Name, "Открываем аккаунт…"));
                try
                {
                    await SwitchCoreAsync(profile.Key, forceStart: true, recovery, cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    progress?.Report(new(index + 1, profiles.Length, profile.Name, "Ищем точный ник и проверяем заявку…"));
                    var attempt = await friends.SendAsync(launcher, profile.AccountId, nickname, cancellation).ConfigureAwait(false);
                    items.Add(new(profile.Name, attempt.Status, attempt.Detail));
                    progress?.Report(new(index + 1, profiles.Length, profile.Name, attempt.Detail));
                    if (attempt.StopBatch || attempt.Status == EpicFriendStatus.Uncertain) break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    items.Add(new(profile.Name, EpicFriendStatus.Failed, SafeReason(ex)));
                    progress?.Report(new(index + 1, profiles.Length, profile.Name, "Ошибка: " + SafeReason(ex)));
                    // A failed switch or inaccessible launcher must be resolved before more requests.
                    break;
                }
                if (index + 1 < profiles.Length) await Task.Delay(1500, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally
        {
            progress?.Report(new(0, profiles.Length, "Epic", "Возвращаем исходный аккаунт…"));
            try
            {
                var beforeFinalStop = await CaptureStableAsync().ConfigureAwait(false);
                var finalLogin = platform.ReadLogin();
                await platform.StopAsync(launcher).ConfigureAwait(false);
                // Keep a refreshed token if the original account was visited during this batch.
                var latestState = platform.Capture();
                if (!EpicRememberedLogin.CanRestore(latestState.ActiveIni) && EpicRememberedLogin.CanRestore(beforeFinalStop.ActiveIni) &&
                    string.Equals(latestState.AccountId, beforeFinalStop.AccountId, StringComparison.OrdinalIgnoreCase)) latestState = beforeFinalStop;
                if (finalLogin.Matches(latestState.AccountId) && EpicRememberedLogin.CanRestore(latestState.ActiveIni))
                {
                    var latestLibrary = store.Load();
                    var known = latestLibrary.Profiles.FirstOrDefault(p => p.AccountId.Equals(latestState.AccountId, StringComparison.OrdinalIgnoreCase));
                    if (known is not null) store.Save(EpicProfileStore.Upsert(latestLibrary, EpicProfileStore.FromState(latestState, known.Name, known.Key)));
                }
                var saved = store.Load().Profiles.FirstOrDefault(p => p.AccountId.Equals(original.AccountId, StringComparison.OrdinalIgnoreCase) && p.HasRememberedLogin);
                var restore = saved is null || !EpicRememberedLogin.CanRestore(original.ActiveIni) ? original :
                    original.EditorIni is not null ? original with { EditorIni = saved.Ini.ToArray() } : original with { LegacyIni = saved.Ini.ToArray() };
                store.SaveRecovery(new(1, restore, launcher));
                platform.Apply(restore); Verify(restore);
                if (wasRunning)
                {
                    await platform.StartAsync(launcher).ConfigureAwait(false);
                    if (saved is not null && EpicRememberedLogin.CanRestore(original.ActiveIni))
                    {
                        try { await ConfirmAndSaveLoginAsync(saved.AccountId).ConfigureAwait(false); }
                        catch (InvalidOperationException)
                        {
                            restored = false;
                            restoreNote = "Локальные данные исходного аккаунта возвращены, но Epic не подтвердил вход. Завершите вход в лаунчере вручную и обновите сохранённый профиль.";
                        }
                    }
                }
                store.ClearRecovery();
            }
            catch { restored = false; }
        }
        for (int index = items.Count; index < profiles.Length; index++)
            items.Add(new(profiles[index].Name, cancelled ? EpicFriendStatus.Cancelled : EpicFriendStatus.Skipped, cancelled ? "Отменено." : "Не обработан после остановки операции."));
        string historyNote="";
        if(!cancelled && EpicFriendHistory.Complete(profiles.Length,items))
        {
            try
            {
                var history=store.LoadFriendHistory();
                var currentIds=store.Load().Profiles.Select(p=>p.AccountId).ToArray();
                if(currentIds.Any(id=>!profiles.Any(p=>p.AccountId.Equals(id,StringComparison.OrdinalIgnoreCase))))
                    historyNote=" Во время обхода сохранён новый аккаунт. Для записи ника в список повторите полный обход.";
                else
                {
                    store.SaveFriendHistory(history.Remember(nickname,currentIds,clock?.Invoke() ?? DateTimeOffset.UtcNow,
                        items.Any(item=>item.Status==EpicFriendStatus.Sent)));
                    historyNote=" Ник сохранён в списке добавленных со всех аккаунтов.";
                }
            }
            catch(Exception) { historyNote=" Добавление подтверждено, но ник и таймер не удалось сохранить. Проверьте доступ к списку ников."; }
        }
        return new EpicFriendBatchResult(items.ToArray(), restored, cancelled,historyNote,restoreNote);
    });
    internal Task RecoverAsync() => Operation(async () =>
    {
        var recovery = store.ReadRecovery();
        string launcher = platform.ResolveLauncher(recovery.LauncherPath);
        await platform.StopAsync(launcher).ConfigureAwait(false);
        platform.Apply(recovery.State); Verify(recovery.State); store.ClearRecovery(); return true;
    }, allowRecovery: true);
    internal Task OpenAsync() => Operation(async () =>
    {
        var library = store.Load(); await platform.StartAsync(platform.ResolveLauncher(library.LauncherPath)).ConfigureAwait(false); return true;
    });
    internal Task<string> CheckFriendSearchAsync(IEpicFriends friends, string configured, CancellationToken cancellation = default) => Operation(async () =>
    {
        if (!platform.IsRunning) throw new InvalidOperationException("Сначала откройте Epic Games Launcher и войдите в аккаунт. Проверка не запускает и не переключает аккаунты.");
        return await friends.CheckSearchAsync(platform.ResolveLauncher(configured), cancellation).ConfigureAwait(false);
    });
    internal Task<string> CheckFriendStatusAsync(IEpicFriends friends,string configured,string nickname,CancellationToken cancellation=default) => Operation(async () =>
    {
        nickname=EpicFriendNames.Validate(nickname);
        if(!platform.IsRunning) throw new InvalidOperationException("Сначала откройте Epic Games Launcher. Проверка не запускает и не переключает аккаунты.");
        return await friends.CheckStatusAsync(platform.ResolveLauncher(configured),nickname,cancellation).ConfigureAwait(false);
    });
    private async Task<EpicLocalState> CaptureStableAsync()
    {
        var previous = platform.Capture(); EpicProfileStore.ValidateState(previous);
        for (int attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(250).ConfigureAwait(false);
            var current = platform.Capture(); EpicProfileStore.ValidateState(current);
            if (current.SameAs(previous)) return current;
            previous = current;
        }
        throw new InvalidOperationException("Epic обновляет данные входа. Дождитесь открытия библиотеки в лаунчере и повторите сохранение.");
    }
    private void Verify(EpicLocalState expected)
    {
        if (!platform.Capture().SameAs(expected)) throw new IOException("Epic изменил файлы во время переключения или запись не подтвердилась.");
    }
    private static string SafeReason(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Нет доступа к файлам или реестру Epic.",
        InvalidOperationException => ex.Message,
        IOException => "Проверьте доступ к файлам Epic и свободное место.",
        _ => "Лаунчер или файлы Epic недоступны."
    };
}
