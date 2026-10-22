using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace VpnZapretSwitch;

internal static class IncyAccessibility
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string[] ConnectNames = ["Подключить", "Подключиться", "Connect"];
    private static readonly string[] PendingNames = ["Подключение", "Подключение…", "Подключается…", "Connecting", "Connecting…", "Connecting..."];
    private sealed record Node(AutomationElement Element, string Name, double X, double Y);

    // A hung external accessibility provider must not hang the WinForms message loop or
    // create an unbounded stream of blocked worker threads on every refresh tick.
    private static async Task<T> ReadAsync<T>(Func<T> action, T unavailable)
    {
        if (!await Gate.WaitAsync(0)) return unavailable;
        var worker = Task.Run(() =>
        {
            try { return action(); }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
                or System.Runtime.InteropServices.COMException) { return unavailable; }
            finally { Gate.Release(); }
        });
        try { return await worker.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { return unavailable; }
    }

    private static List<Node> Nodes(IntPtr window)
    {
        if (window == IntPtr.Zero) return [];
        var root = AutomationElement.FromHandle(window);
        var result = new List<Node>();
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);
        var clock = Stopwatch.StartNew();
        int visited = 0;
        while (queue.Count > 0 && visited++ < 1800 && clock.ElapsedMilliseconds < 1800)
        {
            var element = queue.Dequeue();
            var info = element.Current;
            if (!info.IsPassword && !string.IsNullOrWhiteSpace(info.Name))
                result.Add(new(element, info.Name.Trim(), info.BoundingRectangle.X, info.BoundingRectangle.Y));
            var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
            while (child is not null && queue.Count < 1800)
            {
                queue.Enqueue(child);
                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
        }
        return result;
    }

    public static Task<bool> IsConnectingAsync(IntPtr window) => ReadAsync(() =>
        Nodes(window).Any(n => PendingNames.Contains(n.Name, StringComparer.OrdinalIgnoreCase)), false);
    public static Task<bool> IsDefinitelyDisconnectedAsync(IntPtr window) => ReadAsync(() =>
    {
        var nodes = Nodes(window);
        return nodes.Any(n => ConnectNames.Contains(n.Name, StringComparer.OrdinalIgnoreCase))
            && !nodes.Any(n => PendingNames.Contains(n.Name, StringComparer.OrdinalIgnoreCase)
                || n.Name.Equals("Disconnect", StringComparison.OrdinalIgnoreCase) || n.Name == "Отключить");
    }, false);

    public static async Task<bool> TryConnectAsync(IntPtr window)
    {
        int issued = 0;
        var deadline = Stopwatch.StartNew();
        bool result = await ReadAsync(() =>
        {
        var nodes = Nodes(window);
        if (nodes.Any(n => PendingNames.Contains(n.Name, StringComparer.OrdinalIgnoreCase))) return true;
        var buttons = nodes.Where(n => ConnectNames.Contains(n.Name, StringComparer.OrdinalIgnoreCase)
            && n.Element.Current.IsEnabled && n.Element.Current.ControlType == ControlType.Button).ToArray();
        if (buttons.Length != 1) return false;
        if (!buttons[0].Element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)) return false;
        if (deadline.ElapsedMilliseconds > 2500) return false;
        Interlocked.Exchange(ref issued, 1);
        ((InvokePattern)pattern).Invoke();
        return true;
        }, false);
        // A timed-out provider may still be completing Invoke. Do not follow it with Ctrl+K.
        return result || Volatile.Read(ref issued) != 0;
    }

    public static async Task<IncyDisplayData> ReadDisplayAsync(Settings settings, VpnState state)
    {
        IReadOnlyList<IncyServer> servers;
        string note = "";
        try { servers = await Task.Run(IncyDatabase.ReadServers); }
        catch (Exception ex) { servers = []; note = ex.Message; }
        string selected = servers.FirstOrDefault(s => s.Selected)?.Name ?? "—";
        if (state != VpnState.Active)
        {
            IncyCoreMetrics.Reset();
            return new(servers, "—", "—", "—", selected, note);
        }
        try
        {
            var metrics = await IncyCoreMetrics.ReadAsync(settings);
            return new(servers, metrics.Upload, metrics.Elapsed, metrics.Download, selected,
                string.Join(" ", new[] { note, metrics.Note }.Where(n => n.Length > 0)));
        }
        catch (Exception ex) { note = "Статистика ядра INCY недоступна: " + ex.Message; }
        IntPtr window = IncyDesktopControl.FindWindow(WindowsPlatform.ExactProcesses(settings.IncyExe));
        var stats = await ReadAsync(() => ParseStats(Nodes(window)), ("—", "—", "—"));
        if (stats.Item1 == "—" && stats.Item3 == "—")
            note += " Windows UI Automation также не предоставил статистику.";
        return new(servers, stats.Item1, stats.Item2, stats.Item3, selected, note);
    }

    private static (string, string, string) ParseStats(List<Node> nodes)
    {
        // Read the actual displayed rates, not all-system adapter counters. In INCY's
        // home panel upload is on the left and download on the right of the timer.
        var rate = new Regex(@"(?<![\d.])\d+(?:[.,]\d+)?\s*(?:[KMGT]?i?B/s|[КМГТ]?[бБ]/с)", RegexOptions.IgnoreCase);
        var rates = nodes.SelectMany(n => rate.Matches(n.Name).Select(m => (Text: m.Value, n.X, n.Y)))
            .Distinct().OrderBy(n => n.Y).ThenBy(n => n.X).ToList();
        var timer = nodes.FirstOrDefault(n => Regex.IsMatch(n.Name, @"^\d{1,3}:\d{2}(?::\d{2})?$"));
        if (timer is null) return ("—", "—", "—");
        var sameRow = rates.Where(n => Math.Abs(n.Y - timer.Y) < 45).OrderBy(n => n.X).ToList();
        if (sameRow.Count != 2) return ("—", timer.Name, "—");
        return (sameRow[0].Text, timer.Name, sameRow[1].Text);
    }

    public static async Task SelectServerAsync(Settings settings, IncyServer server)
    {
        var before = await Task.Run(IncyDatabase.ReadServers);
        if (before.FirstOrDefault(s => s.Id == server.Id)?.Selected == true) return;
        if (before.Count(s => s.Name == server.Name) != 1)
            throw new InvalidOperationException("В INCY есть одинаковые названия серверов. Выберите нужный сервер в самом INCY: автоматический выбор неоднозначен.");
        IntPtr window = IncyDesktopControl.FindWindow(WindowsPlatform.ExactProcesses(settings.IncyExe));
        if (window == IntPtr.Zero) throw new InvalidOperationException("Откройте INCY, затем повторите выбор сервера.");
        // Invoke/SelectionItem do not require foreground activation. For Compose
        // without UIA, use the window's own rendered image and targeted messages.
        int issued = 0;
        var deadline = Stopwatch.StartNew();
        bool invoked = await ReadAsync(() =>
        {
            var nodes = Nodes(window);
            var matches = nodes.Where(n => n.Name == server.Name).ToList();
            if (matches.Count != 1) return false;
            var target = matches[0].Element;
            for (int depth = 0; depth < 4 && target is not null; depth++)
            {
                if (deadline.ElapsedMilliseconds > 2500) return false;
                if (target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
                { Interlocked.Exchange(ref issued, 1); ((SelectionItemPattern)selection).Select(); return true; }
                if (target.TryGetCurrentPattern(InvokePattern.Pattern, out var invocation))
                { Interlocked.Exchange(ref issued, 1); ((InvokePattern)invocation).Invoke(); return true; }
                target = TreeWalker.ControlViewWalker.GetParent(target);
                if (target is null || target.Current.ControlType == ControlType.Window) break;
            }
            return false;
        }, false);
        if (!invoked && Volatile.Read(ref issued) == 0) await IncyScreenControl.ChooseAsync(window, server, before);
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(6))
        {
            var actual = await Task.Run(IncyDatabase.ReadServers);
            if (actual.FirstOrDefault(s => s.Id == server.Id)?.Selected == true) return;
            await Task.Delay(250);
        }
        throw new InvalidOperationException("Команда передана INCY, но он не подтвердил выбранный сервер. Проверьте выбор в INCY.");
    }
}
