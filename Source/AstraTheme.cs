using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VpnZapretSwitch;

internal static class Astra
{
    public static readonly Color Background = Color.FromArgb(17, 18, 23);
    public static readonly Color Sidebar = Color.FromArgb(22, 23, 29);
    public static readonly Color Surface = Color.FromArgb(28, 29, 37);
    public static readonly Color ActiveSurface = Color.FromArgb(28, 43, 39);
    public static readonly Color Input = Color.FromArgb(34, 35, 44);
    public static readonly Color Border = Color.FromArgb(48, 49, 61);
    public static readonly Color Text = Color.FromArgb(241, 241, 246);
    public static readonly Color Muted = Color.FromArgb(160, 162, 179);
    public static readonly Color Accent = Color.FromArgb(197, 182, 255);
    public static readonly Color AccentTint = Color.FromArgb(43, 38, 61);
    public static readonly Color Green = Color.FromArgb(133, 217, 177);
    public static readonly Color Amber = Color.FromArgb(245, 198, 129);
    public static readonly Color Red = Color.FromArgb(245, 157, 169);

    public static GraphicsPath Round(RectangleF bounds, float radius)
    {
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Label Label(string text, float size = 10, Color? color = null, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text, ForeColor = color ?? Text, BackColor = Color.Transparent,
        Font = new Font("Segoe UI", size, style), AutoSize = true, Margin = Padding.Empty
    };

    public static Color Backdrop(Control? control)
    {
        for (var current = control; current is not null; current = current.Parent)
        {
            if (current is AstraPanel panel) return panel.Fill;
            if (current is AstraStatusCard card) return card.Fill;
            if (current.BackColor.A == 255) return current.BackColor;
        }
        return Background;
    }
}

internal sealed class AstraLayoutPanel : TableLayoutPanel
{
    public AstraLayoutPanel() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }
}
internal sealed class AstraFlowPanel : FlowLayoutPanel
{
    public AstraFlowPanel() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }
}
internal sealed class AstraCanvas : Panel
{
    public AstraCanvas() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }
}

internal class AstraPanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Fill { get; set; } = Astra.Surface;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Stroke { get; set; } = Astra.Border;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 16;

    public AstraPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Astra.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), Radius * DeviceDpi / 96f);
        using var brush = new SolidBrush(Fill);
        using var pen = new Pen(Stroke);
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(pen, path);
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var fill = new SolidBrush(Astra.Backdrop(Parent));
        e.Graphics.FillRectangle(fill, ClientRectangle);
    }
}

internal enum ButtonKind { Primary, Secondary, Quiet, Navigation, Caption }
internal enum NavigationIcon { None, Overview, Settings, Journal, Applications, Pets, Accounts }

internal sealed class AstraButton : Control, IButtonControl
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind { get; set; } = ButtonKind.Secondary;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public NavigationIcon Icon { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => selected;
        set { if (selected == value) return; selected = value; Invalidate(); }
    }
    private bool selected;
    private bool hovered;
    private bool pressed;
    [DefaultValue(DialogResult.None)]
    public DialogResult DialogResult { get; set; }

    public AstraButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.Opaque, true);
        BackColor = Astra.Background;
        ForeColor = Astra.Text;
        Font = new Font("Segoe UI", 10, FontStyle.Regular);
        Cursor = Cursors.Hand;
        Height = 44;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
    }

    public void NotifyDefault(bool value) { Invalidate(); }
    public void PerformClick() { if (Enabled && Visible) OnClick(EventArgs.Empty); }
    protected override void OnClick(EventArgs e)
    {
        if (!Enabled) return;
        base.OnClick(e);
        if (DialogResult != DialogResult.None && FindForm() is { } form) form.DialogResult = DialogResult;
    }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Focus(); pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnMouseCaptureChanged(EventArgs e) { pressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { pressed = true; Invalidate(); e.Handled = true; e.SuppressKeyPress = true; }
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            bool click = pressed; pressed = false; Invalidate();
            if (click) PerformClick();
            e.Handled = true;
        }
        base.OnKeyUp(e);
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var fill = new SolidBrush(Astra.Backdrop(Parent));
        e.Graphics.FillRectangle(fill, ClientRectangle);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var backdrop = new SolidBrush(Astra.Backdrop(Parent))) g.FillRectangle(backdrop, ClientRectangle);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = Kind switch
        {
            ButtonKind.Primary => Enabled ? (hovered ? Color.FromArgb(213, 201, 255) : Astra.Accent) : Color.FromArgb(73, 67, 93),
            ButtonKind.Navigation => Selected ? Astra.AccentTint : hovered ? Astra.Input : Color.Transparent,
            ButtonKind.Quiet or ButtonKind.Caption => hovered ? Astra.Input : Color.Transparent,
            _ => hovered ? Color.FromArgb(47, 48, 60) : Astra.Input
        };
        Color ink = !Enabled ? Astra.Muted : Kind == ButtonKind.Primary ? Color.FromArgb(30, 24, 47)
            : Kind == ButtonKind.Navigation && Selected ? Astra.Accent : Astra.Text;
        if (Kind == ButtonKind.Caption && Text == "×" && hovered) { fill = Color.FromArgb(162, 58, 73); ink = Color.White; }
        if (pressed && Enabled && fill.A != 0) fill = ControlPaint.Dark(fill, .08f);
        using var path = Astra.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), (Kind == ButtonKind.Caption ? 5 : 10) * DeviceDpi / 96f);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        if (Kind == ButtonKind.Secondary || Focused)
        {
            using var pen = new Pen(Focused ? Astra.Accent : Astra.Border);
            g.DrawPath(pen, path);
        }
        var bounds = ClientRectangle;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
            | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
        if (Kind == ButtonKind.Caption)
        {
            using var pen = new Pen(ink, Math.Max(1, DeviceDpi / 96f));
            float cx = Width / 2f, cy = Height / 2f, radius = 4.5f * DeviceDpi / 96f;
            if (Text == "×") { g.DrawLine(pen, cx - radius, cy - radius, cx + radius, cy + radius); g.DrawLine(pen, cx + radius, cy - radius, cx - radius, cy + radius); }
            else if (Text == "—") g.DrawLine(pen, cx - radius, cy, cx + radius, cy);
            else g.DrawRectangle(pen, cx - radius, cy - radius, radius * 2, radius * 2);
            return;
        }
        if (Kind == ButtonKind.Navigation)
        {
            float scale = DeviceDpi / 96f;
            DrawNavigationIcon(g, ink, scale);
            bounds = new Rectangle((int)(46 * scale), 0, Width - (int)(50 * scale), Height);
            flags |= TextFormatFlags.Left;
        }
        else flags |= TextFormatFlags.HorizontalCenter;
        TextRenderer.DrawText(g, Text, Font, bounds, ink, flags);
    }

    private void DrawNavigationIcon(Graphics g, Color color, float scale)
    {
        var saved = g.Save();
        try
        {
        g.TranslateTransform(17 * scale, Height / 2f - 8 * scale);
        g.ScaleTransform(scale, scale);
        using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (Icon == NavigationIcon.Overview)
        {
            g.DrawRectangle(pen, 0, 0, 6, 6); g.DrawRectangle(pen, 10, 0, 6, 6);
            g.DrawRectangle(pen, 0, 10, 6, 6); g.DrawRectangle(pen, 10, 10, 6, 6);
        }
        else if (Icon == NavigationIcon.Settings)
        {
            g.DrawLine(pen, 0, 3, 16, 3); g.DrawLine(pen, 0, 12, 16, 12);
            using var brush = new SolidBrush(Astra.Sidebar);
            g.FillEllipse(brush, 3, 0, 6, 6); g.DrawEllipse(pen, 3, 0, 6, 6);
            g.FillEllipse(brush, 9, 9, 6, 6); g.DrawEllipse(pen, 9, 9, 6, 6);
        }
        else if (Icon == NavigationIcon.Journal)
        {
            g.DrawRectangle(pen, 1, 0, 13, 16);
            g.DrawLine(pen, 5, 5, 10, 5); g.DrawLine(pen, 5, 9, 10, 9); g.DrawLine(pen, 5, 13, 8, 13);
        }
        else if (Icon == NavigationIcon.Applications)
        {
            g.DrawRectangle(pen, 0, 0, 16, 15); g.DrawLine(pen, 0, 5, 16, 5);
            g.DrawLine(pen, 5, 5, 5, 15); g.DrawLine(pen, 8, 9, 12, 9);
        }
        else if (Icon == NavigationIcon.Pets)
        {
            using var brush = new SolidBrush(color);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(brush, 4f, 6.5f, 8f, 6.5f);
            g.FillEllipse(brush, 1.5f, 3.5f, 3f, 4f);
            g.FillEllipse(brush, 5.2f, 1f, 3.2f, 4.2f);
            g.FillEllipse(brush, 9.6f, 1f, 3.2f, 4.2f);
            g.FillEllipse(brush, 13.5f, 3.5f, 3f, 4f);
        }
        else if (Icon == NavigationIcon.Accounts)
        {
            g.DrawEllipse(pen, 5, 0, 6, 6);
            g.DrawArc(pen, 1, 8, 14, 14, 180, 180);
            g.DrawLine(pen, 1, 15, 1, 16); g.DrawLine(pen, 15, 15, 15, 16);
        }
        }
        finally { g.Restore(saved); }
    }
}

internal sealed class AstraActivityCard : AstraPanel
{
    private string message = "Проверяем состояние приложений…";
    private readonly ToolTip tooltip = new();
    private readonly AstraButton journal = new() { Text = "Журнал  →", Kind = ButtonKind.Quiet, Dock = DockStyle.Right, Width = 100, Margin = Padding.Empty };
    public event EventHandler? OpenJournal;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Message
    {
        get => message;
        set
        {
            message = value.ReplaceLineEndings(" ");
            AccessibleName = "Последнее действие: " + message;
            tooltip.SetToolTip(this, value);
            Invalidate();
        }
    }
    public AstraActivityCard()
    {
        Fill = Astra.Background;
        Padding = new Padding(18, 12, 12, 12);
        Radius = 12;
        Controls.Add(journal);
        journal.Click += (_, e) => OpenJournal?.Invoke(this, e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float scale = DeviceDpi / 96f;
        int left = (int)(20 * scale), width = Math.Max(1, journal.Left - left - (int)(12 * scale));
        using var heading = new Font("Segoe UI", 8);
        using var body = new Font("Segoe UI", 10);
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
            | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
        TextRenderer.DrawText(e.Graphics, "ПОСЛЕДНЕЕ ДЕЙСТВИЕ", heading,
            new Rectangle(left, (int)(12 * scale), width, (int)(20 * scale)), Astra.Muted, flags);
        TextRenderer.DrawText(e.Graphics, message, body,
            new Rectangle(left, (int)(33 * scale), width, (int)(27 * scale)), Astra.Muted, flags | TextFormatFlags.VerticalCenter);
    }
    protected override void Dispose(bool disposing) { if (disposing) tooltip.Dispose(); base.Dispose(disposing); }
}

internal sealed class AstraEmblem : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Busy
    {
        get => busy;
        set
        {
            busy = value;
            if (busy) animation.Start(); else animation.Stop();
            Invalidate();
        }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Compact { get; set; }
    private int angle;
    private bool busy;
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 45 };

    public AstraEmblem()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        animation.Tick += (_, _) => { if (Busy) { angle = (angle + 7) % 360; Invalidate(); } };
        AccessibleName = "Логотип Honestly TUN";
        AccessibleRole = AccessibleRole.Graphic;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float size = Math.Min(Width, Height), cx = Width / 2f, cy = Height / 2f;
        if (!Compact)
        {
            using var brush = new SolidBrush(Astra.AccentTint);
            g.FillEllipse(brush, cx - size * .46f, cy - size * .46f, size * .92f, size * .92f);
            using var border = new Pen(Color.FromArgb(70, 61, 99), 1);
            g.DrawEllipse(border, cx - size * .46f, cy - size * .46f, size * .92f, size * .92f);
            if (Busy)
            {
                using var spinner = new Pen(Astra.Accent, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(spinner, cx - size * .46f, cy - size * .46f, size * .92f, size * .92f, angle, 85);
            }
        }
        float logoWidth = size * (Compact ? .98f : .75f);
        float logoHeight = logoWidth * HonestlyBrand.Logo.Height / HonestlyBrand.Logo.Width;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(HonestlyBrand.Logo, cx - logoWidth / 2, cy - logoHeight / 2, logoWidth, logoHeight);
    }

    protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
}

internal sealed class AstraStatusCard : Control
{
    public Color Fill => Active ? Astra.ActiveSurface : Astra.Surface;
    public void AddPowerButton(AstraButton button)
    {
        Controls.Add(button);
        void Position()
        {
            float scale = DeviceDpi / 96f;
            button.SetBounds(Width - (int)(133 * scale), (int)(122 * scale), (int)(112 * scale), (int)(30 * scale));
        }
        Resize += (_, _) => Position(); Position();
    }
    public void AddOpenButton(AstraButton button)
    {
        Controls.Add(button);
        void Position()
        {
            float scale = DeviceDpi / 96f;
            button.SetBounds(Width - (int)(111 * scale), (int)(12 * scale), (int)(90 * scale), (int)(30 * scale));
        }
        Resize += (_, _) => Position();
        Position();
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string State { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Detail { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active { get; set; }

    public AstraStatusCard()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.StaticText;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        int pad = (int)(22 * scale);
        using var path = Astra.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 14 * scale);
        using var fill = new SolidBrush(Fill);
        using var border = new Pen(Active ? Color.FromArgb(66, 86, 79) : Astra.Border);
        g.FillPath(fill, path); g.DrawPath(border, path);
        using var dot = new SolidBrush(Active ? Astra.Green : Astra.Muted);
        g.FillEllipse(dot, pad, 25 * scale, 7 * scale, 7 * scale);
        using var heading = new Font("Segoe UI", 10, FontStyle.Regular);
        using var state = new Font("Segoe UI", 15, FontStyle.Bold);
        using var detail = new Font("Segoe UI", 9, FontStyle.Regular);
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
            | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
        TextRenderer.DrawText(g, Title, heading, new Rectangle(pad + (int)(15 * scale), (int)(17 * scale), Math.Max(1, Width - pad - (int)(130 * scale)), (int)(25 * scale)), Astra.Muted, flags);
        TextRenderer.DrawText(g, State, state, new Rectangle(pad, (int)(46 * scale), Width - pad * 2, (int)(34 * scale)), Astra.Text, flags);
        TextRenderer.DrawText(g, Detail, detail, new Rectangle(pad, (int)(87 * scale), Width - pad * 2, (int)(24 * scale)), Astra.Muted, flags);
    }
}

internal sealed class AstraComboBox : ComboBox
{
    public AstraComboBox()
    {
        FlatStyle = FlatStyle.Flat;
        BackColor = Astra.Input;
        ForeColor = Astra.Text;
        Font = new Font("Segoe UI", 10);
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 26;
        MaxDropDownItems = 9;
        IntegralHeight = false;
        DropDownHeight = 240;
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        using var fill = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Astra.AccentTint : Astra.Input);
        e.Graphics.FillRectangle(fill, e.Bounds);
        if (e.Index >= 0)
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
                Rectangle.Inflate(e.Bounds, -7, 0), Astra.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
    }
    protected override void WndProc(ref Message m)
    {
        int message = m.Msg;
        base.WndProc(ref m);
        if (message is not (0xF or 0x85 or 0x317 or 0x318) || !IsHandleCreated) return;
        bool print = message is 0x317 or 0x318;
        if (print && m.WParam == IntPtr.Zero) return;
        using var graphics = print ? Graphics.FromHdc(m.WParam) : Graphics.FromHwnd(Handle);
        using var background = new SolidBrush(Astra.Input);
        int edge = Math.Max(2, 2 * DeviceDpi / 96);
        int arrowWidth = Math.Max(24, 24 * DeviceDpi / 96);
        graphics.FillRectangle(background, 0, 0, Width, edge);
        graphics.FillRectangle(background, 0, Height - edge, Width, edge);
        graphics.FillRectangle(background, 0, 0, edge, Height);
        graphics.FillRectangle(background, Width - arrowWidth, 0, arrowWidth, Height);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var arrow = new Pen(Enabled ? Astra.Muted : Astra.Border, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = Width - arrowWidth / 2f, cy = Height / 2f;
        graphics.DrawLines(arrow, [new PointF(cx - 4, cy - 2), new PointF(cx, cy + 2), new PointF(cx + 4, cy - 2)]);
    }
}

internal sealed class AstraTitleBar : Panel
{
    private readonly Form owner;
    public AstraTitleBar(Form form)
    {
        owner = form;
        Dock = DockStyle.Top;
        Height = 42;
        BackColor = Astra.Background;
        var title = Astra.Label(form is MainForm ? "Honestly TUN" : form.Text, 9, Astra.Muted);
        title.AutoSize = false;
        title.Dock = DockStyle.Fill;
        title.TextAlign = ContentAlignment.MiddleLeft;
        title.Padding = new Padding(18, 0, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 132, WrapContents = false, BackColor = Astra.Background, Padding = new Padding(0, 4, 4, 4), Margin = Padding.Empty };
        foreach (var item in new[] { ("—", "Свернуть"), ("□", "Развернуть"), ("×", "Закрыть") })
        {
            var button = new AstraButton { Kind = ButtonKind.Caption, Text = item.Item1, AccessibleName = item.Item2, Size = new Size(40, 32), Margin = new Padding(1, 0, 1, 0), TabStop = false };
            button.Click += (_, _) =>
            {
                if (item.Item1 == "×") owner.Close();
                else if (item.Item1 == "—") owner.WindowState = FormWindowState.Minimized;
                else ToggleMaximize();
            };
            buttons.Controls.Add(button);
        }
        Controls.Add(title); Controls.Add(buttons);
        MouseDown += Drag; title.MouseDown += Drag;
        DoubleClick += (_, _) => ToggleMaximize(); title.DoubleClick += (_, _) => ToggleMaximize();
    }
    private void ToggleMaximize()
    {
        if (owner is MainForm main) main.ToggleWindowSize();
        else owner.WindowState = owner.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }
    private void Drag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(owner.Handle, 0xA1, 2, 0);
    }
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, nint wParam, nint lParam);
}
