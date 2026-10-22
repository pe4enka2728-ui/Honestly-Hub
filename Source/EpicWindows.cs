using System.Diagnostics;
using Microsoft.Win32;

namespace VpnZapretSwitch;

internal interface IEpicAccountRegistry
{
    string? Read();
    void Write(string? accountId);
}
internal sealed class EpicWindows : IEpicPlatform
{
    private const string RegistryPath = @"Software\Epic Games\Unreal Engine\Identifiers";
    private readonly string configRoot;
    private readonly IEpicAccountRegistry accountRegistry;
    private readonly EpicLoginLog loginLog;
    internal EpicWindows(string? configRoot = null, IEpicAccountRegistry? accountRegistry = null)
    {
        this.configRoot = Path.GetFullPath(configRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpicGamesLauncher", "Saved", "Config"));
        this.accountRegistry = accountRegistry ?? new EpicRegistry();
        loginLog = new(Path.Combine(Directory.GetParent(this.configRoot)!.FullName, "Logs", "EpicGamesLauncher.log"));
    }
    private string EditorPath => Path.Combine(configRoot, "WindowsEditor", "GameUserSettings.ini");
    private string LegacyPath => Path.Combine(configRoot, "Windows", "GameUserSettings.ini");
    public string? CurrentAccountId => accountRegistry.Read();
    private sealed class EpicRegistry : IEpicAccountRegistry
    {
        public string? Read()
        {
            using var registry = Registry.CurrentUser.OpenSubKey(RegistryPath);
            object? id = registry?.GetValue("AccountId", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (id is not null and not string) throw new InvalidDataException("Неизвестный формат AccountId Epic. Локальные данные не изменены.");
            return id as string;
        }
        public void Write(string? id)
        {
            if (id is null)
            {
                using var existing = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
                existing?.DeleteValue("AccountId", throwOnMissingValue: false); existing?.Flush();
            }
            else
            {
                using var registry = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
                registry.SetValue("AccountId", id, RegistryValueKind.String); registry.Flush();
            }
        }
    }
    public EpicLocalState Capture() => new(ReadIni(EditorPath), ReadIni(LegacyPath), accountRegistry.Read());
    private static byte[]? ReadIni(string path) => File.Exists(path) ? EpicProfileStore.ReadBounded(path, EpicProfileStore.MaxIni) : null;
    public void Apply(EpicLocalState state)
    {
        EpicProfileStore.ValidateState(state);
        WriteIni(EditorPath, state.EditorIni); WriteIni(LegacyPath, state.LegacyIni); accountRegistry.Write(state.AccountId);
    }
    private static void WriteIni(string path, byte[]? bytes)
    {
        if (bytes is null) { if (File.Exists(path)) File.Delete(path); }
        else EpicProfileStore.AtomicWrite(path, bytes);
    }
    public string ResolveLauncher(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return ValidateLauncher(configured);
        var running = FindLaunchers();
        try { foreach (var process in running) if (EpicProcessImage.Read(process) is { } path) return ValidateLauncher(path); }
        finally { foreach (var process in running) process.Dispose(); }
        foreach (var baseFolder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }.Distinct())
            foreach (string architecture in new[] { "Win64", "Win32" })
            {
                string candidate = Path.Combine(baseFolder, "Epic Games", "Launcher", "Portal", "Binaries", architecture, "EpicGamesLauncher.exe");
                if (File.Exists(candidate)) return ValidateLauncher(candidate);
            }
        throw new FileNotFoundException("Не найден Epic Games Launcher. Укажите путь к EpicGamesLauncher.exe во вкладке «Аккаунты».");
    }
    private static string ValidateLauncher(string path)
    {
        path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        if (!Path.GetFileName(path).Equals("EpicGamesLauncher.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException("Выберите существующий EpicGamesLauncher.exe.");
        return path;
    }
    public bool IsRunning
    {
        get { var found = FindLaunchers(); try { return found.Count > 0; } finally { foreach (var process in found) process.Dispose(); } }
    }
    public EpicLoginObservation ReadLogin()
    {
        var found = FindLaunchers();
        try
        {
            if (found.Count == 0) return EpicLoginObservation.Unknown;
            // The event must belong to these running processes, not a previous launcher run.
            var started = found.Min(process => new DateTimeOffset(process.StartTime.ToUniversalTime()));
            return loginLog.Read(started);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return EpicLoginObservation.Unknown; }
        finally { foreach (var process in found) process.Dispose(); }
    }
    private static List<Process> FindLaunchers(string? expected = null)
    {
        var result = new List<Process>();
        using var ownProcess = Process.GetCurrentProcess(); int session = ownProcess.SessionId;
        try
        {
            foreach (var process in Process.GetProcessesByName("EpicGamesLauncher"))
            {
                bool keep = false;
                try
                {
                    if (process.HasExited || process.SessionId != session) continue;
                    string? path = EpicProcessImage.Read(process);
                    if (path is null) continue;
                    if (!Path.GetFileName(path).Equals("EpicGamesLauncher.exe", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Не удалось проверить процесс Epic. Закройте лаунчер вручную и повторите.");
                    if (expected is not null && !LauncherFamily(path).Equals(LauncherFamily(expected), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Работает Epic из другой папки. Укажите его путь или закройте лаунчер вручную.");
                    result.Add(process); keep = true;
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                finally { if (!keep) process.Dispose(); }
            }
            return result;
        }
        catch { foreach (var process in result) process.Dispose(); throw; }
    }
    internal static string LauncherFamily(string path)
    {
        path = Path.GetFullPath(path);
        var architecture = Directory.GetParent(path);
        return architecture is not null && (architecture.Name.Equals("Win32", StringComparison.OrdinalIgnoreCase) || architecture.Name.Equals("Win64", StringComparison.OrdinalIgnoreCase)) && architecture.Parent?.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) == true
            ? architecture.Parent.FullName : path;
    }
    internal static bool IsLauncherHelperPath(string path, string launcher)
    {
        string family = LauncherFamily(launcher);
        var portal = Directory.GetParent(family);
        if (portal?.Name.Equals("Portal", StringComparison.OrdinalIgnoreCase) != true || portal.Parent is null) return false;
        string root = portal.Parent.FullName; path = Path.GetFullPath(path);
        return new[] { "Engine", "Portal" }.Any(folder => new[] { "Win32", "Win64" }.Any(architecture =>
            path.Equals(Path.Combine(root, folder, "Binaries", architecture, "EpicWebHelper.exe"), StringComparison.OrdinalIgnoreCase)));
    }
    private static List<Process> FindHelpers(string launcher)
    {
        var result = new List<Process>();
        using var ownProcess = Process.GetCurrentProcess(); int session = ownProcess.SessionId;
        try
        {
            foreach (var process in Process.GetProcessesByName("EpicWebHelper"))
            {
                bool keep = false;
                try
                {
                    if (process.HasExited || process.SessionId != session) continue;
                    if (EpicProcessImage.Read(process) is { } path && IsLauncherHelperPath(path, launcher))
                    { result.Add(process); keep = true; }
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                finally { if (!keep) process.Dispose(); }
            }
            return result;
        }
        catch { foreach (var process in result) process.Dispose(); throw; }
    }
    public async Task StopAsync(string launcher)
    {
        var found = FindLaunchers(launcher);
        try
        {
            foreach (var process in found) IfLive(process, () => { _ = process.CloseMainWindow(); });
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (found.Any(process => !process.HasExited) && DateTime.UtcNow < deadline) await Task.Delay(150).ConfigureAwait(false);
            foreach (var process in found) IfLive(process, () => process.Kill(entireProcessTree: false)); // Games and unrelated child processes are never terminated.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            foreach (var process in found) await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw new InvalidOperationException("Epic не завершился за отведённое время. Файлы входа не заменены."); }
        finally { foreach (var process in found) process.Dispose(); }
        // Only the browser helper from this launcher installation and Windows session.
        // No process-tree kill: game/EOS/overlay processes belong to running games too.
        var helpers = FindHelpers(launcher);
        try
        {
            foreach (var helper in helpers) IfLive(helper, () => helper.Kill(entireProcessTree: false));
            using var helperTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach (var helper in helpers) await helper.WaitForExitAsync(helperTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw new InvalidOperationException("Фоновый процесс Epic не завершился. Закройте Epic полностью и повторите; данные входа не заменены."); }
        finally { foreach (var helper in helpers) helper.Dispose(); }
        var remaining = FindLaunchers(launcher);
        try { if (remaining.Count > 0) throw new InvalidOperationException("Epic запустился повторно во время переключения. Закройте его вручную."); }
        finally { foreach (var process in remaining) process.Dispose(); }
        var remainingHelpers = FindHelpers(launcher);
        try { if (remainingHelpers.Count > 0) throw new InvalidOperationException("EpicWebHelper запустился повторно. Закройте Epic полностью перед переключением."); }
        finally { foreach (var helper in remainingHelpers) helper.Dispose(); }
    }
    public async Task StartAsync(string launcher)
    {
        launcher = ValidateLauncher(launcher);
        var existing = FindLaunchers(launcher);
        try { if (existing.Count > 0) return; } finally { foreach (var process in existing) process.Dispose(); }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { AppWindows.Launch(launcher); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "Honestly Epic launch" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false);
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var found = FindLaunchers(launcher);
            try { if (found.Count > 0) return; } finally { foreach (var process in found) process.Dispose(); }
            await Task.Delay(250).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Epic не подтвердил запуск процесса.");
    }
    private static void IfLive(Process process, Action action)
    {
        if (process.HasExited) return;
        try { action(); }
        catch (InvalidOperationException) when (process.HasExited) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }
}
