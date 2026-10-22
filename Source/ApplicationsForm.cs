using System.Drawing.Drawing2D;
using System.IO;

namespace VpnZapretSwitch;

internal sealed class ApplicationsForm : Form
{
    private readonly AppShortcutStore store;
    private List<AppShortcut> pinned = [];
    private readonly AstraCanvas grid = new() { Dock = DockStyle.Top, BackColor = Astra.Background };
    private readonly Label status = Astra.Label("Читаем открытые приложения…", 9, Astra.Muted);
    private readonly Label empty = Astra.Label("Здесь появятся открытые окна и добавленные приложения.", 11, Astra.Muted);
    private readonly Dictionary<string, AppTile> tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1500 };
    private readonly ToolTip hints = new();
    private bool reading, arranging, released;
    internal ApplicationsForm(AppShortcutStore? store = null)
    {
        this.store = store ?? new AppShortcutStore();
        Text = "Приложения · Honestly TUN"; Icon = HonestlyBrand.LoadIcon();
        BackColor = Astra.Background; ForeColor = Astra.Text; Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None; ClientSize = new Size(800, 610); MinimumSize = new Size(620, 470);
        Padding = new Padding(6); StartPosition = FormStartPosition.CenterParent; DoubleBuffered = true;
        var body = new AstraCanvas { Dock = DockStyle.Fill, BackColor = Astra.Background, Padding = new Padding(24, 14, 24, 18) };
        var heading = new AstraCanvas { Dock = DockStyle.Top, Height = 110, BackColor = Astra.Background };
        var title = Astra.Label("Приложения", 22, style: FontStyle.Bold); title.Location = new Point(0, 0);
        var description = Astra.Label("Открывайте приложения и возвращайтесь к их окнам.", 10, Astra.Muted);
        description.AutoSize = false; description.SetBounds(0, 44, 730, 26); description.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        var add = new AstraButton { Text = "+ Добавить .exe", AccessibleName = "Добавить приложение по пути к EXE", Kind = ButtonKind.Primary,
            Location = new Point(0, 76), Size = new Size(177, 34) };
        add.Click += (_, _) => AddApplication();
        heading.Controls.Add(title); heading.Controls.Add(description); heading.Controls.Add(add);
        status.Dock = DockStyle.Bottom; status.AutoSize = false; status.Height = 36; status.TextAlign = ContentAlignment.MiddleLeft;
        empty.AutoSize = false; empty.SetBounds(12, 16, 680, 64); grid.Controls.Add(empty);
        var page = new AstraCanvas { BackColor = Astra.Background }; page.Controls.Add(grid);
        var scroll = new AstraScrollHost(page, Astra.Background, alwaysVisible: false);
        body.Controls.Add(scroll); body.Controls.Add(status); body.Controls.Add(heading);
        Controls.Add(body); Controls.Add(new AstraTitleBar(this));
        grid.Resize += (_, _) => Reflow();
        refresh.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) =>
        {
            try { pinned = this.store.Load(); }
            catch (Exception ex) { status.Text = "Не удалось прочитать список: " + ex.Message; }
            await RefreshAsync();
            if (!released) refresh.Start();
        };
    }
    private void AddApplication()
    {
        using var dialog = new OpenFileDialog { Title = "Выберите приложение или вставьте путь к .exe", Filter = "Приложения (*.exe)|*.exe",
            CheckFileExists = true, Multiselect = false, RestoreDirectory = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            string path = AppShortcutStore.Normalize(dialog.FileName);
            var next = pinned.Where(item => !item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
            next.Add(new(path, AppWindows.DisplayName(path))); store.Save(next); pinned = next;
            status.Text = "Добавлено: " + next[^1].Name;
            _ = RefreshAsync();
        }
        catch (Exception ex) { status.Text = "Не удалось добавить: " + ex.Message; }
    }
    private async Task RefreshAsync()
    {
        if (reading || released || Control.MouseButtons != MouseButtons.None) return;
        reading = true;
        var known = new HashSet<string>(icons.Keys, StringComparer.OrdinalIgnoreCase);
        var selected = pinned.ToArray();
        var loaded = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var entries = await Task.Run(() =>
            {
                var found = AppWindows.Read(selected);
                foreach (var entry in found)
                    if (!known.Contains(entry.Key)) loaded[entry.Key] = AppWindows.LoadIcon(entry);
                return found;
            });
            if (released) return;
            foreach (var item in loaded) icons[item.Key] = item.Value;
            loaded.Clear();
            foreach (var key in tiles.Keys.Except(entries.Select(entry => entry.Key), StringComparer.OrdinalIgnoreCase).ToArray())
            { var tile = tiles[key]; grid.Controls.Remove(tile); hints.SetToolTip(tile, null); tile.ContextMenuStrip?.Dispose(); tile.Dispose(); tiles.Remove(key); }
            foreach (var entry in entries)
            {
                if (!tiles.TryGetValue(entry.Key, out var tile))
                {
                    tile = new AppTile(); tiles.Add(entry.Key, tile); grid.Controls.Add(tile);
                    tile.Click += (_, _) => InvokeApplication(tile.Entry!);
                    var menu = new ContextMenuStrip { BackColor = Astra.Surface, ForeColor = Astra.Text, ShowImageMargin = false };
                    var remove = menu.Items.Add("Убрать из добавленных");
                    menu.Opening += (_, e) => { e.Cancel = tile.Entry?.Pinned != true; };
                    remove.Click += (_, _) => Unpin(tile.Entry!);
                    tile.ContextMenuStrip = menu;
                }
                tile.UpdateEntry(entry, icons[entry.Key]);
                hints.SetToolTip(tile, (entry.Pinned ? "Добавлено · " : "") + (entry.Running ? "Запущено" : "Не запущено") + "\n" + entry.Path);
            }
            // Keep the same order, coordinates and scroll position on ordinary status refreshes.
            int order = 0;
            foreach (var entry in entries) tiles[entry.Key].TabIndex = order++;
            empty.Visible = entries.Length == 0;
            Reflow();
            status.Text = $"Открыто: {entries.Count(entry => entry.Running)}  ·  Добавлено: {pinned.Count}";
            foreach (var key in icons.Keys.Except(tiles.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
            { icons[key].Dispose(); icons.Remove(key); }
        }
        catch (Exception ex) { if (!released) status.Text = "Не удалось обновить список: " + ex.Message; }
        finally { foreach (var icon in loaded.Values) icon.Dispose(); reading = false; }
    }
    private void Reflow()
    {
        if (arranging || released) return;
        arranging = true;
        try
        {
            int D(int n) => (int)Math.Round(n * DeviceDpi / 96d);
            int gap = D(12), desired = D(132), height = D(142);
            int columns = Math.Max(1, (grid.ClientSize.Width - gap) / (desired + gap));
            int width = Math.Max(1, (grid.ClientSize.Width - gap * (columns + 1)) / columns), index = 0;
            foreach (var tile in tiles.Values.OrderBy(tile => tile.TabIndex))
            {
                tile.SetBounds(gap + index % columns * (width + gap), gap + index / columns * (height + gap), width, height); index++;
            }
            grid.Height = Math.Max(D(100), gap + (int)Math.Ceiling(index / (double)columns) * (height + gap));
            empty.Width = Math.Max(1, grid.Width - gap * 2);
        }
        finally { arranging = false; }
    }
    private void InvokeApplication(AppEntry entry)
    {
        try
        {
            if (AppWindows.Activate(entry)) return;
            if (entry.Running) { status.Text = entry.Name + " работает в фоне, открытого окна нет."; return; }
            AppWindows.Launch(entry.Path); status.Text = "Запуск: " + entry.Name;
            _ = RefreshAsync();
        }
        catch (Exception ex) { status.Text = "Не удалось открыть: " + ex.Message; }
    }
    private void Unpin(AppEntry entry)
    {
        try
        {
            var next = pinned.Where(item => !item.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            store.Save(next); pinned = next; _ = RefreshAsync();
        }
        catch (Exception ex) { status.Text = "Не удалось сохранить: " + ex.Message; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !released)
        {
            released = true; refresh.Dispose(); hints.Dispose();
            foreach (var tile in tiles.Values) tile.ContextMenuStrip?.Dispose();
            foreach (var icon in icons.Values) icon.Dispose(); icons.Clear();
        }
        base.Dispose(disposing);
    }
}

internal sealed class AppTile : Control, IButtonControl
{
    internal AppEntry? Entry { get; private set; }
    private Bitmap? icon;
    private bool hovered, pressed;
    [System.ComponentModel.DefaultValue(DialogResult.None)] public DialogResult DialogResult { get; set; }
    internal AppTile()
    {
        BackColor = Astra.Background; Font = new Font("Segoe UI", 9); Cursor = Cursors.Hand; TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
    }
    internal void UpdateEntry(AppEntry entry, Bitmap image)
    {
        if (Entry == entry && ReferenceEquals(icon, image)) return;
        Entry = entry; icon = image;
        AccessibleName = entry.Name + (entry.Running ? " — запущено" : " — не запущено");
        AccessibleDescription = entry.Pinned ? "Добавленное приложение. Нажмите, чтобы открыть." : "Открытое приложение. Нажмите, чтобы перейти к окну.";
        if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
        Invalidate();
    }
    public void NotifyDefault(bool value) { }
    public void PerformClick() { if (Enabled && Visible) OnClick(EventArgs.Empty); }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Focus(); pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnMouseCaptureChanged(EventArgs e) { pressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) { pressed = true; Invalidate(); e.Handled = true; e.SuppressKeyPress = true; }
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) { bool click = pressed; pressed = false; Invalidate(); if (click) PerformClick(); e.Handled = true; }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; using var background = new SolidBrush(BackColor); g.FillRectangle(background, ClientRectangle);
        if (Entry is null || Width < 2 || Height < 2) return;
        float s = DeviceDpi / 96f; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = Astra.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 14 * s);
        using var fill = new SolidBrush(Entry.Running ? Astra.ActiveSurface : hovered || pressed ? Astra.Input : Astra.Surface);
        g.FillPath(fill, shape);
        if (hovered || Focused) { using var pen = new Pen(Focused ? Astra.Accent : Astra.Border); g.DrawPath(pen, shape); }
        if (icon is not null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(icon, new RectangleF((Width - 44 * s) / 2, 16 * s, 44 * s, 44 * s));
        }
        TextRenderer.DrawText(g, Entry.Name, Font, new Rectangle((int)(8 * s), (int)(72 * s), Math.Max(1, Width - (int)(16 * s)), (int)(38 * s)), Astra.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using var small = new Font(Font.FontFamily, 8);
        TextRenderer.DrawText(g, Entry.Running ? "Запущено" : "Не запущено", small,
            new Rectangle((int)(6 * s), (int)(116 * s), Math.Max(1, Width - (int)(12 * s)), (int)(20 * s)), Entry.Running ? Astra.Green : Astra.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
    protected override AccessibleObject CreateAccessibilityInstance() => new TileAccess(this);
    private sealed class TileAccess(AppTile owner) : ControlAccessibleObject(owner)
    { public override string? DefaultAction => "Открыть"; public override void DoDefaultAction() => owner.PerformClick(); }
}
