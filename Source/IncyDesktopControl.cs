using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VpnZapretSwitch;

internal static class IncyDesktopControl
{
    // INCY 3.8.10's connect URI emits a notification without starting its VPN service.
    // Its window's Ctrl+K handler calls the same connect action as the main connect button.
    public static bool NeedsShortcut(string executable)
    {
        string configuration = Path.Combine(Path.GetDirectoryName(executable)!, "app", "incy.cfg");
        return File.Exists(configuration) && File.ReadLines(configuration)
            .Any(line => line.Trim() == "java-options=-Djpackage.app-version=3.8.10");
    }

    public static async Task ConnectAsync(string executable, Func<int[]> processes, Func<IncyRuntimeStatus> readStatus,
        Action<string>? report = null)
    {
        var before = readStatus();
        if (before.IsRunning) return;
        if (before.State == VpnState.Unknown) throw new InvalidOperationException(before.Detail);
        IntPtr window = FindWindow(processes());
        if (window == IntPtr.Zero)
        {
            using var launch = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)!
            });
            var startup = Stopwatch.StartNew();
            while (window == IntPtr.Zero && startup.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(200);
                window = FindWindow(processes());
            }
        }
        if (window == IntPtr.Zero)
            throw new InvalidOperationException("Не найдено окно INCY. Откройте INCY и выберите сервер.");
        await ActivateAsync(window);
        uint targetPid;
        GetWindowThreadProcessId(window, out targetPid);
        uint foregroundPid;
        GetWindowThreadProcessId(GetForegroundWindow(), out foregroundPid);
        if (targetPid != foregroundPid || GetForegroundWindow() != window)
            throw new InvalidOperationException("Не удалось активировать окно INCY. Закройте его диалоговые окна и повторите.");
        var released = Stopwatch.StartNew();
        while (ModifiersHeld() && released.ElapsedMilliseconds < 2000) await Task.Delay(50);
        if (ModifiersHeld()) throw new InvalidOperationException("Отпустите Ctrl, Alt, Shift и Win и повторите подключение.");
        // A second check prevents toggling OFF a connection that started while opening the window.
        before = readStatus();
        if (before.IsRunning) return;
        if (before.State == VpnState.Unknown) throw new InvalidOperationException(before.Detail);
        if (GetForegroundWindow() != window)
            throw new InvalidOperationException("Фокус переключился с INCY. Повторите подключение.");
        // Prefer INCY's accessible connect action: unlike a shortcut, it has an explicit target.
        var evidence = IncyStartEvidence.Capture();
        if (await IncyAccessibility.TryConnectAsync(window))
        {
            report?.Invoke("Команда подключения передана кнопке INCY. Ожидаем ядро…");
        }
        else await ShortcutAsync(window, () => readStatus().State == VpnState.Disconnected && !evidence.HasChanged());
        // A cold Compose window may swallow its first shortcut. Retry only if no core,
        // no connecting state, no new session/log/config activity was observed.
        var acknowledgement = Stopwatch.StartNew();
        while (acknowledgement.Elapsed < TimeSpan.FromSeconds(8))
        {
            await Task.Delay(400);
            var state = readStatus();
            if (state.IsRunning || state.State == VpnState.Unknown || evidence.HasChanged()) return;
            if (await IncyAccessibility.IsConnectingAsync(window)) return;
        }
        // 3.8.10 logs [VpnVM]/[SESSION] before starting its core. Its toggle ignores
        // Connecting. A silent request can be retried once if these logs are readable,
        // the core remains stopped and no new session/config activity has appeared.
        if (!evidence.CanObserveRequests && !await IncyAccessibility.IsDefinitelyDisconnectedAsync(window))
        {
            report?.Invoke("INCY пока не подтвердил команду. Повтор Ctrl+K пропущен: состояние подключения не подтверждено.");
            return;
        }
        await ActivateAsync(window);
        before = readStatus();
        if (before.State != VpnState.Disconnected || evidence.HasChanged()) return;
        report?.Invoke("INCY остался отключённым без признаков запуска. Повторяем команду один раз…");
        if (!await IncyAccessibility.TryConnectAsync(window))
            await ShortcutAsync(window, () => readStatus().State == VpnState.Disconnected && !evidence.HasChanged());
    }

    internal static async Task ActivateAsync(IntPtr window)
    {
        ShowWindowAsync(window, 9);
        var ready = Stopwatch.StartNew();
        int stable = 0;
        while (ready.Elapsed < TimeSpan.FromSeconds(8))
        {
            FocusWindow(window);
            await Task.Delay(150);
            bool responsive = SendMessageTimeout(window, 0, IntPtr.Zero, IntPtr.Zero, 2, 300, out _) != IntPtr.Zero;
            stable = responsive && GetForegroundWindow() == window ? stable + 1 : 0;
            if (stable >= 7)
            {
                FocusContent(window);
                await Task.Delay(200);
                return;
            }
        }
        throw new InvalidOperationException("Окно INCY не готово к вводу. Закройте его диалоговые окна и повторите.");
    }

    private static async Task ShortcutAsync(IntPtr window, Func<bool> canSend)
    {
        if (GetForegroundWindow() != window || ModifiersHeld())
            throw new InvalidOperationException("INCY потерял фокус или нажата клавиша-модификатор. Повторите подключение.");
        try
        {
            if (!canSend()) return;
            SendKey(Key(0x11, false));
            await Task.Delay(90);
            if (GetForegroundWindow() != window) throw new InvalidOperationException("Фокус переключился с INCY.");
            if (!canSend()) return;
            SendKey(Key(0x4B, false));
            await Task.Delay(90);
        }
        finally
        {
            // Always attempt both releases, even if key injection fails partway through.
            SendInput(2, [Key(0x4B, true), Key(0x11, true)], Marshal.SizeOf<Input>());
        }
    }

    private static void SendKey(Input input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows не передала команду в INCY.");
    }

    internal static IntPtr FindWindow(int[] processes)
    {
        var pids = processes.ToHashSet();
        IntPtr found = IntPtr.Zero;
        int largest = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint pid);
            if (!pids.Contains((int)pid)) return true;
            var title = new StringBuilder(512);
            GetWindowText(window, title, title.Capacity);
            if (!title.ToString().Contains("INCY", StringComparison.OrdinalIgnoreCase)) return true;
            GetClientRect(window, out var bounds);
            int area = (bounds.Right - bounds.Left) * (bounds.Bottom - bounds.Top);
            if (area > largest) { largest = area; found = window; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static void FocusWindow(IntPtr window)
    {
        IntPtr foreground = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint currentThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        try { BringWindowToTop(window); SetForegroundWindow(window); }
        finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
    }
    private static void FocusContent(IntPtr window)
    {
        // Foreground frame != focused Compose canvas. Give the real Java content
        // control keyboard focus before injecting the same shortcut the user uses.
        IntPtr canvas = IntPtr.Zero;
        EnumChildWindows(window, (child, _) =>
        {
            var name = new StringBuilder(128); GetClassName(child, name, name.Capacity);
            if (IsWindowVisible(child) && name.ToString() == "SunAwtCanvas") canvas = child;
            return true;
        }, IntPtr.Zero);
        uint targetThread = GetWindowThreadProcessId(window, out _), current = GetCurrentThreadId();
        bool attached = targetThread != current && AttachThreadInput(current, targetThread, true);
        try
        {
            SetFocus(canvas == IntPtr.Zero ? window : canvas);
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            if (!GetGUIThreadInfo(targetThread, ref info) || info.Focus == IntPtr.Zero
                || info.Focus != window && !IsChild(window, info.Focus))
                throw new InvalidOperationException("Окно INCY активно, но его поле ввода не получило фокус. Откройте главную вкладку INCY и повторите.");
        }
        finally { if (attached) AttachThreadInput(current, targetThread, false); }
    }
    private static bool ModifiersHeld() => new[] { 0x11, 0x12, 0x10, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
    // Physical Ctrl/K scan codes are stable across English and Russian layouts.
    private static Input Key(ushort key, bool up) => new() { Type = 1, Data = new() { Keyboard = new() { Scan = key == 0x11 ? (ushort)0x1D : (ushort)0x25, Flags = 8u | (up ? 2u : 0u) } } };
    private delegate bool EnumCallback(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    { public int Size; public uint Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
}

internal sealed class IncyStartEvidence
{
    private readonly Dictionary<string, (long Length, DateTime Write)> files;
    private IncyStartEvidence(Dictionary<string, (long, DateTime)> files) => this.files = files;
    public static IncyStartEvidence Capture() => new(Snapshot());
    public bool CanObserveRequests => files.Keys.Any(p => p.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
    public bool HasChanged()
    {
        var now = Snapshot();
        foreach (var pair in now)
        {
            bool existed = files.TryGetValue(pair.Key, out var old);
            if (existed && old == pair.Value) continue;
            if (!pair.Key.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                using var stream = new FileStream(pair.Key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (existed && stream.Length < old.Length) return true; // Rotation: uncertain history.
                stream.Position = existed ? old.Length : Math.Max(0, stream.Length - 128000);
                byte[] buffer = new byte[(int)Math.Min(128000, stream.Length - stream.Position)];
                int length = stream.Read(buffer, 0, buffer.Length);
                string appended = Encoding.UTF8.GetString(buffer, 0, length);
                if (appended.Contains("[VpnVM]", StringComparison.Ordinal) || appended.Contains("[SESSION]", StringComparison.Ordinal)
                    || appended.Contains("Connecting", StringComparison.OrdinalIgnoreCase)
                    || appended.Contains("StartCore", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }
        return files.Keys.Any(p => !now.ContainsKey(p));
    }
    private static Dictionary<string, (long, DateTime)> Snapshot()
    {
        var result = new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "incy", "cache");
        try
        {
            if (Directory.Exists(cache))
                foreach (string file in Directory.EnumerateFiles(cache).Where(p => p.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(p) is "xray_config.json" or "xray-config.json"))
                {
                    var info = new FileInfo(file);
                    result[file] = (info.Length, info.LastWriteTimeUtc);
                }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }
}
