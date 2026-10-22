using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VpnZapretSwitch;

public sealed class WindowsPlatform(Settings settings, Action<string>? report = null) : ISwitchPlatform
{
    private TimeSpan Timeout => TimeSpan.FromSeconds(settings.TimeoutSeconds);
    private NativeService Service => new(settings.ServiceName);

    public void Validate() => ValidateComponents(true, true);
    internal void ValidateVpn() => ValidateComponents(true, false);
    internal void ValidateZapret() => ValidateComponents(false, true);

    private void ValidateComponents(bool vpn, bool zapret)
    {
        if (settings.TimeoutSeconds is < 10 or > 180)
            throw new InvalidOperationException("TimeoutSeconds должен быть от 10 до 180.");
        if (vpn)
        {
        _ = VpnDns.Address(settings.DnsProvider);
        if (settings.VpnMode is not ("embedded" or "incy")) throw new InvalidOperationException("Выберите встроенный VPN или INCY.");
        if (settings.VpnMode == "embedded") CoreCatalog.Verify();
        else
        {
            if (!Path.IsPathFullyQualified(settings.IncyExe) || !File.Exists(settings.IncyExe))
                throw new InvalidOperationException("Укажите полный путь к установленному incy.exe.");
            if (!File.Exists(settings.CoreExe) && !File.Exists(Path.Combine(Path.GetDirectoryName(settings.CoreExe)!, "incycore.dll")))
                throw new InvalidOperationException("В установке INCY не найдено xray.exe или incycore.dll.");
        }
        }
        if (!zapret) return;
        if (!Path.IsPathFullyQualified(settings.ZapretFolder) || !File.Exists(settings.WinwsExe))
            throw new InvalidOperationException("В папке Zapret должен находиться bin\\winws.exe.");
        if (string.IsNullOrWhiteSpace(settings.ServiceName) || settings.ServiceName.IndexOfAny(['/', '\\']) >= 0)
            throw new InvalidOperationException("Недопустимое имя службы Zapret.");
        if (settings.ZapretMode is not ("bat" or "service"))
            throw new InvalidOperationException("Выберите режим Zapret: .bat или service.bat.");
        if (settings.ZapretMode == "service")
        {
            if (settings.ServiceName != "zapret" || !File.Exists(settings.ServiceScript))
                throw new InvalidOperationException("Режим service.bat требует service.bat в папке Zapret и имя службы zapret.");
            if (settings.ServiceScript.IndexOfAny(['\"', '%', '!', '\r', '\n']) >= 0)
                throw new InvalidOperationException("Путь к service.bat не должен содержать кавычки, %, ! или переводы строк.");
            string script = File.ReadAllText(settings.ServiceScript);
            if (!script.Contains("Select option (0-11):", StringComparison.Ordinal)
                || !script.Contains("Input file index (number):", StringComparison.Ordinal)
                || !script.Contains("7. Update IPSet List", StringComparison.Ordinal))
                throw new InvalidOperationException("Меню service.bat отличается от поддерживаемого. Автоматический ввод отключён; используйте кнопку «Открыть».");
        }
        if (Service.GetState() is not null)
        {
            // Refuse to control an unrelated service with the same name.
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + settings.ServiceName);
            string image = Environment.ExpandEnvironmentVariables(key?.GetValue("ImagePath") as string ?? "").Trim();
            string executable = image.StartsWith('"')
                ? image[1..].Split('"')[0]
                : image.Split(' ', 2)[0];
            if (!SamePath(executable, settings.WinwsExe))
                throw new InvalidOperationException("Служба Zapret использует другую папку. Укажите её в настройках.");
        }
        if (settings.ZapretMode == "service" || Service.GetState() is null)
        {
            if (string.IsNullOrWhiteSpace(settings.StrategyFile)
                || settings.StrategyFile != Path.GetFileName(settings.StrategyFile)
                || !settings.StrategyFile.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                || settings.StrategyFile.StartsWith("service", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(settings.StrategyPath))
                throw new InvalidOperationException("Выберите существующий .bat со стратегией Zapret (например, general (ALT).bat).");
            // cmd.exe expands these even inside quotes; reject them in configurable batch paths.
            if (settings.StrategyPath.IndexOfAny(['"', '%', '!', '\r', '\n']) >= 0)
                throw new InvalidOperationException("Путь к .bat не должен содержать кавычки, %, ! или переводы строк.");
        }
    }

    public IncyRuntimeStatus ReadIncyStatus() => settings.VpnMode == "embedded"
        ? EmbeddedVpnHost.Shared.ReadStatus() : new IncyProbe(settings, ExactProcesses).Read();

    private void RequireExternalVpnOff()
    {
        var external = new IncyProbe(settings, ExactProcesses).Read();
        if (external.IsRunning || external.State == VpnState.Unknown)
            throw new InvalidOperationException("Отключите VPN в INCY вручную перед использованием встроенного клиента или запуском Zapret.");
    }
    public void EnsureExternalVpnOff() { if (settings.VpnMode == "embedded") RequireExternalVpnOff(); }

    public bool IsVpnRunning()
    {
        var status = ReadIncyStatus();
        if (status.State == VpnState.Unknown)
            throw new InvalidOperationException(status.Detail + ". Проверьте INCY вручную.");
        return status.IsRunning;
    }

    public RuntimeStatus ReadRuntimeStatus()
    {
        IncyRuntimeStatus incy;
        ZapretRuntimeStatus zapret;
        try { incy = ReadIncyStatus(); }
        catch (Exception ex) { incy = new(false, false, VpnState.Unknown, ex.Message); }
        try
        {
            uint? state = Service.GetState();
            bool process = ExactProcesses(settings.WinwsExe).Length > 0;
            bool running = process || state is not null && state != NativeService.Stopped;
            string title = state switch
            {
                NativeService.StartPending => "Запускается…", NativeService.StopPending => "Останавливается…",
                _ => running ? "Работает" : "Остановлен"
            };
            zapret = new(running, state is not null, title,
                state is not null ? "Используется стратегия службы" : settings.StrategyFile)
            {
                ZapretService = state == NativeService.Running,
                WinDivertService = ReadDriverStatus(),
                BypassProcess = process
            };
        }
        catch (Exception ex) { zapret = new(null, false, "Неизвестно", ex.Message); }
        return new(incy, zapret);
    }

    private static bool? ReadDriverStatus()
    {
        try
        {
            // WinDivert64 is used by some distributions; a driver may remain loaded
            // after winws stops, so its state is deliberately independent of Bypass.
            return new NativeService("WinDivert").GetState() == NativeService.Running
                || new NativeService("WinDivert64").GetState() == NativeService.Running;
        }
        catch { return null; }
    }

    public bool IsZapretRunning()
    {
        uint? state = Service.GetState();
        return state is not null && state != NativeService.Stopped
            || ExactProcesses(settings.WinwsExe).Length > 0;
    }

    public async Task StopZapretAsync()
    {
        uint? state = Service.GetState();
        if (state is not null && state != NativeService.Stopped)
        {
            if (state == NativeService.StartPending)
                await WaitAsync(() => Service.GetState() != NativeService.StartPending, "Служба Zapret не завершила запуск.");
            state = Service.GetState();
            if (state != NativeService.Stopped && state != NativeService.StopPending)
                Service.Stop();
            await WaitAsync(() => Service.GetState() is null or NativeService.Stopped, "Служба Zapret не остановилась.");
        }
        // Manual .bat runs are limited to this exact executable, never all winws processes.
        foreach (int id in ExactProcesses(settings.WinwsExe))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                // Recheck identity immediately before terminating (PID reuse / external exit).
                if (!SamePath(process.MainModule?.FileName, settings.WinwsExe)) continue;
                process.Kill(entireProcessTree: false);
            }
            catch (ArgumentException) { /* Process exited between enumeration and lookup. */ }
            catch (InvalidOperationException) { /* Process already exited. */ }
        }
        await WaitAsync(() => !IsZapretRunning(), "Zapret снова запускается или не остановился.");
    }

    public async Task StartZapretAsync()
    {
        if (IsVpnRunning())
            throw new InvalidOperationException("Ядро INCY ещё работает. Zapret не запущен.");
        if (settings.VpnMode == "embedded") RequireExternalVpnOff();
        if (settings.ZapretMode == "service")
        {
            await ZapretServiceSession.InstallAndUpdateAsync(settings, report);
            await WaitAsync(() => ExactProcesses(settings.WinwsExe).Length > 0, "Служба установлена, но winws.exe не работает.");
            await RequireStableAsync(IsZapretRunning, "Zapret завершился после установки службы.", 1500);
            return;
        }
        uint? state = Service.GetState();
        if (state is not null)
        {
            if (state == NativeService.StopPending)
                await WaitAsync(() => Service.GetState() == NativeService.Stopped, "Служба Zapret ещё останавливается.");
            state = Service.GetState();
            if (state == NativeService.Stopped) Service.Start();
            await WaitAsync(() => Service.GetState() == NativeService.Running, "Служба Zapret не запустилась.");
        }
        else if (ExactProcesses(settings.WinwsExe).Length == 0)
        {
            // service.bat is interactive. Launch the chosen strategy with its own working folder.
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = false,
                WorkingDirectory = settings.ZapretFolder,
                Arguments = "/d /v:off /s /c \"\"" + settings.StrategyPath + "\"\""
            };
            start.Environment["NO_UPDATE_CHECK"] = "1";
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Не удалось открыть стратегию Zapret.");
        }
        await WaitAsync(() => ExactProcesses(settings.WinwsExe).Length > 0, "winws.exe не запустился. Посмотрите окно стратегии Zapret.");
        // Reject short-lived crashes instead of showing a successful switch.
        await RequireStableAsync(() => IsZapretRunning(), "Zapret завершился сразу после запуска.", 1500);
    }

    public async Task ConnectVpnAsync()
    {
        if (IsZapretRunning()) throw new InvalidOperationException("Zapret всё ещё работает.");
        if (settings.VpnMode == "embedded")
        {
            RequireExternalVpnOff();
            await EmbeddedVpnHost.Shared.ConnectAsync(settings, report);
            if (IsZapretRunning()) throw new InvalidOperationException("Zapret запустился во время подключения встроенного VPN.");
            return;
        }
        if (IncyDesktopControl.NeedsShortcut(settings.IncyExe))
        {
            report?.Invoke("INCY 3.8.10: фокус на содержимом окна и физические клавиши Ctrl+K…");
            await IncyDesktopControl.ConnectAsync(settings.IncyExe, () => ExactProcesses(settings.IncyExe), ReadIncyStatus, report);
        }
        else SendIncyCommand("connect");
        await WaitAsync(() => ReadIncyStatus().State == VpnState.Active,
            "INCY не подтвердил подключение. Проверьте выбранный сервер и сообщения в окне INCY.");
        await RequireStableAsync(() => ReadIncyStatus().State == VpnState.Active, "Подключение INCY прервалось после запуска.", 2500);
        if (IsZapretRunning())
            throw new InvalidOperationException("Zapret запустился снова во время подключения INCY.");
    }

    public async Task DisconnectVpnAsync()
    {
        if (settings.VpnMode == "embedded") { await EmbeddedVpnHost.Shared.DisconnectAsync(); return; }
        SendIncyCommand("disconnect");
        await WaitAsync(() => !IsVpnRunning(), "Ядро INCY не остановилось. Проверьте приложение INCY.");
        await RequireStableAsync(() => !IsVpnRunning(), "Ядро INCY запустилось повторно.", 2500);
    }

    private void SendIncyCommand(string command)
    {
        var start = new ProcessStartInfo(settings.IncyExe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(settings.IncyExe)!
        };
        start.ArgumentList.Add("incy://" + command);
        using var process = Process.Start(start);
        // Process lifetime is NOT used as evidence of VPN connectivity.
    }

    private async Task WaitAsync(Func<bool> predicate, string error)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed >= Timeout) throw new TimeoutException(error);
            await Task.Delay(250);
        }
    }

    private static async Task RequireStableAsync(Func<bool> predicate, string error, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            if (!predicate()) throw new InvalidOperationException(error);
            await Task.Delay(250);
        }
    }

    internal static int[] ExactProcesses(string executable)
    {
        var result = new List<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                try
                {
                    if (SamePath(process.MainModule?.FileName, executable)) result.Add(process.Id);
                }
                catch (InvalidOperationException) { /* Already exited. */ }
                catch (Win32Exception ex)
                {
                    if (ex.NativeErrorCode == 5)
                        throw new InvalidOperationException("Не удалось проверить путь процесса. Запустите программу с правами администратора.", ex);
                    if (!process.HasExited) throw;
                }
            }
        }
        return result.ToArray();
    }

    private static bool SamePath(string? first, string second) => !string.IsNullOrEmpty(first)
        && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
}

internal sealed class NativeService(string name)
{
    public const uint Stopped = 1, StartPending = 2, StopPending = 3, Running = 4;
    private const uint QueryStatus = 4, StartAccess = 0x10, StopAccess = 0x20;

    public uint? GetState()
    {
        IntPtr manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw Error("Открытие диспетчера служб");
        try
        {
            IntPtr service = OpenService(manager, name, QueryStatus);
            if (service == IntPtr.Zero)
            {
                int code = Marshal.GetLastWin32Error();
                if (code == 1060) return null;
                throw new Win32Exception(code, "Не удалось прочитать состояние службы " + name);
            }
            try
            {
                if (!QueryServiceStatus(service, out var status)) throw Error("Чтение состояния " + name);
                return status.CurrentState;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public void Start() => Control(true);
    public void Stop() => Control(false);

    private void Control(bool start)
    {
        IntPtr manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw Error("Открытие диспетчера служб");
        try
        {
            IntPtr service = OpenService(manager, name, start ? StartAccess : StopAccess);
            if (service == IntPtr.Zero) throw Error("Открытие службы " + name);
            try
            {
                bool ok = start ? StartService(service, 0, IntPtr.Zero) : ControlService(service, 1, out _);
                if (!ok)
                {
                    int code = Marshal.GetLastWin32Error();
                    if (start && code == 1056 || !start && code == 1062) return;
                    throw new Win32Exception(code, "Не удалось " + (start ? "запустить " : "остановить ") + name);
                }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static Win32Exception Error(string action) => new(Marshal.GetLastWin32Error(), action);

    [StructLayout(LayoutKind.Sequential)]
    private struct Status
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out Status status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(IntPtr service, uint count, IntPtr args);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint command, out Status status);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
