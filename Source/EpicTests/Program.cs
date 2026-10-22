using System.Text;
using System.Text.Json;
using VpnZapretSwitch;

if (args.SequenceEqual(new[] { "--read-only-login" }))
{
    var diagnosticPlatform = new EpicWindows();
    var diagnosticLogin = diagnosticPlatform.ReadLogin();
    var diagnosticLibrary = new EpicProfileStore().Load();
    var diagnosticPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EpicGamesLauncher","Saved","Logs","EpicGamesLauncher.log");
    var diagnosticFormat = EpicLoginLog.Parse(Encoding.UTF8.GetString(EpicProfileStore.ReadBounded(diagnosticPath, EpicProfileStore.MaxData)),DateTimeOffset.MinValue);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {
        LoginStatus = diagnosticLogin.Status.ToString(), MatchesLocalAccount = diagnosticLogin.Matches(diagnosticPlatform.CurrentAccountId),
        Running = diagnosticPlatform.IsRunning, LogFormatStatus = diagnosticFormat.Status.ToString(),
        LogFormatMatchesLocal = diagnosticFormat.Matches(diagnosticPlatform.CurrentAccountId), Profiles = diagnosticLibrary.Profiles.Length, ChangesMade = false }));
    foreach (var example in Directory.EnumerateFiles(Path.GetDirectoryName(diagnosticPath)!,"EpicGamesLauncher-backup-*.log").OrderDescending().Take(5))
    {
        var format = EpicLoginLog.Parse(Encoding.UTF8.GetString(EpicProfileStore.ReadBounded(example,EpicProfileStore.MaxData)),DateTimeOffset.MinValue);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { File = Path.GetFileName(example), FormatStatus = format.Status.ToString(), EstablishesCurrentLogin = false }));
    }
    return;
}

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "epic-tests-" + Guid.NewGuid().ToString("N")));
int passed = 0;
string idA = new('a', 32), idB = new('b', 32);
byte[] Ini(string marker, int bytes = 1376, bool enabled = true, bool quoted = true)
{
    var envelope = new byte[bytes]; Encoding.UTF8.GetBytes(marker).CopyTo(envelope, 0);
    string value = Convert.ToBase64String(envelope); if (quoted) value = "\"" + value + "\"";
    return Encoding.UTF8.GetBytes("[Launcher]\r\nOtherSetting=preserved\r\n[RememberMe]\r\nEnable=" + enabled + "\r\nData=" + value + "\r\n");
}
byte[] iniA = Ini("synthetic-session-A"), iniB = Ini("synthetic-session-B");
EpicLocalState StateA() => new(iniA.ToArray(), Encoding.UTF8.GetBytes("legacy-original-A"), idA);
EpicLocalState StateB() => new(iniB.ToArray(), null, idB);
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
async Task Expect<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { passed++; return; }
    throw new Exception("Expected " + typeof(T).Name);
}
EpicProfileStore Store(string name) => new(Path.Combine(root, name));
EpicProfile Add(EpicProfileStore store, EpicLocalState state, string name)
{
    var profile = EpicProfileStore.FromState(state, name); store.Save(EpicProfileStore.Upsert(store.Load(), profile)); return profile;
}
try
{
    var store = Store("storage"); Check(store.Load().Profiles.Length == 0, "Missing store");
    var first = Add(store, StateA(), "Основной / профиль ..");
    Check(store.Load().Profiles.Single().Name == first.Name, "Unicode/punctuation label roundtrip");
    Check(!Encoding.UTF8.GetString(File.ReadAllBytes(store.LibraryPath)).Contains("synthetic-session"), "Credentials stored in plaintext");
    string unchanged = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(store.LibraryPath)));
    await Expect<InvalidDataException>(() => Task.Run(() => store.Save(store.Load() with { Profiles = [first with { Key = "../../evil" }] })));
    Check(unchanged == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(store.LibraryPath))), "Rejected save damaged store");
    using (store.Lock()) await Expect<InvalidOperationException>(() => Task.Run(() => store.Lock().Dispose()));
    var platform = new FakeEpic(StateA()); var controller = new EpicAccountController(store, platform);
    await controller.SaveCurrentAsync("Обновлённый");
    Check(store.Load().Profiles.Length == 1 && store.Load().Profiles[0].Key == first.Key, "ID deduplication changed identity");
    await controller.RenameAsync(first.Key, "Переименован"); Check(store.Load().Profiles[0].Name == "Переименован", "Rename");
    await controller.ExportAsync(Path.Combine(root, "backup.ht-epic"));
    await controller.RemoveAsync(first.Key); Check(store.Load().Profiles.Length == 0 && platform.Capture().SameAs(StateA()), "Remove affected current session");
    Check(await controller.ImportBackupAsync(Path.Combine(root, "backup.ht-epic")) == 1 && store.Load().Profiles.Length == 1, "Encrypted backup import");
    var target = Add(store, StateB(), "Второй");
    await controller.ConfigureAsync("EpicGamesLauncher.exe", true);
    string result = await controller.SwitchAsync(target.Key);
    Check(platform.Capture().SameAs(new(iniB, null, idB)), "Target state not applied or legacy not cleared");
    Check(platform.Calls.TakeLast(3).SequenceEqual(new[] { "stop", "apply", "start" }), "Wrong transaction order");
    Check(!store.NeedsRecovery && result.Contains("Epic подтвердил вход"), "Incorrect success/recovery state");
    var savedA = store.Load().Profiles.Single(profile => profile.AccountId == idA);
    Check(savedA.Ini.AsSpan().SequenceEqual(iniA), "Current state not preserved");
    await controller.ConfigureAsync("EpicGamesLauncher.exe", false); await controller.SwitchAsync(null);
    Check(platform.Capture().SameAs(new(null, null, "")) && platform.Calls.Last() == "apply", "New login or auto-start off");

    var old = StateA(); var stopFailStore = Store("stop-fail"); var stopTarget = Add(stopFailStore, StateB(), "B");
    var stopPlatform = new FakeEpic(old) { FailStop = true }; var stopController = new EpicAccountController(stopFailStore, stopPlatform);
    await Expect<InvalidOperationException>(() => stopController.SwitchAsync(stopTarget.Key));
    Check(stopPlatform.Capture().SameAs(old) && !stopPlatform.Calls.Contains("apply") && !stopFailStore.NeedsRecovery, "Stop failure modified files");
    stopPlatform.FailStop = false; stopPlatform.FailResolve = true; stopPlatform.Calls.Clear();
    await Expect<InvalidOperationException>(() => stopController.SwitchAsync(stopTarget.Key));
    Check(stopPlatform.Calls.Count == 0, "Invalid launcher stopped processes");
    await Expect<InvalidOperationException>(() => stopController.SwitchAsync("missing"));

    foreach (bool startFailure in new[] { false, true })
    {
        var rollbackStore = Store("rollback-" + startFailure); var rollbackTarget = Add(rollbackStore, StateB(), "B");
        var rollbackPlatform = new FakeEpic(old) { FailApplyCount = startFailure ? 0 : 1, FailStart = startFailure };
        var rollbackController = new EpicAccountController(rollbackStore, rollbackPlatform);
        await Expect<InvalidOperationException>(() => rollbackController.SwitchAsync(rollbackTarget.Key));
        Check(rollbackPlatform.Capture().SameAs(old) && !rollbackStore.NeedsRecovery, "Partial write/start failure did not restore exact original");
        Check(rollbackStore.Load().Profiles.Any(profile => profile.AccountId == idA), "Unknown current profile was not auto-saved");
    }
    var noLoginStore = Store("rollback-empty"); var emptyTarget = Add(noLoginStore, StateB(), "B");
    var emptyOriginal = new EpicLocalState(null, iniA, null); var noLoginPlatform = new FakeEpic(emptyOriginal) { FailStart = true };
    await Expect<InvalidOperationException>(() => new EpicAccountController(noLoginStore, noLoginPlatform).SwitchAsync(emptyTarget.Key));
    Check(noLoginPlatform.Capture().SameAs(emptyOriginal), "Absent registry/editor state not restored");

    var recoveryStore = Store("recovery"); var recoveryTarget = Add(recoveryStore, StateB(), "B");
    var recoveryPlatform = new FakeEpic(old) { FailApplyCount = 2 }; var recoveryController = new EpicAccountController(recoveryStore, recoveryPlatform);
    await Expect<InvalidOperationException>(() => recoveryController.SwitchAsync(recoveryTarget.Key));
    Check(recoveryStore.NeedsRecovery && recoveryStore.ReadRecovery().State.SameAs(old), "Recovery journal not retained");
    Check(!Encoding.UTF8.GetString(File.ReadAllBytes(recoveryStore.RecoveryPath)).Contains("synthetic-session"), "Recovery plaintext leak");
    int beforeBlocked = recoveryPlatform.Calls.Count;
    await Expect<InvalidOperationException>(() => recoveryController.SwitchAsync(null));
    Check(recoveryPlatform.Calls.Count == beforeBlocked, "Pending recovery did not block another switch");
    await recoveryController.RecoverAsync();
    Check(!recoveryStore.NeedsRecovery && recoveryPlatform.Capture().SameAs(old), "Explicit recovery failed");

    var mismatchStore = Store("verification"); var mismatchTarget = Add(mismatchStore, StateB(), "B");
    var mismatchPlatform = new FakeEpic(old) { IgnoreApplyCount = 1 };
    await Expect<InvalidOperationException>(() => new EpicAccountController(mismatchStore, mismatchPlatform).SwitchAsync(mismatchTarget.Key));
    Check(mismatchPlatform.Capture().SameAs(old) && !mismatchPlatform.Calls.Contains("start"), "Readback mismatch was treated as success");
    var concurrentStore = Store("concurrent"); var concurrentTarget = Add(concurrentStore, StateB(), "B");
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var concurrentPlatform = new FakeEpic(old) { StopWait = release.Task }; var concurrent = new EpicAccountController(concurrentStore, concurrentPlatform);
    var ongoing = concurrent.SwitchAsync(concurrentTarget.Key);
    await Expect<InvalidOperationException>(() => concurrent.SwitchAsync(concurrentTarget.Key));
    release.SetResult(); await ongoing;
    Check(!concurrentStore.NeedsRecovery, "Concurrent transaction failed");

    var sameStore = Store("same-profile"); var sameProfile = Add(sameStore, StateA(), "A");
    var rotated = StateA() with { EditorIni = Ini("freshly-rotated-synthetic-session") };
    var samePlatform = new FakeEpic(rotated); await new EpicAccountController(sameStore, samePlatform).SwitchAsync(sameProfile.Key);
    Check(samePlatform.Capture().ActiveIni!.AsSpan().SequenceEqual(rotated.ActiveIni), "Selecting current profile restored stale credentials");

    string tcno = Path.Combine(root, "tcno"); Directory.CreateDirectory(Path.Combine(tcno, "Профиль A"));
    const string regKey = "REG:HKCU\\Software\\Epic Games\\Unreal Engine\\Identifiers:AccountId";
    File.WriteAllText(Path.Combine(tcno, "ids.json"), JsonSerializer.Serialize(new Dictionary<string, string> { [idA] = "Профиль A" }));
    File.WriteAllBytes(Path.Combine(tcno, "Профиль A", "GameUserSettings.ini"), iniA);
    File.WriteAllText(Path.Combine(tcno, "Профиль A", "reg.json"), JsonSerializer.Serialize(new Dictionary<string, string> { [regKey] = idA }));
    var importStore = Store("import"); var importController = new EpicAccountController(importStore, new FakeEpic(new(null, null, null)));
    Check(await importController.ImportTcNoAsync(tcno) == 1 && importStore.Load().Profiles.Single().Name == "Профиль A", "TcNo import");
    await importController.ImportTcNoAsync(tcno); Check(importStore.Load().Profiles.Length == 1, "TcNo reimport duplicates");
    File.WriteAllText(Path.Combine(tcno, "Профиль A", "reg.json"), JsonSerializer.Serialize(new Dictionary<string, string> { [regKey] = idB }));
    await Expect<InvalidDataException>(() => importController.ImportTcNoAsync(tcno));
    Check(importStore.Load().Profiles.Length == 1, "Bad import partially replaced store");
    File.WriteAllText(Path.Combine(tcno, "ids.json"), JsonSerializer.Serialize(new Dictionary<string, string> { [idA] = "..\\..\\escape" }));
    await Expect<InvalidDataException>(() => importController.ImportTcNoAsync(tcno));
    await Expect<InvalidOperationException>(() => Task.Run(() => EpicProfileStore.FromState(new(iniA, null, "not-an-account-id"), "bad")));
    await Expect<InvalidOperationException>(() => Task.Run(() => EpicProfileStore.FromState(new([], null, idA), "bad")));
    await Expect<InvalidDataException>(() => Task.Run(() => EpicProfileStore.CleanName("bad\nname")));
    Check(EpicProfileStore.Target(new(null, iniA, idA), EpicProfileStore.FromState(StateB(), "B")).LegacyIni is not null, "Legacy Windows layout not supported");
    Check(EpicProfileStore.Target(new(null, null, null), EpicProfileStore.FromState(StateB(), "B")).EditorIni is not null, "New install did not prefer WindowsEditor");
    Check(!Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "Temporary files left behind");
    var registry = new MemoryRegistry(); var windows = new EpicWindows(Path.Combine(root, "synthetic-config"), registry);
    windows.Apply(old); Check(windows.Capture().SameAs(old), "Real file writer did not preserve both config variants");
    windows.Apply(new(iniB, null, idB)); Check(windows.Capture().SameAs(new(iniB, null, idB)), "Legacy file cleanup or registry update failed");
    windows.Apply(new(null, null, null)); Check(windows.Capture().SameAs(new(null, null, null)), "Absent config/registry restoration failed");
    Check(EpicWindows.LauncherFamily(@"C:\Epic\Portal\Binaries\Win32\EpicGamesLauncher.exe").Equals(
        EpicWindows.LauncherFamily(@"c:\epic\portal\binaries\win64\EpicGamesLauncher.exe"), StringComparison.OrdinalIgnoreCase), "Launcher bootstrap family / case handling");
    Check(!EpicWindows.LauncherFamily(@"C:\Other\EpicGamesLauncher.exe").Equals(EpicWindows.LauncherFamily(@"C:\Epic\Portal\Binaries\Win64\EpicGamesLauncher.exe"), StringComparison.OrdinalIgnoreCase), "Different installs were mixed");
    // Real-world regression: Epic can retain Enable=True + AccountId but write only
    // a short encrypted placeholder after losing its remembered login.
    byte[] shortLogin = Ini("synthetic-logged-out-placeholder", 160, quoted: false);
    Check(!EpicRememberedLogin.CanRestore(shortLogin), "216-character login-screen placeholder accepted");
    Check(EpicRememberedLogin.CanRestore(iniB), "Quoted 1838-character current launcher envelope rejected");
    Check(EpicRememberedLogin.CanRestore(Ini("unquoted", quoted: false)), "Unquoted remembered login rejected");
    Check(!EpicRememberedLogin.CanRestore(Ini("disabled", enabled: false)), "Disabled RememberMe accepted");
    Check(!EpicRememberedLogin.CanRestore(Encoding.UTF8.GetBytes("[RememberMe]\nEnable=True\nData=\n")), "Empty remembered login accepted");
    Check(!EpicRememberedLogin.CanRestore(Encoding.UTF8.GetBytes("[RememberMe]\nEnable=True\nData=not-base64!\n")), "Malformed remembered login accepted");
    Check(!EpicRememberedLogin.CanRestore(iniA.Concat(iniB).ToArray()), "Ambiguous duplicate sections accepted");
    var unicodeIni = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Encoding.UTF8.GetString(iniB))).ToArray();
    Check(EpicRememberedLogin.CanRestore(unicodeIni), "UTF16/BOM config rejected");
    var bigEndianIni = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(Encoding.UTF8.GetString(iniB))).ToArray();
    Check(EpicRememberedLogin.CanRestore(bigEndianIni), "Big endian config rejected");
    Check(!EpicRememberedLogin.CanRestore(Ini("short-block", 257)), "Invalid encrypted block size accepted");

    var guardStore = Store("placeholder-overwrite"); var guardA = Add(guardStore, StateA(), "A"); var guardB = Add(guardStore, StateB(), "B");
    var loggedOut = StateA() with { EditorIni = shortLogin };
    var guardPlatform = new FakeEpic(loggedOut); var guardController = new EpicAccountController(guardStore, guardPlatform);
    await Expect<InvalidOperationException>(() => guardController.SaveCurrentAsync("must not replace A"));
    Check(guardStore.Load().Profiles.Single(p => p.AccountId == idA).Ini.AsSpan().SequenceEqual(iniA), "Manual save destroyed useful login with placeholder");
    await guardController.SwitchAsync(guardB.Key);
    Check(guardStore.Load().Profiles.Single(p => p.AccountId == idA).Ini.AsSpan().SequenceEqual(iniA), "Auto-save destroyed useful login with placeholder");
    var sameFromLoginScreen = new FakeEpic(loggedOut);
    await new EpicAccountController(guardStore, sameFromLoginScreen).SwitchAsync(guardA.Key);
    Check(sameFromLoginScreen.Capture().ActiveIni!.AsSpan().SequenceEqual(iniA), "Stale local AccountId replaced the selected valid profile");

    var invalidStore = Store("old-invalid-profile"); var invalidA = guardA with { Ini = shortLogin };
    invalidStore.Save(EpicLibrary.Empty with { Profiles = [invalidA] });
    Check(invalidStore.Load().Profiles.Length == 1 && !invalidStore.Load().Profiles[0].HasRememberedLogin, "Old invalid profile lost instead of being marked");
    var invalidPlatform = new FakeEpic(StateB()); var invalidController = new EpicAccountController(invalidStore, invalidPlatform);
    await Expect<InvalidOperationException>(() => invalidController.SwitchAsync(invalidA.Key));
    Check(invalidPlatform.Calls.Count == 0 && !invalidStore.NeedsRecovery, "Invalid target closed the launcher or changed login");
    await invalidController.RenameAsync(invalidA.Key, "Needs new login");
    Check(invalidStore.Load().Profiles.Single().Name == "Needs new login", "Legacy incomplete profile cannot be renamed");

    var flushStore = Store("shutdown-flush"); var flushA = Add(flushStore, StateA(), "A");
    var flushPlatform = new FakeEpic(rotated) { StateAfterStop = loggedOut };
    await new EpicAccountController(flushStore, flushPlatform).SwitchAsync(flushA.Key);
    Check(flushStore.Load().Profiles.Single().Ini.AsSpan().SequenceEqual(rotated.ActiveIni), "Shutdown placeholder replaced the pre-close remembered login");
    Check(flushPlatform.Capture().ActiveIni!.AsSpan().SequenceEqual(rotated.ActiveIni), "Same-account switch restored a shutdown placeholder");

    var disabledCurrentStore = Store("no-auto-save-from-login"); var onlyB = Add(disabledCurrentStore, StateB(), "B");
    await new EpicAccountController(disabledCurrentStore, new FakeEpic(loggedOut)).SwitchAsync(onlyB.Key);
    Check(disabledCurrentStore.Load().Profiles.Length == 1, "A login-screen AccountId created a fake profile");
    Check(EpicWindows.IsLauncherHelperPath(@"C:\Epic\Launcher\Engine\Binaries\Win64\EpicWebHelper.exe", @"C:\Epic\Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe"), "Matching launcher browser helper rejected");
    Check(!EpicWindows.IsLauncherHelperPath(@"C:\Other\Launcher\Engine\Binaries\Win64\EpicWebHelper.exe", @"C:\Epic\Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe"), "Other installation browser helper accepted");
    Check(!EpicWindows.IsLauncherHelperPath(@"C:\Epic\Launcher\Engine\Binaries\Win64\FortniteClient.exe", @"C:\Epic\Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe"), "Game accepted as launcher helper");
    await Expect<InvalidOperationException>(() => Task.Run(() => EpicProfileStore.Upsert(guardStore.Load(), invalidA)));
    var shortBackupStore = Store("short-backup"); shortBackupStore.Save(EpicLibrary.Empty with { Profiles = [invalidA] });
    string shortBackup = Path.Combine(root, "short-login.ht-epic"); shortBackupStore.Export(shortBackup);
    await Expect<InvalidOperationException>(() => guardController.ImportBackupAsync(shortBackup));
    Check(guardStore.Load().Profiles.Single(p => p.AccountId == idA).Ini.AsSpan().SequenceEqual(iniA), "Import replaced useful remembered login with an old short envelope");
    var flushRollbackStore = Store("shutdown-flush-rollback"); var flushRollbackA = Add(flushRollbackStore, StateA(), "A");
    var flushRollbackPlatform = new FakeEpic(rotated) { StateAfterStop = loggedOut, FailStart = true };
    await Expect<InvalidOperationException>(() => new EpicAccountController(flushRollbackStore, flushRollbackPlatform).SwitchAsync(flushRollbackA.Key));
    Check(flushRollbackPlatform.Capture().SameAs(rotated), "Rollback discarded the complete pre-shutdown login");
    Check(EpicFriendNames.Validate("  Exact_Nick  ") == "Exact_Nick", "Friend nickname trimming");
    Check(!EpicFriendNames.Exact("Exact_Nick_2", "Exact_Nick"), "Partial nickname accepted");
    await Expect<InvalidOperationException>(() => Task.Run(() => EpicFriendNames.Validate("bad\nname")));
    await Expect<InvalidOperationException>(() => Task.Run(() => EpicFriendNames.Validate("bad\u200bname")));

    var friendsStore = Store("friends-batch"); Add(friendsStore, StateA(), "A"); Add(friendsStore, StateB(), "B");
    friendsStore.Save(friendsStore.Load() with { AutoStart = false });
    var friendsPlatform = new FakeEpic(StateA()); var friendsController = new EpicAccountController(friendsStore, friendsPlatform);
    var friends = new FakeFriends(friendsPlatform);
    var batch = await friendsController.AddFriendFromAllAsync("Exact_Nick", friends);
    Check(batch.Restored && !batch.Cancelled && batch.Items.All(i => i.Status == EpicFriendStatus.Sent), "Normal friend batch failed");
    Check(friends.Senders.SequenceEqual(new[] { idA, idB }) && friends.Names.All(n => n == "Exact_Nick"), "Wrong senders or recipient");
    Check(friendsPlatform.Capture().SameAs(StateA()) && friendsPlatform.IsRunning && !friendsStore.NeedsRecovery, "Original account/process not restored");
    Check(!friendsStore.Load().AutoStart, "Friend batch changed normal auto-start preference");
    var mixedLibrary = friendsStore.Load();
    mixedLibrary = mixedLibrary with { Profiles = [mixedLibrary.Profiles[0], new(Guid.NewGuid().ToString("N"), new string('c',32), "Incomplete", DateTimeOffset.UtcNow, "WindowsEditor", shortLogin), mixedLibrary.Profiles[1]] };
    friendsStore.Save(mixedLibrary); friends = new(friendsPlatform);
    batch = await friendsController.AddFriendFromAllAsync("Partial_Nick", friends);
    Check(batch.Items.Length == 3 && batch.Items[1].Status == EpicFriendStatus.Skipped && friends.Senders.Count == 2, "Incomplete profile was visited/sent");
    Check(!friendsStore.LoadFriendHistory().Friends.Any(f=>f.Nickname=="Partial_Nick"),"Nick with skipped account was tracked as complete");

    var cancelled = new CancellationTokenSource(); friends = new(friendsPlatform) { OnSend = () => cancelled.Cancel() };
    batch = await friendsController.AddFriendFromAllAsync("Exact_Nick", friends, cancellation: cancelled.Token);
    Check(batch.Cancelled && batch.Restored && friends.Senders.Count == 1 && batch.Items[^1].Status == EpicFriendStatus.Cancelled, "Cancellation did not stop further accounts");
    Check(friendsPlatform.Capture().SameAs(StateA()) && !friendsStore.NeedsRecovery, "Cancellation lost original login");
    friends = new(friendsPlatform) { Attempt = new(EpicFriendStatus.Uncertain, "Unknown acknowledgement", true) };
    batch = await friendsController.AddFriendFromAllAsync("Exact_Nick", friends);
    Check(friends.Senders.Count == 1 && batch.Restored && batch.Items.Count(i => i.Status == EpicFriendStatus.Uncertain) == 1, "Uncertain request was retried or batch continued");
    friends = new(friendsPlatform) { Attempt = new(EpicFriendStatus.Pending, "Already pending") };
    batch = await friendsController.AddFriendFromAllAsync("Exact_Nick", friends);
    Check(batch.Items.Count(i => i.Status == EpicFriendStatus.Pending) == 2 && batch.Restored, "Pending requests were not recorded separately");
    friends = new(friendsPlatform) { Attempt = new(EpicFriendStatus.NotFound, "Not found") };
    batch = await friendsController.AddFriendFromAllAsync("Exact_Nick", friends);
    Check(friends.Senders.Count == 2 && batch.Restored && batch.Items.Count(i => i.Status == EpicFriendStatus.NotFound) == 2, "Not-found recipient prevented other account checks");

    friendsPlatform.SetRunning(false); friends = new(friendsPlatform);
    batch = await friendsController.AddFriendFromAllAsync("Exact_Nick", friends);
    Check(batch.Restored && !friendsPlatform.IsRunning && friendsPlatform.Capture().SameAs(StateA()), "Initially closed launcher left running");
    using var preCancelled = new CancellationTokenSource(); preCancelled.Cancel();
    int callsBeforeCancelled = friendsPlatform.Calls.Count;
    await Expect<OperationCanceledException>(() => friendsController.AddFriendFromAllAsync("Exact_Nick", new FakeFriends(friendsPlatform), cancellation: preCancelled.Token));
    Check(friendsPlatform.Calls.Count == callsBeforeCancelled, "Pre-cancelled batch changed Epic");

    friends = new(friendsPlatform) { FailAvailability = true };
    await Expect<InvalidOperationException>(() => friendsController.AddFriendFromAllAsync("Exact_Nick", friends));
    Check(friendsPlatform.Calls.Count == callsBeforeCancelled && !friendsStore.NeedsRecovery, "Missing OCR changed account state");
    var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    friends = new(friendsPlatform) { Wait = block.Task };
    var concurrentBatch = friendsController.AddFriendFromAllAsync("Exact_Nick", friends);
    await friends.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Expect<InvalidOperationException>(() => friendsController.SaveCurrentAsync("Blocked"));
    await Expect<InvalidOperationException>(() => Task.Run(() => friendsStore.Lock().Dispose()));
    block.SetResult(); batch = await concurrentBatch;
    Check(batch.Restored, "Batch locks prevented its own restoration");

    var restoreFailureStore = Store("friend-restore-failure"); Add(restoreFailureStore, StateA(), "A");
    var restoreFailurePlatform = new FakeEpic(StateA());
    var restoreFailureFriends = new FakeFriends(restoreFailurePlatform) { OnSend = () => restoreFailurePlatform.FailApplyCount = 1 };
    var restoreFailureController = new EpicAccountController(restoreFailureStore, restoreFailurePlatform);
    batch = await restoreFailureController.AddFriendFromAllAsync("Exact_Nick", restoreFailureFriends);
    Check(!batch.Restored && restoreFailureStore.NeedsRecovery, "Restore failure silently lost recovery journal");
    await Expect<InvalidOperationException>(() => restoreFailureController.SaveCurrentAsync("Blocked"));
    await restoreFailureController.RecoverAsync();
    Check(restoreFailurePlatform.Capture().SameAs(StateA()) && !restoreFailureStore.NeedsRecovery, "Friend batch recovery failed");

    var header = new EpicOcrLine("Добавить друзей", new(1205, 140, 170, 22));
    var resultLine = new EpicOcrLine("Exact_Nick", new(1210, 311, 100, 20));
    Check(EpicFriendVision.SearchResult([header, resultLine], header, "Exact_Nick") == resultLine, "Exact search row not selected");
    Check(EpicFriendVision.SearchResult([header,resultLine,resultLine with {Bounds=new(1211,312,100,20)}],header,"Exact_Nick") is not null,"Coincident OCR passes rejected an exact row");
    var realHeader=new EpicOcrLine("Добавить друзей",new(1206,142,154,18));
    var countCaption=new EpicOcrLine("Найден 1 игрок",new(1158,256,107,14));
    var broadName=new EpicOcrLine("MopsShOp7",new(1210,310,85,15));
    var typedName=new EpicOcrLine("MopsSh0p7",new(1199,199,81,14));
    var nameRegions=EpicFriendNameVision.Regions([realHeader,countCaption,typedName,broadName],realHeader,new(1546,831),1);
    Check(nameRegions.Length==1 && nameRegions[0].Bounds==new Rectangle(1200,299,246,40),"Name region mixed query/avatar/action with player name");
    Check(nameRegions[0].RowBounds==broadName.Bounds,"Enlarged OCR lost original row coordinates");
    Check(EpicFriendNameVision.Regions([realHeader,new("Друзья + Q",new(1158,440,330,22))],realHeader,new(1546,831),1).Length==0,"Toolbar was treated as a player name region");
    Check(EpicFriendVision.SearchResult([realHeader,countCaption,typedName],realHeader,"MopsSh0p7") is null,"Typed search query established recipient identity");
    var correctedName=broadName with {Text="MopsSh0p7"};
    Check(EpicFriendNameVision.Consensus([correctedName],[correctedName],nameRegions[0].Bounds,1)?.Text=="MopsSh0p7","Whole-line OCR agreement not recognized");
    var nativeBox=correctedName with {Bounds=new(1210,307,85,17)};
    Check(EpicFriendNameVision.Consensus([nativeBox],[correctedName with {Bounds=new(1211,299,84,21)}],nameRegions[0].Bounds,1)?.Bounds==nativeBox.Bounds,"Contrast OCR moved the physical row/button coordinates");
    Check(EpicFriendNameVision.Consensus([correctedName],[broadName],nameRegions[0].Bounds,1) is null,"OCR O/0 disagreement was normalized");
    Check(EpicFriendNameVision.Consensus([correctedName],[correctedName with {Text="MopsSh0p7 More"}],nameRegions[0].Bounds,1) is null,"Longer nickname matched a fragment");
    Check(EpicFriendNameVision.Consensus([correctedName,correctedName],[correctedName],nameRegions[0].Bounds,1) is null,"Multiple name lines accepted as one recipient");
    Check(EpicFriendNameVision.Consensus([],[],nameRegions[0].Bounds,1) is null,"Empty OCR established recipient");
    Check(EpicFriendNameVision.Consensus([correctedName],[correctedName with {Bounds=new(1210,360,85,15)}],nameRegions[0].Bounds,1) is null,"Different OCR row agreed on nickname");
    Check(EpicFriendNameVision.Consensus([correctedName with {Bounds=new(1200,310,85,15)}],[correctedName],nameRegions[0].Bounds,1) is null,"Clipped left edge accepted as complete nickname");
    Check(EpicFriendNameVision.Consensus([correctedName with {Bounds=new(1210,310,234,15)}],[correctedName],nameRegions[0].Bounds,1) is null,"Clipped right edge accepted as complete nickname");
    var correctedLines=new List<EpicOcrLine>{realHeader,countCaption,typedName,broadName};
    EpicFriendNameVision.ReplaceRow(correctedLines,nameRegions[0].Bounds,correctedName);
    Check(EpicFriendVision.SearchResult(correctedLines,realHeader,"MopsSh0p7") is not null,"Confirmed zero nickname not selected");
    Check(EpicFriendVision.SearchResult(correctedLines,realHeader,"MopsShOp7") is null,"Broad OCR O spelling survived exact zero confirmation");
    EpicFriendNameVision.ReplaceRow(correctedLines,nameRegions[0].Bounds,null);
    Check(EpicFriendVision.SearchResult(correctedLines,realHeader,"MopsSh0p7") is null,"Failed row confirmation fell back to unconfirmed OCR");
    Check(correctedLines.Contains(typedName) && correctedLines.Contains(countCaption),"Row replacement altered query or result caption");
    Check(EpicFriendVision.SearchResult([header, resultLine with { Text = "Exact_Nick_2" }], header, "Exact_Nick") is null, "Partial search row selected");
    Check(EpicFriendVision.SearchResult([header, resultLine, resultLine with { Bounds = new(1210, 365, 100, 20) }], header, "Exact_Nick") is null, "Ambiguous rows selected");
    var modalName = new EpicOcrLine("Exact_Nick", new(860, 420, 130, 25));
    var addAction = new EpicOcrLine("Добавить в друзья", new(840, 565, 170, 20));
    Check(EpicFriendVision.Profile([header, resultLine, modalName, addAction], header, "Exact_Nick")?.ExistingStatus is null &&
        EpicFriendVision.Profile([header, resultLine, modalName, addAction], header, "Exact_Nick")?.Action == addAction, "Target card not verified");
    Check(EpicFriendVision.Profile([header, resultLine, modalName with { Text = "Exact_Nick_2" }, addAction], header, "Exact_Nick") is null, "Wrong modal recipient accepted");
    Check(EpicFriendVision.Profile([header, modalName, addAction with { Text = "Отменить запрос" }], header, "Exact_Nick")?.ExistingStatus == EpicFriendStatus.Pending, "Pending state not recognized");
    Check(!EpicFriendVision.IsAddAction("Принять") && !EpicFriendVision.IsAddAction("Удалить из друзей"), "Unsafe social action allowed");
    Check(EpicFriendVision.AddHeader([header], 1546) == header && EpicFriendVision.AddHeader([header with { Bounds = new(10,140,170,22) }],1546) is null, "Background content accepted as social panel");
    var finalTokenStore = Store("friend-final-token"); Add(finalTokenStore, StateA(), "A");
    var finalTokenPlatform = new FakeEpic(StateA());
    var finalTokenFriends = new FakeFriends(finalTokenPlatform) { OnSend = () => { finalTokenPlatform.Apply(rotated); finalTokenPlatform.StateAfterStop = loggedOut; } };
    batch = await new EpicAccountController(finalTokenStore, finalTokenPlatform).AddFriendFromAllAsync("Exact_Nick", finalTokenFriends);
    Check(batch.Restored && finalTokenPlatform.Capture().SameAs(rotated), "Final shutdown placeholder lost refreshed original-account token");
    var emptyBatchStore = Store("friend-empty-original"); Add(emptyBatchStore, StateA(), "A");
    var emptyBatchPlatform = new FakeEpic(new(null, null, null)); emptyBatchPlatform.SetRunning(false);
    batch = await new EpicAccountController(emptyBatchStore, emptyBatchPlatform).AddFriendFromAllAsync("Exact_Nick", new FakeFriends(emptyBatchPlatform));
    Check(batch.Restored && !emptyBatchPlatform.IsRunning && emptyBatchPlatform.Capture().SameAs(new(null, null, null)), "Batch failed to restore an initially absent login");
    var groupedFriends = new EpicOcrLine("Друзья + Q", new(1158,440,330,22), [new("Друзья",new(1158,440,68,22)),new("+",new(1420,440,16,22)),new("Q",new(1463,440,16,22))]);
    Check(EpicFriendVision.FriendsHeading([groupedFriends],1546)?.Bounds == new Rectangle(1158,440,68,22), "Toolbar glyphs hid friends heading");
    var groupedHeader = new EpicOcrLine("< Добавить друзей Q", new(1166,140,320,22), [new("<",new(1166,140,15,22)),new("Добавить",new(1205,140,90,22)),new("друзей",new(1303,140,75,22)),new("Q",new(1463,140,16,22))]);
    Check(EpicFriendVision.AddHeader([groupedHeader],1546)?.Text == "Добавить друзей", "Navigation glyphs hid add-friends heading");
    Check(EpicFriendVision.FriendsHeading([new("Запросы на добавление в друзья",new(1205,480,280,40))],1546) is null, "Friend-request paragraph mistaken for toolbar");
    Check(EpicFriendVision.AddHeader([header,header with { Bounds=new(1206,141,170,22) }],1546) is not null, "Two OCR passes created an ambiguous heading");
    var partialNameLine = resultLine with { Text="Cool Exact_Nick", WordBoxes=[new("Cool",new(1210,311,35,20)),new("Exact_Nick",new(1250,311,100,20))] };
    Check(EpicFriendVision.SearchResult([header,partialNameLine],header,"Exact_Nick") is null, "OCR word metadata loosened exact recipient search");
    Check(EpicFriendVision.Profile([header,modalName with { Text="Cool Exact_Nick", WordBoxes=[new("Exact_Nick",modalName.Bounds)] },addAction],header,"Exact_Nick") is null, "Nickname fragment mistaken for complete profile name");
    var navigator = new EpicSearchNavigation();
    Check(navigator.Next(false,true,true,true,false,TimeSpan.Zero) == EpicSearchStep.OpenAdd, "Pre-opened social panel did not open Add friends");
    Check(navigator.Next(false,false,false,true,false,TimeSpan.FromMilliseconds(750)) == EpicSearchStep.Wait, "Avatar toggled while search was loading");
    Check(navigator.Next(false,false,false,true,false,TimeSpan.FromSeconds(8)) == EpicSearchStep.Wait && navigator.SocialAttempts == 0, "Loading panel was closed after delay");
    Check(navigator.Next(true,false,false,true,false,TimeSpan.FromSeconds(10)) == EpicSearchStep.Ready, "Loaded search did not finish navigation");
    var retryNavigator = new EpicSearchNavigation();
    Check(retryNavigator.Next(false,false,false,true,false,TimeSpan.Zero) == EpicSearchStep.OpenSocial, "Closed social panel did not open");
    Check(retryNavigator.Next(false,false,false,true,false,TimeSpan.FromSeconds(1)) == EpicSearchStep.Wait, "Social click retried too soon");
    Check(retryNavigator.Next(false,false,false,true,false,TimeSpan.FromSeconds(6)) == EpicSearchStep.OpenSocial, "Swallowed social click could not retry");
    Check(retryNavigator.Next(false,true,false,true,false,TimeSpan.FromSeconds(12)) == EpicSearchStep.Wait, "Visible social panel was toggled closed");
    Check(new EpicSearchNavigation().Next(false,false,false,false,true,TimeSpan.Zero) == EpicSearchStep.LoginRequired, "Sign-in screen not detected");
    var searchProbeStore=Store("friend-search-probe"); Add(searchProbeStore,StateA(),"A");
    var searchProbePlatform=new FakeEpic(StateA()); var searchProbeFriends=new FakeFriends(searchProbePlatform);
    string probe=await new EpicAccountController(searchProbeStore,searchProbePlatform).CheckFriendSearchAsync(searchProbeFriends,"EpicGamesLauncher.exe");
    Check(searchProbeFriends.SearchChecks==1 && searchProbeFriends.Senders.Count==0 && searchProbePlatform.Calls.Count==0 && !searchProbeStore.NeedsRecovery, "Search-only probe switched accounts or sent a request");
    Bitmap ToolbarImage(double scale, bool second = true, bool glyphs = true, bool duplicate = false, int xShift = 0)
    {
        var image = new Bitmap((int)(1546 * scale), (int)(830 * scale));
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.FromArgb(25,25,29)); graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(Color.FromArgb(37,37,41)); using var border = new Pen(Color.FromArgb(57,57,61), (float)scale);
        using var glyph = new Pen(Color.FromArgb(220,220,220), (float)(1.4 * scale));
        void DrawButton(int distance, int cy)
        {
            float x = image.Width - (float)(distance * scale) + xShift, y = (float)(cy * scale), h = (float)(18 * scale), r = (float)(8 * scale);
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(x-h,y-h,r*2,r*2,180,90); path.AddArc(x+h-r*2,y-h,r*2,r*2,270,90);
            path.AddArc(x+h-r*2,y+h-r*2,r*2,r*2,0,90); path.AddArc(x-h,y+h-r*2,r*2,r*2,90,90); path.CloseFigure();
            graphics.FillPath(fill,path); graphics.DrawPath(border,path);
            if (!glyphs) return;
            if (distance == 118) { graphics.DrawEllipse(glyph,x-(float)(4*scale),y-(float)(8*scale),(float)(6*scale),(float)(6*scale)); graphics.DrawArc(glyph,x-(float)(7*scale),y,(float)(10*scale),(float)(10*scale),180,180); graphics.DrawLine(glyph,x+(float)(5*scale),y,x+(float)(5*scale),y+(float)(8*scale)); graphics.DrawLine(glyph,x+(float)(1*scale),y+(float)(4*scale),x+(float)(9*scale),y+(float)(4*scale)); }
            else { graphics.DrawEllipse(glyph,x-(float)(7*scale),y-(float)(7*scale),(float)(11*scale),(float)(11*scale)); graphics.DrawLine(glyph,x+(float)(3*scale),y+(float)(3*scale),x+(float)(9*scale),y+(float)(9*scale)); }
        }
        DrawButton(118,448); if(second) DrawButton(74,448);
        if(duplicate) { DrawButton(118,600); DrawButton(74,600); }
        return image;
    }
    foreach (double scale in new[] { 1d,1.25,1.5,2 })
    {
        using var image = ToolbarImage(scale);
        var caption = new EpicOcrLine("Панель общения",new((int)(1158*scale),(int)(130*scale),(int)(195*scale),(int)(24*scale)));
        var foundToolbar = EpicSocialToolbar.Find(image,scale,caption);
        Check(foundToolbar is not null && Math.Abs(foundToolbar.Add.X - (image.Width - 118*scale)) < 3 && Math.Abs(foundToolbar.Add.Y - 448*scale) < 3,
            $"Icon toolbar not found at scale {scale} when Friends OCR is absent");
        Check(EpicSocialToolbar.Find(image,scale,null) is null,"Toolbar accepted without social caption");
    }
    var socialCaption = new EpicOcrLine("Панель общения",new(1158,130,195,24));
    using (var oneButton = ToolbarImage(1,second:false)) Check(EpicSocialToolbar.Find(oneButton,1,socialCaption) is null,"Single settings button mistaken for friends toolbar");
    using (var emptyButtons = ToolbarImage(1,glyphs:false)) Check(EpicSocialToolbar.Find(emptyButtons,1,socialCaption) is null,"Empty decorative boxes accepted as toolbar");
    using (var duplicates = ToolbarImage(1,duplicate:true)) Check(EpicSocialToolbar.Find(duplicates,1,socialCaption) is null,"Two possible toolbars not rejected");
    using (var shifted = ToolbarImage(1,xShift:-70)) Check(EpicSocialToolbar.Find(shifted,1,socialCaption) is null,"Unrelated button pair accepted outside sidebar toolbar");
    var fallbackNavigation=new EpicSearchNavigation();
    Check(fallbackNavigation.Next(false,true,true,true,false,TimeSpan.Zero)==EpicSearchStep.OpenAdd &&
        fallbackNavigation.Next(false,true,false,true,false,TimeSpan.FromSeconds(1))==EpicSearchStep.Wait &&
        fallbackNavigation.Next(true,false,false,true,false,TimeSpan.FromSeconds(2))==EpicSearchStep.Ready,"Visual toolbar fallback did not wait for confirmed Add friends page");
    var rowPending = new EpicOcrLine("Заявка отправлена",new(1210,332,140,16));
    Check(EpicFriendVision.WholeSidebarResult(resultLine,header,new(1546,830),1,1026),"Complete enlarged result line rejected");
    Check(!EpicFriendVision.WholeSidebarResult(resultLine with {Bounds=new(1026,311,100,20)},header,new(1546,830),1,1026),"Crop-edge nickname fragment accepted");
    Check(!EpicFriendVision.WholeSidebarResult(resultLine with {Bounds=new(1210,200,100,20)},header,new(1546,830),1,1026),"Typed query accepted as enlarged result");
    Check(!EpicFriendVision.WholeSidebarResult(resultLine with {Bounds=new(1390,311,100,20)},header,new(1546,830),1,1026),"Action-column crop allowed a truncated result");
    Check(EpicFriendVision.RowStatus([header,resultLine,rowPending],header,"Exact_Nick")==EpicFriendStatus.Pending,"Pending label not bound to exact row");
    Check(EpicFriendVision.RowStatus([header,resultLine,rowPending with {Bounds=new(1210,395,140,16)}],header,"Exact_Nick") is null,"Another player's status accepted");
    Check(EpicFriendVision.RowStatus([header,resultLine with {Text="Exact_Nick_2"},rowPending],header,"Exact_Nick") is null,"Row status accepted for partial nickname");
    Check(EpicFriendVision.RowStatus([header,resultLine,rowPending,new("Вы друзья",new(1210,332,100,16))],header,"Exact_Nick") is null,"Contradictory row states accepted");
    Check(EpicFriendVision.RowStatus([header,resultLine with {Text="Friends"}],header,"Friends") is null,"Player name mistaken for friendship state");
    var sentToast=new EpicOcrLine("Friend request sent",new(700,40,160,18));
    Check(EpicFriendVision.NewSentNotice([header,resultLine],[header,resultLine,sentToast]),"New explicit acknowledgement missed");
    Check(!EpicFriendVision.NewSentNotice([header,resultLine,sentToast],[header,resultLine,sentToast]),"Old toast mistaken for new acknowledgement");
    Check(!EpicFriendVision.NewSentNotice([],[new("Cancel friend request",new(700,40,160,18))]),"Unrelated action mistaken for sent notice");
    Check(!EpicFriendVision.IsSentNotice("Friend request sent failed") && !EpicFriendVision.IsSentNotice("Запрос не отправлен"),"Failure text accepted as success");
    using(var blankRow=new Bitmap(1546,830))
    {
        Check(EpicFriendRowButton.Find(blankRow,1,resultLine) is null,"Blank row caused Add click");
        Check(EpicFriendRowButton.Find(blankRow,1,null) is null,"Add selected without exact nickname result");
        Check(EpicFriendRowButton.Find(blankRow,1,resultLine with {Bounds=new(50,311,100,20)}) is null,"Main content accepted as player row");
    }
    Bitmap PendingImage(double scale,string shape="check",bool cancel=true,int offsetY=0)
    {
        var image=new Bitmap((int)(1546*scale),(int)(830*scale)); using var g=Graphics.FromImage(image);
        g.Clear(Color.FromArgb(24,24,30));g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float x=image.Width-(float)(118*scale),y=(float)(316*scale)+offsetY;
        using var green=new Pen(shape=="red" ? Color.FromArgb(210,90,90) : Color.FromArgb(101,186,117),(float)(1.25*scale));
        green.StartCap=green.EndCap=System.Drawing.Drawing2D.LineCap.Round;
        if(shape is "check" or "red") g.DrawLines(green,new PointF[]{new(x-(float)(4*scale),y),new(x,y+(float)(3*scale)),new(x+(float)(8*scale),y-(float)(5*scale))});
        else if(shape=="dot") g.DrawEllipse(green,x-(float)(4*scale),y-(float)(4*scale),(float)(8*scale),(float)(8*scale));
        else if(shape=="plus") {g.DrawLine(green,x-(float)(6*scale),y,x+(float)(6*scale),y);g.DrawLine(green,x,y-(float)(6*scale),x,y+(float)(6*scale));}
        if(cancel)
        {
            float xx=image.Width-(float)(74*scale),hh=(float)(4.5*scale);using var cross=new Pen(Color.FromArgb(200,200,200),(float)(1.1*scale));
            g.DrawLine(cross,xx-hh,y-hh,xx+hh,y+hh);g.DrawLine(cross,xx-hh,y+hh,xx+hh,y-hh);
        }
        return image;
    }
    foreach(double scale in new[]{1d,1.25,1.5,2})
    {
        var playerRow=new EpicOcrLine("Exact_Nick",new((int)(1210*scale),(int)(308*scale),(int)(78*scale),(int)(16*scale)));
        using var image=PendingImage(scale);
        Check(EpicFriendRowButton.HasPending(image,scale,playerRow),$"Green check and Cancel not recognized at {scale}");
        Check(EpicFriendRowButton.Find(image,scale,playerRow) is null,"Pending Cancel button mistaken for Add; duplicate request possible");
        Check(!EpicFriendRowButton.HasPending(image,scale,null),"Pending accepted without exact player row");
        using var noCancel=PendingImage(scale,cancel:false);Check(!EpicFriendRowButton.HasPending(noCancel,scale,playerRow),"Green check without Cancel accepted");
    }
    foreach(string shape in new[]{"dot","plus","red"})
    {using var image=PendingImage(1,shape);Check(!EpicFriendRowButton.HasPending(image,1,new("Exact_Nick",new(1210,308,78,16))),"Non-check indicator accepted: "+shape);}
    using(var otherRow=PendingImage(1,offsetY:50)) Check(!EpicFriendRowButton.HasPending(otherRow,1,new("Exact_Nick",new(1210,308,78,16))),"Another row's green check accepted");
    bool queried=false;
    Check(EpicProcessImage.Resolve(()=>true,()=>{queried=true;return @"C:\Epic\EpicGamesLauncher.exe";}) is null && !queried,"Exited process queried");
    Check(EpicProcessImage.Resolve(()=>false,()=>@"C:\Epic\EpicGamesLauncher.exe")==@"C:\Epic\EpicGamesLauncher.exe","Direct process image path rejected");
    bool exitedDuringQuery=false;
    Check(EpicProcessImage.Resolve(()=>exitedDuringQuery,()=>{exitedDuringQuery=true;throw new System.ComponentModel.Win32Exception(299);}) is null,"Normal process exit mistaken for failed verification");
    bool exitedAfterRead=false;
    Check(EpicProcessImage.Resolve(()=>exitedAfterRead,()=>{exitedAfterRead=true;return @"C:\Epic\EpicGamesLauncher.exe";}) is null,"Exited bootstrap process retained");
    await Expect<InvalidOperationException>(()=>Task.Run(()=>EpicProcessImage.Resolve(()=>false,()=>throw new System.ComponentModel.Win32Exception(5))));
    await Expect<InvalidOperationException>(()=>Task.Run(()=>EpicProcessImage.Resolve(()=>false,()=>"")));
    using(var ownProcess=System.Diagnostics.Process.GetCurrentProcess())
        Check(string.Equals(EpicProcessImage.Read(ownProcess),Environment.ProcessPath,StringComparison.OrdinalIgnoreCase),"QueryFullProcessImageName failed on own test process");
    await new EpicAccountController(searchProbeStore,searchProbePlatform).CheckFriendStatusAsync(searchProbeFriends,"EpicGamesLauncher.exe","Exact_Nick");
    Check(searchProbeFriends.StatusChecks==1 && searchProbeFriends.Senders.Count==0 && searchProbePlatform.Calls.Count==0 && !searchProbeStore.NeedsRecovery,"Status-only check switched accounts or sent a request");
    DateTimeOffset timerNow=new(2026,10,21,7,0,0,TimeSpan.Zero);
    var successItems=new[]{new EpicFriendItem("A",EpicFriendStatus.Sent,""),new EpicFriendItem("B",EpicFriendStatus.Pending,""),new EpicFriendItem("C",EpicFriendStatus.AlreadyFriends,"")};
    Check(EpicFriendHistory.Complete(3,successItems),"Complete mixed successful states rejected");
    Check(!EpicFriendHistory.Complete(0,[]) && !EpicFriendHistory.Complete(4,successItems),"Empty/partial batch counted as all accounts");
    foreach(var status in new[]{EpicFriendStatus.Skipped,EpicFriendStatus.Failed,EpicFriendStatus.Uncertain,EpicFriendStatus.Cancelled,EpicFriendStatus.NotFound})
        Check(!EpicFriendHistory.Complete(3,[successItems[0],successItems[1],successItems[2] with {Status=status}]),"Incomplete status started timer: "+status);
    var timedHistory=EpicFriendHistory.Empty.Remember("Timer_Nick",[idA,idB],timerNow,true);
    var timed=timedHistory.Friends.Single();
    Check(timed.StartedAt==timerNow && timed.ReadyAt==timerNow.AddHours(48) && timed.Countdown(timerNow)=="48:00:00","48-hour timer start incorrect");
    Check(!timed.IsReady([idA,idB],timerNow.AddHours(48).AddTicks(-1)) && timed.IsReady([idA,idB],timerNow.AddHours(48)),"Timer boundary incorrect");
    Check(timed.Remaining(timerNow.AddDays(3))==TimeSpan.Zero && timed.Countdown(timerNow.AddDays(3))=="00:00:00","Expired timer became negative");
    Check(timed.Countdown(timerNow.AddSeconds(.1))=="48:00:00" && timed.Countdown(timerNow.AddSeconds(1))=="47:59:59","Countdown rounding incorrect");
    Check(timed.IsReady([idA,idB],timerNow.AddHours(48).ToOffset(TimeSpan.FromHours(5))),"Time zone changed timer deadline");
    string idC=new('c',32);
    Check(!timed.IsReady([idA,idB,idC],timerNow.AddDays(3)) && !timed.IsReady([],timerNow.AddDays(3)),"New/missing account scope allowed green state");
    Check(timed.IsReady([idA],timerNow.AddDays(3)),"Removed account prevented completion for remaining accounts");
    var repeated=timedHistory.Remember("timer_nick",[idB,idA],timerNow.AddHours(1),false);
    Check(repeated.Friends.Length==1 && repeated.Friends[0].Key==timed.Key && repeated.Friends[0].StartedAt==timerNow,"Existing nick was duplicated or timer reset");
    Check(timedHistory.Remember("Timer_Nick",[idA,idB],timerNow.AddHours(1),true).Friends[0].StartedAt==timerNow.AddHours(1),"New actual request did not restart wait");
    Check(timedHistory.Remember("Timer_Nick",[idA,idB,idC],timerNow.AddHours(1),false).Friends[0].StartedAt==timerNow.AddHours(1),"Completing new account scope did not restart wait");
    var timerStore=Store("friend-timer-complete");Add(timerStore,StateA(),"A");Add(timerStore,StateB(),"B");
    var timerPlatform=new FakeEpic(StateA());var timerController=new EpicAccountController(timerStore,timerPlatform,()=>timerNow);
    var timerBatch=await timerController.AddFriendFromAllAsync("Timer_Nick",new FakeFriends(timerPlatform));
    var persisted=new EpicProfileStore(timerStore.Root).LoadFriendHistory().Friends.Single();
    Check(timerBatch.Restored && persisted.Nickname=="Timer_Nick" && persisted.AccountIds.Length==2 && persisted.StartedAt==timerNow,"Complete batch did not persist all-account timer");
    Check(!Encoding.UTF8.GetString(File.ReadAllBytes(timerStore.FriendHistoryPath)).Contains("Timer_Nick",StringComparison.Ordinal),"Nickname stored as plaintext");
    timerNow=timerNow.AddHours(3);
    await timerController.AddFriendFromAllAsync("Timer_Nick",new FakeFriends(timerPlatform){Attempt=new(EpicFriendStatus.Pending,"Already pending")});
    Check(timerStore.LoadFriendHistory().Friends.Single().StartedAt==persisted.StartedAt,"Repeated complete status check reset persisted timer");
    byte[] originalProfiles=File.ReadAllBytes(timerStore.LibraryPath);int processCalls=timerPlatform.Calls.Count;
    await timerController.RemoveTrackedFriendAsync(persisted.Key);
    Check(new EpicProfileStore(timerStore.Root).LoadFriendHistory().Friends.Length==0,"Deleted nickname survived restart");
    Check(timerPlatform.Calls.Count==processCalls && File.ReadAllBytes(timerStore.LibraryPath).SequenceEqual(originalProfiles),"Deleting timer changed Epic processes/profiles");
    var scopeStore=Store("friend-timer-new-autosaved-account");Add(scopeStore,StateA(),"A");Add(scopeStore,StateB(),"B");
    var scopePlatform=new FakeEpic(new(Ini("original-C"),null,idC));
    var scopeBatch=await new EpicAccountController(scopeStore,scopePlatform).AddFriendFromAllAsync("Scope_Nick",new FakeFriends(scopePlatform));
    Check(scopeBatch.Items.All(i=>i.Status==EpicFriendStatus.Sent) && scopeStore.Load().Profiles.Length==3 && scopeStore.LoadFriendHistory().Friends.Length==0,"New auto-saved account bypassed all-account requirement");
    var damagedStore=Store("friend-timer-corrupt");Add(damagedStore,StateA(),"A");File.WriteAllBytes(damagedStore.FriendHistoryPath,[1,2,3]);
    var damagedPlatform=new FakeEpic(StateA());
    var damagedBatch=await new EpicAccountController(damagedStore,damagedPlatform).AddFriendFromAllAsync("Safe_Nick",new FakeFriends(damagedPlatform));
    Check(damagedBatch.Items.Single().Status==EpicFriendStatus.Sent && damagedBatch.Restored && damagedBatch.HistoryNote.Contains("не удалось сохранить") && File.ReadAllBytes(damagedStore.FriendHistoryPath).SequenceEqual(new byte[]{1,2,3}),"History error lost request result or overwrote damaged data");
    await Expect<InvalidDataException>(()=>Task.Run(()=>EpicFriendHistory.Validate(timedHistory with {Friends=[timed,timed]})));
    await Expect<InvalidDataException>(()=>Task.Run(()=>EpicFriendHistory.Validate(timedHistory with {Friends=[timed with {AccountIds=[]}]})));
    var loginStart = new DateTimeOffset(2026,10,21,10,0,0,TimeSpan.Zero);
    string AuthLine(string message, int second=1) => "[2026.10.21-10.00."+second.ToString("00")+":123][100]LogSynthetic: "+message+"\n";
    string authSuccess = AuthLine("OnLoginComplete LocalUserNum=0 bWasSuccessful TRUE "+idA);
    string authReject = AuthLine("OnLoginComplete LocalUserNum=0 bWasSuccessful FALSE errors.com.epicgames.account.auth_token.invalid_refresh_token",2);
    Check(EpicLoginLog.Parse(authSuccess,loginStart).Matches(idA),"Confirmed Epic log login not recognized");
    Check(!EpicLoginLog.Parse(authSuccess,loginStart).Matches(idB),"Log authentication accepted wrong account");
    Check(EpicLoginLog.Parse(authSuccess,loginStart.AddSeconds(2)).Status==EpicLoginStatus.Unknown,"Prior process log established current login");
    Check(EpicLoginLog.Parse(authSuccess+authReject,loginStart).Status==EpicLoginStatus.Rejected,"Rejected login treated as authenticated");
    Check(EpicLoginLog.Parse(authReject+AuthLine("OnLoginComplete 0 TRUE "+idA,3),loginStart).Matches(idA),"Manual reauthentication did not clear rejection");
    Check(EpicLoginLog.Parse(AuthLine("OnLoginComplete 0 FALSE network error"),loginStart).Status==EpicLoginStatus.Failed,"Network failure treated as expired token");
    Check(EpicLoginLog.Parse(authSuccess+AuthLine("OnLoginPurchaseComplete 0 FALSE invalid_refresh_token",2),loginStart).Matches(idA),"Secondary purchase auth invalidated main login");
    Check(EpicLoginLog.Parse(AuthLine("OnLoginComplete TRUE missing-id"),loginStart).Status==EpicLoginStatus.Unknown,"Missing login account accepted");
    Check(EpicLoginLog.Parse(authSuccess+AuthLine("OnLogoutComplete 0 TRUE",2),loginStart).Status==EpicLoginStatus.Unknown,"Logout retained authenticated state");
    Check(EpicLoginLog.Parse("OnLoginComplete TRUE "+idA,loginStart).Status==EpicLoginStatus.Unknown,"Untimestamped log accepted");
    Check(!new EpicLoginObservation(EpicLoginStatus.Authenticated,idA).ToString().Contains(idA),"Observation diagnostic leaked account ID");
    string logPath=Path.Combine(root,"synthetic-auth.log");
    var logReader=new EpicLoginLog(logPath);
    Check(logReader.Read(loginStart).Status==EpicLoginStatus.Unknown,"Missing log inferred authentication");
    File.WriteAllText(logPath,authSuccess);
    Check(logReader.Read(loginStart).Matches(idA) && logReader.Read(loginStart).Matches(idA),"Cached log lost confirmed session");
    File.AppendAllText(logPath,authReject);
    Check(logReader.Read(loginStart).Status==EpicLoginStatus.Rejected,"Log append retained stale cached success");
    Check(logReader.Read(loginStart.AddSeconds(3)).Status==EpicLoginStatus.Unknown,"New launcher reused previous log session");

    var refreshStore=Store("background-rotation");var refreshProfile=Add(refreshStore,StateA(),"A");
    var refreshPlatform=new FakeEpic(rotated);var refreshController=new EpicAccountController(refreshStore,refreshPlatform);
    Check(await refreshController.RefreshSavedSessionAsync(),"Background refresh missed rotated credentials");
    Check(new EpicProfileStore(refreshStore.Root).Load().Profiles.Single().Ini.SequenceEqual(rotated.ActiveIni!),"Rotated credentials not durable across store restart");
    byte[] stableLibrary=File.ReadAllBytes(refreshStore.LibraryPath);
    Check(!await refreshController.RefreshSavedSessionAsync() && File.ReadAllBytes(refreshStore.LibraryPath).SequenceEqual(stableLibrary),"Unchanged credentials rewrote DPAPI library");
    refreshPlatform.SetState(StateA());refreshPlatform.LoginOverride=new(EpicLoginStatus.Rejected);
    Check(!await refreshController.RefreshSavedSessionAsync() && File.ReadAllBytes(refreshStore.LibraryPath).SequenceEqual(stableLibrary),"Rejected session overwrote useful profile");
    await Expect<InvalidOperationException>(()=>refreshController.SaveCurrentAsync("bad-overwrite"));
    refreshPlatform.LoginOverride=EpicLoginObservation.Unknown;
    Check(!await refreshController.RefreshSavedSessionAsync(),"Unknown session overwrote useful profile");
    refreshPlatform.LoginOverride=new(EpicLoginStatus.Authenticated,idB);
    Check(!await refreshController.RefreshSavedSessionAsync(),"Different authenticated ID overwrote saved profile");
    refreshPlatform.LoginOverride=null;refreshPlatform.SetState(loggedOut);
    Check(!await refreshController.RefreshSavedSessionAsync(),"Login placeholder entered background save");
    refreshPlatform.SetState(StateB());
    Check(!await refreshController.RefreshSavedSessionAsync() && refreshStore.Load().Profiles.Length==1,"Background monitor added an unknown account");
    refreshStore.SaveRecovery(new(1,StateA(),"fake"));refreshPlatform.SetState(StateA());
    Check(!await refreshController.RefreshSavedSessionAsync() && File.ReadAllBytes(refreshStore.LibraryPath).SequenceEqual(stableLibrary),"Monitor changed pending recovery");
    refreshStore.ClearRecovery();
    var afterStartStore=Store("after-start-rotation");Add(afterStartStore,StateA(),"A");var afterStartB=Add(afterStartStore,StateB(),"B");
    var afterStartPlatform=new FakeEpic(StateA()) { OnStart=p=>p.SetState(p.Capture() with {EditorIni=Ini("rotated-after-start-"+p.CurrentAccountId![0])}) };
    await new EpicAccountController(afterStartStore,afterStartPlatform).SwitchAsync(afterStartB.Key);
    Check(afterStartStore.Load().Profiles.Single(p=>p.AccountId==idB).Ini.SequenceEqual(afterStartPlatform.Capture().ActiveIni!),"Login completion did not persist refreshed target");
    var delayedIniStore=Store("delayed-rememberme");Add(delayedIniStore,StateA(),"A");var delayedIniB=Add(delayedIniStore,StateB(),"B");
    int delayedCaptures=0;
    var delayedIniPlatform=new FakeEpic(StateA()){OnStart=p=>
    {
        p.SetState(new(shortLogin,null,idB));
        p.BeforeCapture=x=>{if(++delayedCaptures==4)x.SetState(StateB() with {EditorIni=Ini("delayed-fresh-B")});};
    }};
    await new EpicAccountController(delayedIniStore,delayedIniPlatform).SwitchAsync(delayedIniB.Key);
    Check(delayedIniStore.Load().Profiles.Single(p=>p.AccountId==idB).Ini.SequenceEqual(Ini("delayed-fresh-B")),"Confirmed account with delayed INI was mistaken for another account");
    var rejectedStore=Store("rejected-switch");Add(rejectedStore,StateA(),"A");var rejectedB=Add(rejectedStore,StateB(),"B");
    var rejectedPlatform=new FakeEpic(StateA()){OnStart=p=>p.LoginOverride=new(EpicLoginStatus.Rejected)};
    await Expect<InvalidOperationException>(()=>new EpicAccountController(rejectedStore,rejectedPlatform).SwitchAsync(rejectedB.Key));
    Check(rejectedPlatform.CurrentAccountId==idB && rejectedPlatform.IsRunning && !rejectedStore.NeedsRecovery,"Rejected auth rolled back local files or closed manual login");
    Check(rejectedStore.Load().Profiles.Single(p=>p.AccountId==idB).Ini.SequenceEqual(iniB),"Rejected target profile was replaced");
    var rejectedFriends=new FakeFriends(rejectedPlatform);
    var rejectedBatch=await new EpicAccountController(rejectedStore,rejectedPlatform).AddFriendFromAllAsync("Exact_Nick",rejectedFriends);
    Check(rejectedFriends.Senders.Count==0 && rejectedBatch.Items.All(i=>i.Status!=EpicFriendStatus.Sent),"Rejected login advanced into friend requests");
    Check(!rejectedBatch.Restored && !rejectedBatch.Summary.Contains("Нажмите «Восстановить Epic»") && rejectedBatch.Summary.Contains("вручную"),"Auth rejection offered nonexistent recovery file");
    var restoredRotationStore=Store("rotated-restore");Add(restoredRotationStore,StateA(),"A");Add(restoredRotationStore,StateB(),"B");
    var restoredRotationPlatform=new FakeEpic(StateA()){OnStart=p=>p.SetState(p.Capture() with {EditorIni=Ini("restore-fresh-"+p.CurrentAccountId![0])})};
    var restoredRotationResult=await new EpicAccountController(restoredRotationStore,restoredRotationPlatform).AddFriendFromAllAsync("Exact_Nick",new FakeFriends(restoredRotationPlatform));
    Check(restoredRotationResult.Restored && restoredRotationStore.Load().Profiles.Single(p=>p.AccountId==idA).Ini.SequenceEqual(restoredRotationPlatform.Capture().ActiveIni!),"Original account's post-restore refresh token was lost");
    var windowReady=new EpicWindowReadiness();
    EpicWindowCandidate splash=new(1,new(480,270)),mainWindow=new(2,new(1920,1050));
    Check(windowReady.Observe([splash],TimeSpan.Zero)==0,"Splash window accepted for capture");
    Check(windowReady.Observe([splash,mainWindow],TimeSpan.FromMilliseconds(100))==0,"Main window accepted before settling");
    Check(windowReady.Observe([mainWindow],TimeSpan.FromMilliseconds(599))==0,"Window stabilization boundary was early");
    Check(windowReady.Observe([mainWindow],TimeSpan.FromMilliseconds(600))==2,"Main window behind splash was not selected");
    Check(windowReady.Observe([mainWindow,mainWindow],TimeSpan.FromMilliseconds(700))==2,"Duplicate window handle caused ambiguity");
    var replaced=mainWindow with {Handle=3};
    Check(windowReady.Observe([replaced],TimeSpan.FromMilliseconds(800))==0,"Replacement HWND bypassed readiness");
    Check(windowReady.Observe([replaced],TimeSpan.FromMilliseconds(1300))==3,"Replacement HWND never became ready");
    var resized=replaced with {ClientSize=new(1546,830)};
    Check(windowReady.Observe([resized],TimeSpan.FromMilliseconds(1400))==0,"Resize bypassed stability delay");
    Check(windowReady.Observe([],TimeSpan.FromMilliseconds(1800))==0,"Vanished window retained ready state");
    Check(windowReady.Observe([resized],TimeSpan.FromMilliseconds(1900))==0,"Vanished/reappeared window did not settle again");
    Check(windowReady.Observe([resized],TimeSpan.FromMilliseconds(2400))==3,"Reappeared window never became ready");
    await Expect<InvalidOperationException>(()=>Task.Run(()=>windowReady.Observe([mainWindow,replaced],TimeSpan.FromSeconds(3))));
    Check(windowReady.Observe([new(0,new(1920,1080)),splash],TimeSpan.FromSeconds(4))==0,"Null HWND accepted");
    foreach(var size in new[]{new Size(950,600),new Size(1920,1080),new Size(3840,2160),new Size(7680,2160),new Size(7680,4320)})
        Check(EpicCaptureGeometry.IsUsable(size),"Valid fullscreen capture rejected: "+size);
    foreach(var size in new[]{Size.Empty,new Size(949,600),new Size(950,599),new Size(320,200),new Size(20000,10000)})
        Check(!EpicCaptureGeometry.IsUsable(size),"Unsafe capture size accepted: "+size);
    Check(EpicCaptureGeometry.Problem(new(320,200)).Contains("320×200"),"Small-window diagnostic omitted measured size");
    Check(EpicCaptureGeometry.Problem(new(20000,10000)).Contains("слишком велика") && !EpicCaptureGeometry.Problem(new(20000,10000)).Contains("Разверните"),"Oversized capture asked to enlarge window");
    Check(EpicCaptureGeometry.Problem(new(1920,1080)).Contains("стабильным"),"Unstable large window described as too small");
    Check(EpicCaptureGeometry.OcrSize(new(1546,830),3200)==new Size(1546,830),"Normal OCR image unnecessarily resized");
    Check(EpicCaptureGeometry.OcrSize(new(3840,2160),3200)==new Size(3200,1800),"4K OCR scaling incorrect");
    Check(EpicCaptureGeometry.OcrSize(new(7680,2160),3200)==new Size(3200,900),"Ultrawide OCR scaling incorrect");
    Check(EpicCaptureGeometry.OcrSize(new(2160,3840),3200)==new Size(1800,3200),"Portrait OCR scaling incorrect");
    Check(EpicCaptureGeometry.RestoreOcrBounds(new(100,200,100,50),new(3840,2160),new(3200,1800))==new Rectangle(120,240,120,60),"OCR coordinates did not map back to original pixels");
    Check(EpicCaptureGeometry.RestoreOcrBounds(new(-20,-20,4000,2000),new(3840,2160),new(3200,1800))==new Rectangle(0,0,3840,2160),"OCR coordinate clamp escaped source");
    Console.WriteLine($"PASS: {passed} Epic checks, including stable main-window selection, fullscreen geometry, OCR coordinate scaling, authentication rotation and existing profile/friend/timer safeguards. Synthetic platform/images and own process only; no real friend requests.");
}

finally
{
    string allowed = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture cleanup path");
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

internal sealed class FakeEpic(EpicLocalState state) : IEpicPlatform
{
    internal EpicLoginObservation? LoginOverride;
    internal Action<FakeEpic>? OnStart;
    internal Action<FakeEpic>? BeforeCapture;
    public EpicLoginObservation ReadLogin() => LoginOverride ?? new(EpicLoginStatus.Authenticated, state.AccountId);
    internal void SetState(EpicLocalState next) => state = next;
    internal readonly List<string> Calls = [];
    internal bool FailStop, FailStart, FailResolve;
    internal int FailApplyCount, IgnoreApplyCount;
    internal Task? StopWait;
    internal EpicLocalState? StateAfterStop;
    public string? CurrentAccountId => state.AccountId;
    public bool IsRunning { get; private set; } = true;
    internal void SetRunning(bool running) => IsRunning = running;
    public EpicLocalState Capture() { BeforeCapture?.Invoke(this); return state; }
    public string ResolveLauncher(string configured) => FailResolve ? throw new InvalidOperationException("Invalid launcher") : "fake-EpicGamesLauncher.exe";
    public async Task StopAsync(string launcher)
    {
        Calls.Add("stop"); if (FailStop) throw new InvalidOperationException("Stop failed");
        if (StopWait is not null) await StopWait; IsRunning = false;
        if (StateAfterStop is not null) { state = StateAfterStop; StateAfterStop = null; }
    }
    public void Apply(EpicLocalState next)
    {
        Calls.Add("apply");
        if (IgnoreApplyCount-- > 0) return;
        if (FailApplyCount-- > 0) { state = next with { LegacyIni = Encoding.UTF8.GetBytes("partial-write") }; throw new IOException("Partial synthetic write"); }
        state = next;
    }
    public Task StartAsync(string launcher)
    { Calls.Add("start"); if (FailStart) throw new InvalidOperationException("Start failed"); IsRunning = true; OnStart?.Invoke(this); return Task.CompletedTask; }
}

internal sealed class MemoryRegistry : IEpicAccountRegistry
{
    private string? id;
    public string? Read() => id;
    public void Write(string? value) => id = value;
}
internal sealed class FakeFriends(FakeEpic platform) : IEpicFriends
{
    internal readonly List<string> Senders = [], Names = [];
    internal bool FailAvailability;
    internal int SearchChecks,StatusChecks;
    internal Action? OnSend;
    internal Task? Wait;
    internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal EpicFriendAttempt Attempt = new(EpicFriendStatus.Sent, "Synthetic request acknowledged");
    public Task CheckAvailableAsync(CancellationToken cancellation) => FailAvailability ? throw new InvalidOperationException("OCR unavailable") : Task.CompletedTask;
    public Task<string> CheckSearchAsync(string launcher, CancellationToken cancellation) { SearchChecks++; return Task.FromResult("Synthetic search opened; no requests."); }
    public Task<string> CheckStatusAsync(string launcher,string nickname,CancellationToken cancellation) { StatusChecks++; return Task.FromResult("Synthetic existing request; no sends."); }
    public async Task<EpicFriendAttempt> SendAsync(string launcher, string accountId, string nickname, CancellationToken cancellation)
    {
        if (!platform.IsRunning || platform.CurrentAccountId != accountId) throw new Exception("Wrong sender before friend operation");
        Senders.Add(accountId); Names.Add(nickname); Entered.TrySetResult();
        if (Wait is not null) await Wait;
        OnSend?.Invoke(); return Attempt;
    }
}
// The actual process-launch boundary is replaced in this console test project.
internal static class AppWindows
{ internal static void Launch(string path) => throw new Exception("Real application launch is forbidden in Epic unit tests."); }
