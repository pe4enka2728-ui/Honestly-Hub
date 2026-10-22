using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace VpnZapretSwitch;

internal static class IncyScreenControl
{
    private static Process? bridge;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private sealed record Line(string Text, Rectangle Bounds);

    public static async Task ChooseAsync(IntPtr window, IncyServer server, IReadOnlyList<IncyServer> servers)
    {
        await Gate.WaitAsync();
        try
        {
            // Restore/show without stealing the user's active window or cursor.
            ShowWindowAsync(window, 4); // SW_SHOWNOACTIVATE
            await Task.Delay(400);
            bool openedServers = false, expanded = false, movedToTop = false;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                using var snapshot = Capture(window);
                var lines = await RecognizeAsync(snapshot.Bitmap);
                var known = lines.Where(l => servers.Any(s => Normalize(s.Name) == Normalize(l.Text))).ToList();
                var matches = known.Where(l => Normalize(l.Text) == Normalize(server.Name)).ToList();
                bool listVisible = known.Count >= 2 || lines.Any(l => Normalize(l.Text).Contains("текущаяподписка", StringComparison.Ordinal)
                    || Normalize(l.Text).Contains("currentsubscription", StringComparison.Ordinal));
                if (matches.Count == 1 && listVisible)
                {
                    Click(window, snapshot, matches[0].Bounds);
                    return;
                }
                if (matches.Count > 1) throw new InvalidOperationException("OCR нашёл несколько строк с таким именем. Выберите сервер в INCY вручную.");
                if (!openedServers && known.Count == 0)
                {
                    var navigation = lines.Where(l => (Normalize(l.Text) is "серверы" or "servers")
                        && l.Bounds.Left < snapshot.Bitmap.Width / 3).ToArray();
                    if (navigation.Length == 1)
                    {
                        Click(window, snapshot, navigation[0].Bounds);
                        openedServers = true; await Task.Delay(500); continue;
                    }
                }
                if (!expanded && server.Subscription.Length > 0)
                {
                    string name = Normalize(server.Subscription);
                    var subscription = lines.Where(l => Normalize(l.Text) == name).ToArray();
                    // Expand a collapsed subscription only when none of its rows are visible.
                    bool rowsVisible = known.Any(l => servers.Any(s => s.Subscription == server.Subscription && Normalize(s.Name) == Normalize(l.Text)));
                    if (!rowsVisible && subscription.Length == 1)
                    {
                        Click(window, snapshot, subscription[0].Bounds);
                        expanded = true; await Task.Delay(400); continue;
                    }
                }
                if (known.Count == 0)
                    throw new InvalidOperationException("Не удалось распознать список серверов INCY. Откройте в INCY нужную подписку и повторите выбор. Для OCR нужен установленный язык Windows.");
                Rectangle anchor = known.OrderBy(l => l.Bounds.Top).Last().Bounds;
                // Scroll within a visually identified server list, never at a fixed screen position.
                Wheel(window, snapshot.Window, anchor, movedToTop ? -360 : 3600);
                movedToTop = true; await Task.Delay(350);
            }
            throw new InvalidOperationException("Сервер не найден в видимой части списка INCY. Откройте его строку в INCY и повторите.");
        }
        finally { Gate.Release(); }
    }

    private static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private sealed class Snapshot(Bitmap bitmap, Rectangle window) : IDisposable
    {
        public Bitmap Bitmap { get; } = bitmap;
        public Rectangle Window { get; } = window;
        public void Dispose() => Bitmap.Dispose();
    }
    private static Snapshot Capture(IntPtr window)
    {
        if (!IsWindowEnabled(window)) throw new InvalidOperationException("Закройте диалоговое окно INCY перед выбором сервера.");
        if (!GetClientRect(window, out var rect))
            throw new InvalidOperationException("Не удалось прочитать размер окна INCY.");
        var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        if (bounds.Width < 200 || bounds.Height < 200 || bounds.Width > 6000 || bounds.Height > 6000)
            throw new InvalidOperationException("Некорректный размер окна INCY.");
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        IntPtr dc = graphics.GetHdc();
        bool rendered;
        try { rendered = PrintWindow(window, dc, 3); } // PW_CLIENTONLY | PW_RENDERFULLCONTENT
        finally { graphics.ReleaseHdc(dc); }
        if (!rendered) { bitmap.Dispose(); throw new IOException("INCY не предоставил изображение для фонового выбора. Выберите сервер в INCY вручную."); }
        return new(bitmap, bounds);
    }
    private static void Click(IntPtr window, Snapshot snapshot, Rectangle row)
    {
        using var current = Capture(window);
        if (current.Window != snapshot.Window) throw new InvalidOperationException("Окно INCY переместилось. Повторите выбор.");
        Rectangle sample = Rectangle.Intersect(row, new Rectangle(Point.Empty, snapshot.Bitmap.Size));
        if (sample.Width < 5 || sample.Height < 5) throw new InvalidOperationException("Не удалось определить строку сервера.");
        for (int y = sample.Top; y < sample.Bottom; y += 3)
            for (int x = sample.Left; x < sample.Right; x += 3)
                if (snapshot.Bitmap.GetPixel(x, y) != current.Bitmap.GetPixel(x, y))
                    throw new InvalidOperationException("Список INCY изменился после распознавания. Повторите выбор.");
        var point = new NativePoint { X = sample.Left + sample.Width / 2, Y = sample.Top + sample.Height / 2 };
        IntPtr target = InputTarget(window, ref point);
        IntPtr packed = Pack(point);
        SendTo(target, 0x200, IntPtr.Zero, packed); // WM_MOUSEMOVE
        SendTo(target, 0x201, (IntPtr)1, packed); // WM_LBUTTONDOWN
        SendTo(target, 0x202, IntPtr.Zero, packed); // WM_LBUTTONUP
    }
    private static void Wheel(IntPtr window, Rectangle bounds, Rectangle row, int delta)
    {
        var point = new NativePoint { X = row.Left + row.Width / 2, Y = row.Top + row.Height / 2 };
        IntPtr target = InputTarget(window, ref point);
        ClientToScreen(target, ref point);
        SendTo(target, 0x20A, (IntPtr)(delta << 16), Pack(point));
    }
    private static IntPtr InputTarget(IntPtr root, ref NativePoint point)
    {
        IntPtr target = root;
        GetWindowThreadProcessId(root, out uint owner);
        for (int depth = 0; depth < 12; depth++)
        {
            IntPtr child = ChildWindowFromPointEx(target, point, 7);
            if (child == IntPtr.Zero || child == target) return target;
            GetWindowThreadProcessId(child, out uint pid);
            if (pid != owner) throw new IOException("Внутреннее окно INCY изменилось. Повторите выбор.");
            MapWindowPoints(target, child, ref point, 1); target = child;
        }
        return target;
    }
    private static IntPtr Pack(NativePoint point) => (IntPtr)((point.X & 65535) | (point.Y << 16));
    private static void SendTo(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (SendMessageTimeout(window, message, wParam, lParam, 2, 1000, out _) == IntPtr.Zero)
            throw new IOException("INCY не ответил на фоновую команду. Выберите сервер в INCY вручную.");
    }
    private static async Task<List<Line>> RecognizeAsync(Bitmap bitmap)
    {
        ClientStore.EnsurePrivateDirectory();
        string directory = Path.Combine(ClientStore.Root, "ocr");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
        try
        {
            double ratio = Math.Min(1, 2400d / Math.Max(bitmap.Width, bitmap.Height));
            using var resized = ratio < 1 ? new Bitmap(bitmap, new Size((int)(bitmap.Width * ratio), (int)(bitmap.Height * ratio))) : null;
            (resized ?? bitmap).Save(file, ImageFormat.Png);
            await EnsureBridgeAsync();
            await bridge!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { file }));
            string? json = await bridge.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            using var document = JsonDocument.Parse(json ?? throw new IOException("Windows OCR завершился без ответа."));
            if (document.RootElement.TryGetProperty("error", out var error)) throw new IOException("Windows OCR: " + error.GetString());
            return document.RootElement.GetProperty("lines").EnumerateArray().Select(l => new Line(l.GetProperty("text").GetString() ?? "",
                new Rectangle((int)(l.GetProperty("x").GetDouble() / ratio), (int)(l.GetProperty("y").GetDouble() / ratio),
                    (int)Math.Ceiling(l.GetProperty("width").GetDouble() / ratio), (int)Math.Ceiling(l.GetProperty("height").GetDouble() / ratio)))).ToList();
        }
        catch { Shutdown(); throw; }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
    private static async Task EnsureBridgeAsync()
    {
        if (bridge is not null && !bridge.HasExited) return;
        Shutdown();
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "OcrBridge.ps1") })
            start.ArgumentList.Add(argument);
        bridge = Process.Start(start) ?? throw new IOException("Не удалось открыть встроенное распознавание Windows.");
        bridge.ErrorDataReceived += (_, _) => { }; bridge.BeginErrorReadLine();
        string? response = await bridge.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12));
        using var document = JsonDocument.Parse(response ?? "{}");
        if (!document.RootElement.TryGetProperty("ready", out var ready) || !ready.GetBoolean())
            throw new IOException("Windows OCR недоступен. Проверьте установку языка распознавания Windows. " +
                (document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : ""));
    }
    public static void Shutdown()
    {
        if (bridge is null) return;
        try { if (!bridge.HasExited) bridge.Kill(entireProcessTree: true); }
        finally { bridge.Dispose(); bridge = null; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref NativePoint points, uint count);
    [DllImport("user32.dll")] private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
