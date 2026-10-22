using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VpnZapretSwitch;

internal static class AppWindows
{
    internal static AppEntry[] Read(IReadOnlyList<AppShortcut> pinned)
    {
        var windows = new List<AppWindow>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processPaths = new Dictionary<int, string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string path = ReadProcessPath(process.Id);
                    processPaths[process.Id] = path;
                    if (!string.IsNullOrEmpty(path)) paths.Add(path);
                }
                catch { }
            }
        }
        EnumWindows((handle, _) =>
        {
            try
            {
                if (!IsWindowVisible(handle) || GetWindowTextLength(handle) == 0) return true;
                long style = GetWindowLongPtr(handle, -20).ToInt64();
                if ((style & 0x80) != 0 || GetWindow(handle, 4) != 0 && (style & 0x40000) == 0) return true;
                if (DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                GetWindowThreadProcessId(handle, out uint pid);
                if (pid == Environment.ProcessId || pid == 0) return true;
                var caption = new StringBuilder(512); GetWindowText(handle, caption, caption.Capacity);
                if (caption.Length == 0) return true;
                processPaths.TryGetValue((int)pid, out string? path);
                windows.Add(new((int)pid, handle, path ?? "", caption.ToString()));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        var entries = AppCatalog.Merge(pinned, windows, paths);
        return entries.Select(entry => entry.Pinned || string.IsNullOrEmpty(entry.Path) ? entry
            : entry with { Name = DisplayName(entry.Path) }).ToArray();
    }
    internal static string DisplayName(string path)
    {
        try
        {
            string? name = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }
    private static string ReadProcessPath(int id)
    {
        nint process = OpenProcess(0x1000, false, id);
        if (process == 0) return "";
        try
        {
            var value = new StringBuilder(32768); int size = value.Capacity;
            return QueryFullProcessImageName(process, 0, value, ref size) ? value.ToString() : "";
        }
        finally { CloseHandle(process); }
    }
    internal static Bitmap LoadIcon(AppEntry entry)
    {
        try
        {
            if (!string.IsNullOrEmpty(entry.Path) && File.Exists(entry.Path))
            {
                using var icon = Icon.ExtractAssociatedIcon(entry.Path);
                if (icon is not null) return icon.ToBitmap();
            }
            if (entry.Window != 0 && SendMessageTimeout(entry.Window, 0x7F, (nint)1, 0, 2, 80, out nint handle) != 0 && handle != 0)
                return Icon.FromHandle(handle).ToBitmap();
        }
        catch { }
        return SystemIcons.Application.ToBitmap();
    }
    internal static bool Activate(AppEntry app)
    {
        if (app.Window == 0 || !IsWindow(app.Window)) return false;
        GetWindowThreadProcessId(app.Window, out uint pid);
        if (pid != app.ProcessId) return false;
        if (IsIconic(app.Window)) ShowWindowAsync(app.Window, 9);
        if (!SetForegroundWindow(app.Window))
        {
            var flash = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = app.Window, Flags = 3, Count = 3 };
            FlashWindowEx(ref flash);
        }
        return true;
    }
    internal static void Launch(string path)
    {
        path = AppShortcutStore.Normalize(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Файл приложения не найден.", path);
        // Ask the existing desktop's Shell.Application to launch. Applications
        // don't inherit Honestly TUN's administrator token unnecessarily.
        object? windows = null, desktop = null, document = null, shell = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: true)!;
            windows = Activator.CreateInstance(type)!;
            object location = 0, root = Type.Missing; int window;
            desktop = ((dynamic)windows).FindWindowSW(ref location, ref root, 8, out window, 1);
            if (desktop is null) throw new InvalidOperationException("Не удалось обратиться к оболочке Windows.");
            document = ((dynamic)desktop).Document;
            shell = ((dynamic)document).Application;
            ((dynamic)shell).ShellExecute(path, "", Path.GetDirectoryName(path)!, "open", 1);
        }
        finally
        {
            foreach (var item in new[] { shell, document, desktop, windows })
                if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FlashInfo { internal uint Size; internal nint Window; internal uint Flags, Count, Timeout; }
    private delegate bool WindowCallback(nint window, nint state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder value, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
