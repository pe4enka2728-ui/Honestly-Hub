using System.Drawing;
using System.Reflection;
using System.Text.Json;
using VpnZapretSwitch;

internal static class Inspect
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (!AppContext.BaseDirectory.Contains("UiTests", StringComparison.OrdinalIgnoreCase))
            throw new Exception("UI checks must run from their own build folder.");
        var settings = new Settings { VpnMode = "incy" };
        settings.Save();
        var platform = new WindowsPlatform(settings);
        platform.Validate();
        Console.WriteLine("PASS: installed INCY, xray.exe, Zapret folder and default strategy validated.");
        Console.WriteLine($"Current core running: {platform.IsVpnRunning()}; Zapret running: {platform.IsZapretRunning()}");
        Console.WriteLine(JsonSerializer.Serialize(platform.ReadRuntimeStatus()));
        if (args[0] == "--probe") return;
        ApplicationConfiguration.Initialize();
        using var form = new MainForm();
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        form.ShowInTaskbar = false;
        form.Show();
        PumpUntil(() => (bool)typeof(MainForm).GetField("startupCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!);
        Capture(form, args[0]);
        var navigation = Descendants(form).Where(control => control is IButtonControl).ToArray();
        navigation.Single(b => b.Text == "Настройки").PerformClick();
        Application.DoEvents();
        Capture(form, Path.Combine(Path.GetDirectoryName(args[0])!, "astra-settings.png"));
        var combo = Descendants(form).OfType<ComboBox>().Single(c => c.AccessibleName == "Стратегия Zapret");
        string original = combo.Text;
        if (original != "general (ALT).bat") throw new Exception("Default strategy was changed.");
        int alternate = combo.FindStringExact("general (ALT2).bat");
        if (alternate < 0) throw new Exception("Alternative strategy is unavailable.");
        if (!AppContext.BaseDirectory.Contains("UiTests", StringComparison.OrdinalIgnoreCase))
            throw new Exception("UI tests must save only to the work folder.");
        combo.SelectedIndex = alternate;
        navigation.Single(b => b.Text == "Сохранить настройки").PerformClick();
        if (Settings.Load().StrategyFile != "general (ALT2).bat") throw new Exception("Strategy was not saved.");
        combo.SelectedIndex = combo.FindStringExact(original);
        navigation.Single(b => b.Text == "Сохранить настройки").PerformClick();
        navigation.Single(b => b.Text == "Журнал").PerformClick();
        Application.DoEvents();
        Capture(form, Path.Combine(Path.GetDirectoryName(args[0])!, "astra-journal.png"));
        var journal = Descendants(form).OfType<TextBox>().Single(t => t.Multiline);
        if (!journal.Text.Contains("Настройки сохранены")) throw new Exception("Journal did not update.");
        if (!journal.Text.Contains("Начальная проверка")) throw new Exception("Startup status check is missing.");
        navigation.Single(b => b.Text == "Очистить журнал").PerformClick();
        if (journal.TextLength != 0) throw new Exception("Clear journal did not work.");
        navigation.Single(b => b.Text == "Обзор").PerformClick();
        form.Size = form.MinimumSize;
        Application.DoEvents();
        Capture(form, Path.Combine(Path.GetDirectoryName(args[0])!, "astra-minimum.png"));
        navigation.Single(b => b.Text == "Настройки").PerformClick();
        Application.DoEvents();
        Capture(form, Path.Combine(Path.GetDirectoryName(args[0])!, "astra-settings-minimum.png"));
        TestPaintTransform(form);
        StressLayout(form, navigation, args[0]);
        TestButtons(form, navigation, args[0]);
        form.Close();
        Console.WriteLine("PASS: all three pages, navigation, strategy selection/save, journal/clear and minimum window size.");
        Console.WriteLine("No VPN or Zapret processes were started or stopped.");
    }
    private static void TestButtons(MainForm form, Control[] buttons, string output)
    {
        const BindingFlags methods = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (Control button in buttons)
        {
            if (button is ButtonBase) throw new Exception("Native button rendering is still present.");
            if (!button.Visible) continue;
            Color backdrop = (Color)button.GetType().Assembly.GetType("VpnZapretSwitch.Astra")!
                .GetMethod("Backdrop", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [button.Parent])!;
            button.Focus();
            button.GetType().GetMethod("OnMouseEnter", methods)!.Invoke(button, [EventArgs.Empty]);
            button.Invalidate(); button.Update();
            using var bitmap = new Bitmap(button.Width, button.Height);
            button.DrawToBitmap(bitmap, button.ClientRectangle);
            foreach (Point corner in new[] { new Point(0, 0), new Point(button.Width - 1, 0), new Point(0, button.Height - 1), new Point(button.Width - 1, button.Height - 1) })
                if (bitmap.GetPixel(corner.X, corner.Y).ToArgb() != backdrop.ToArgb())
                    throw new Exception("Incorrect rounded-corner background: " + button.Text);
            button.GetType().GetMethod("OnMouseLeave", methods)!.Invoke(button, [EventArgs.Empty]);
        }
        var target = buttons.Single(b => b.Text == "Настройки");
        using (var bitmap = new Bitmap(320, 180))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Magenta);
            graphics.TranslateTransform(20, 20);
            var localClip = new Rectangle(30, 15, 45, 18);
            graphics.SetClip(localClip);
            var paint = target.GetType().GetMethod("OnPaint", methods)!;
            paint.Invoke(target, [new PaintEventArgs(graphics, localClip)]);
            var deviceClip = new Rectangle(50, 35, 45, 18);
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (!deviceClip.Contains(x, y) && bitmap.GetPixel(x, y).ToArgb() != Color.Magenta.ToArgb())
                    throw new Exception("Text or button painting escaped its translated clipping region.");
        }
        buttons.Single(b => b.Text == "Обзор").PerformClick();
        target.Focus();
        Application.DoEvents();
        Capture(form, Path.Combine(Path.GetDirectoryName(output)!, "honestly-focus-fixed.png"));
        Console.WriteLine("PASS: controls have no native ButtonBase painting, all rounded corners match their parent, and text respects translated partial clipping.");
    }
    private static void PumpUntil(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Application.DoEvents();
            Thread.Sleep(10);
            if (watch.ElapsedMilliseconds > 15000) throw new Exception("UI operation did not complete.");
        }
        Application.DoEvents();
    }
    private static void TestPaintTransform(Form form)
    {
        var navigationButton = Descendants(form).Where(control => control is IButtonControl).Single(b => b.Text == "Настройки");
        var draw = navigationButton.GetType().GetMethod("DrawNavigationIcon", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var bitmap = new Bitmap(300, 200);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.TranslateTransform(13, 29);
        graphics.ScaleTransform(1.5f, 1.5f);
        float[] before = graphics.Transform.Elements;
        draw.Invoke(navigationButton, [graphics, Color.White, 1.5f]);
        if (!before.SequenceEqual(graphics.Transform.Elements)) throw new Exception("Navigation painting lost the graphics transform.");
        Console.WriteLine("PASS: navigation painting preserves the caller's transform.");
    }
    private static void StressLayout(MainForm form, Control[] buttons, string output)
    {
        var append = typeof(MainForm).GetMethod("AppendLog", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var serversField = typeof(MainForm).GetField("servers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var serverPanel = (Control)serversField.GetValue(form)!;
        string[] messages = ["Коротко.", new string('Ш', 450), "Первая строка\nВторая строка\n" + new string('Д', 200)];
        string[] pageNames = ["Обзор", "Настройки", "Журнал"];
        for (int i = 0; i < 36; i++)
        {
            form.Size = i % 2 == 0 ? form.MinimumSize : new Size(1200, 850);
            buttons.Single(b => b.Text == pageNames[i % 3]).PerformClick();
            append.Invoke(form, [messages[i % 3]]);
            form.Invalidate(true);
            form.Update();
            Application.DoEvents();
            foreach (var button in buttons.Where(b => pageNames.Contains(b.Text)))
                if (button.Width < 100 || button.Height < 30) throw new Exception("Navigation collapsed during layout.");
            if (serverPanel.Width < 300 || serverPanel.Height < 150) throw new Exception("Server panel collapsed during layout.");
        }
        buttons.Single(b => b.Text == "Обзор").PerformClick();
        form.Size = new Size(1080, 800);
        append.Invoke(form, [new string('Д', 350)]);
        Application.DoEvents();
        Capture(form, Path.Combine(Path.GetDirectoryName(output)!, "astra-long-activity.png"));
        append.Invoke(form, ["Готово. Проверка завершена."]);
        Application.DoEvents();
        Capture(form, Path.Combine(Path.GetDirectoryName(output)!, "astra-redraw-fixed.png"));
        Console.WriteLine("PASS: 36 resize/page-change/redraw cycles, long and multiline messages, navigation and activity geometry.");
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            yield return control;
            foreach (var child in Descendants(control)) yield return child;
        }
    }
    private static void Capture(Form form, string path)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path);
    }
}

internal static class TestClicks
{
    public static void PerformClick(this Control control) => ((IButtonControl)control).PerformClick();
}

