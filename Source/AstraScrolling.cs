using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VpnZapretSwitch;

internal sealed class AstraScrollBar : Control
{
    private int maximum, page = 1, value, dragOffset;
    private bool dragging, hover;
    private int repeatDirection;
    private readonly System.Windows.Forms.Timer repeat = new() { Interval = 90 };
    public event Action<int>? StepRequested;
    public event Action<int, bool>? PositionRequested;
    public event Action<int>? WheelRequested;
    public int Value => value;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int SmallChange { get; set; } = 1;
    public AstraScrollBar()
    {
        Width = 18; Dock = DockStyle.Right; TabStop = true; AccessibleName = "Прокрутка вверх и вниз";
        AccessibleRole = AccessibleRole.ScrollBar;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        repeat.Tick += (_, _) => StepBy(repeatDirection);
    }
    public void SetRange(int maximum, int page, int current)
    {
        this.maximum = Math.Max(0, maximum); this.page = Math.Max(1, page);
        value = Math.Clamp(current, 0, this.maximum);
        Enabled = this.maximum > 0; Invalidate();
    }
    private int Arrow => Math.Max(14, (int)(18 * DeviceDpi / 96f));
    private Rectangle Track => new(4, Arrow + 3, Math.Max(3, Width - 8), Math.Max(1, Height - 2 * Arrow - 6));
    private Rectangle Thumb
    {
        get
        {
            var track = Track;
            int height = Math.Min(track.Height, Math.Max(24 * DeviceDpi / 96, (int)((long)track.Height * page / (maximum + (long)page))));
            int y = track.Top + (maximum == 0 ? 0 : (int)((long)(track.Height - height) * value / maximum));
            return new(track.X, y, track.Width, height);
        }
    }
    private void StepBy(int delta) => StepRequested?.Invoke(delta);
    private void Change(int next, bool immediate = false) => PositionRequested?.Invoke(Math.Clamp(next, 0, maximum), immediate);
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var thumb = Thumb;
        using var track = new SolidBrush(Astra.Input);
        using var path = Astra.Round(Track, Math.Max(1, Track.Width / 2f));
        e.Graphics.FillPath(track, path);
        if (Enabled)
        {
            using var brush = new SolidBrush(dragging || hover ? Astra.Accent : Color.FromArgb(100, 94, 126));
            using var shape = Astra.Round(thumb, Math.Max(1, thumb.Width / 2f));
            e.Graphics.FillPath(brush, shape);
        }
        using var pen = new Pen(Enabled ? Astra.Muted : Astra.Border, 1.3f);
        float mid = Width / 2f;
        e.Graphics.DrawLines(pen, new PointF[] { new(mid - 3, Arrow * .6f), new(mid, Arrow * .4f), new(mid + 3, Arrow * .6f) });
        e.Graphics.DrawLines(pen, new PointF[] { new(mid - 3, Height - Arrow * .6f), new(mid, Height - Arrow * .4f), new(mid + 3, Height - Arrow * .6f) });
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Focus(); Capture = true;
        if (Thumb.Contains(e.Location)) { dragging = true; dragOffset = e.Y - Thumb.Top; }
        else if (e.Y < Arrow || e.Y >= Height - Arrow)
        { repeatDirection = e.Y < Arrow ? -SmallChange : SmallChange; StepBy(repeatDirection); repeat.Start(); }
        else StepBy(e.Y < Thumb.Top ? -page : page);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        hover = Thumb.Contains(e.Location); Invalidate();
        if (dragging)
        {
            int travel = Track.Height - Thumb.Height;
            if (travel > 0) Change((int)Math.Round((e.Y - dragOffset - Track.Top) * (double)maximum / travel), immediate: true);
        }
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Capture = false; }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); dragging = false; repeat.Stop(); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); WheelRequested?.Invoke(e.Delta); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Up: StepBy(-SmallChange); break; case Keys.Down: StepBy(SmallChange); break;
            case Keys.PageUp: StepBy(-page); break; case Keys.PageDown: StepBy(page); break;
            case Keys.Home: Change(0); break; case Keys.End: Change(maximum); break;
            default: return;
        }
        e.Handled = true;
    }
    protected override void Dispose(bool disposing) { if (disposing) repeat.Dispose(); base.Dispose(disposing); }
}

internal sealed class AstraHorizontalScrollBar : Control
{
    private int maximum, page = 1, value, dragOffset;
    private bool dragging, hover;
    private int repeatDirection;
    private readonly System.Windows.Forms.Timer repeat = new() { Interval = 90 };
    public event Action<int>? StepRequested;
    public event Action<int, bool>? PositionRequested;
    public event Action<int>? WheelRequested;
    public int Value => value;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int SmallChange { get; set; } = 40;

    public AstraHorizontalScrollBar()
    {
        Height = 18; Dock = DockStyle.None; TabStop = true; AccessibleName = "Прокрутка влево и вправо";
        AccessibleRole = AccessibleRole.ScrollBar;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        repeat.Tick += (_, _) => StepBy(repeatDirection);
    }

    public void SetRange(int maximum, int page, int current)
    {
        this.maximum = Math.Max(0, maximum);
        this.page = Math.Max(1, page);
        value = Math.Clamp(current, 0, this.maximum);
        Enabled = this.maximum > 0;
        Invalidate();
    }

    private int Arrow => Math.Max(14, (int)(18 * DeviceDpi / 96f));
    private Rectangle Track => new(Arrow + 3, 4, Math.Max(1, Width - 2 * Arrow - 6), Math.Max(3, Height - 8));
    private Rectangle Thumb
    {
        get
        {
            var track = Track;
            int thumbWidth = Math.Min(track.Width, Math.Max((int)(30 * DeviceDpi / 96f), (int)((long)track.Width * page / (maximum + (long)page))));
            int x = track.Left + (maximum == 0 ? 0 : (int)((long)(track.Width - thumbWidth) * value / maximum));
            return new(x, track.Y, thumbWidth, track.Height);
        }
    }

    private void StepBy(int delta) => StepRequested?.Invoke(delta);
    private void Change(int next, bool immediate = false) => PositionRequested?.Invoke(Math.Clamp(next, 0, maximum), immediate);

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var thumb = Thumb;
        using var trackBrush = new SolidBrush(Astra.Input);
        using var path = Astra.Round(Track, Math.Max(1, Track.Height / 2f));
        e.Graphics.FillPath(trackBrush, path);
        if (Enabled)
        {
            using var brush = new SolidBrush(dragging || hover ? Astra.Accent : Color.FromArgb(100, 94, 126));
            using var shape = Astra.Round(thumb, Math.Max(1, thumb.Height / 2f));
            e.Graphics.FillPath(brush, shape);
        }
        using var pen = new Pen(Enabled ? Astra.Muted : Astra.Border, 1.3f);
        float mid = Height / 2f;
        // Left arrow
        e.Graphics.DrawLines(pen, new PointF[] { new(Arrow * .6f, mid - 3), new(Arrow * .4f, mid), new(Arrow * .6f, mid + 3) });
        // Right arrow
        e.Graphics.DrawLines(pen, new PointF[] { new(Width - Arrow * .6f, mid - 3), new(Width - Arrow * .4f, mid), new(Width - Arrow * .6f, mid + 3) });
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Focus();
        Capture = true;
        if (Thumb.Contains(e.Location))
        {
            dragging = true;
            dragOffset = e.X - Thumb.Left;
        }
        else if (e.X < Arrow || e.X >= Width - Arrow)
        {
            repeatDirection = e.X < Arrow ? -SmallChange : SmallChange;
            StepBy(repeatDirection);
            repeat.Start();
        }
        else
        {
            StepBy(e.X < Thumb.Left ? -page : page);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        hover = Thumb.Contains(e.Location);
        Invalidate();
        if (dragging)
        {
            int travel = Track.Width - Thumb.Width;
            if (travel > 0)
                Change((int)Math.Round((e.X - dragOffset - Track.Left) * (double)maximum / travel), immediate: true);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Capture = false; }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); dragging = false; repeat.Stop(); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); WheelRequested?.Invoke(e.Delta); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left: StepBy(-SmallChange); break;
            case Keys.Right: StepBy(SmallChange); break;
            case Keys.PageUp: StepBy(-page); break;
            case Keys.PageDown: StepBy(page); break;
            case Keys.Home: Change(0); break;
            case Keys.End: Change(maximum); break;
            default: return;
        }
        e.Handled = true;
    }
    protected override void Dispose(bool disposing) { if (disposing) repeat.Dispose(); base.Dispose(disposing); }
}

// Contents and scrollbar occupy separate clipped windows. Pages never enable
// AutoScroll. The log's native scrollbar stays outside the clipping viewport,
// so Windows can repaint/recreate it without exposing even a single frame.
internal sealed class AstraScrollHost : Panel
{
    private readonly Control target;
    private readonly bool alwaysVisible;
    private readonly AstraCanvas viewport;
    private readonly AstraScrollBar bar = new();
    private readonly ScrollMotion motion = new();
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 15 };
    private bool arranging, applying;
    private long previousFrame;
    private int lastDpi;
    public AstraScrollHost(Control target, Color background, bool alwaysVisible = true)
    {
        this.target = target; this.alwaysVisible = alwaysVisible; lastDpi = DeviceDpi;
        Dock = DockStyle.Fill; Margin = Padding.Empty; BackColor = background;
        DoubleBuffered = true;
        viewport = new AstraCanvas { Dock = DockStyle.Fill, BackColor = background, Margin = Padding.Empty };
        bar.BackColor = background; bar.Visible = alwaysVisible;
        target.Dock = DockStyle.None; target.Anchor = AnchorStyles.Top | AnchorStyles.Left; target.Margin = Padding.Empty;
        if (target is ScrollableControl page) page.AutoScroll = false;
        if (target is AstraLogBox log)
        {
            log.ScrollBars = ScrollBars.Vertical;
            log.ViewportChanged += SyncNativeLog;
        }
        if (target is AstraServerList list)
        {
            list.ExtentChanged += RefreshExtent;
            list.RevealRequested += RevealRow;
        }
        viewport.Controls.Add(target);
        Controls.Add(viewport); Controls.Add(bar);
        bar.StepRequested += delta => AnimateBy(delta);
        bar.PositionRequested += (position, immediate) =>
        {
            if (immediate) { animation.Stop(); motion.Snap(position); ApplyPosition(); }
            else AnimateTo(position);
        };
        bar.WheelRequested += delta => { ScrollWheel(delta); };
        animation.Tick += (_, _) => AnimateFrame();
        viewport.Resize += (_, _) => RefreshExtent();
        target.HandleCreated += (_, _) => RefreshExtent();
        target.TextChanged += (_, _) => RefreshExtent();
        target.Layout += (_, _) => { if (!applying) RefreshExtent(); };
        VisibleChanged += (_, _) =>
        {
            if (Visible) RefreshExtent();
            else { animation.Stop(); motion.Snap(motion.Position); }
        };
        WatchFocus(target);
        AstraWheelRouter.Register(this);
    }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); RefreshExtent(); }
    private void WatchFocus(Control control)
    {
        control.Enter += (_, _) =>
        {
            if (!Visible || Control.MouseButtons != MouseButtons.None || target is not ScrollableControl || control.Height >= viewport.Height) return;
            int top = target.PointToClient(control.PointToScreen(Point.Empty)).Y;
            Reveal(top, control.Height);
        };
        foreach (Control child in control.Controls) WatchFocus(child);
    }
    private int PageContentHeight()
    {
        int fixedHeight = 0, fillMinimum = 0, bottom = 0;
        foreach (Control child in target.Controls)
        {
            // Hidden navigation pages are measured on show, when their contents are visible.
            if (!child.Visible) continue;
            if (child.Dock is DockStyle.Top or DockStyle.Bottom) fixedHeight += child.Height;
            else if (child.Dock == DockStyle.Fill) fillMinimum = Math.Max(fillMinimum, child.MinimumSize.Height);
            else bottom = Math.Max(bottom, child.Bottom + target.Padding.Bottom);
        }
        return Math.Max(viewport.Height, Math.Max(target.MinimumSize.Height,
            Math.Max(bottom, fixedHeight + fillMinimum + target.Padding.Vertical)));
    }
    private void RefreshExtent() => RefreshExtentCore(apply: true);
    private void RefreshExtentCore(bool apply)
    {
        if (arranging || applying || IsDisposed || !Visible || viewport is null || target.IsDisposed) return;
        arranging = true;
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                int width = Math.Max(1, viewport.ClientSize.Width), height = Math.Max(1, viewport.ClientSize.Height);
                int maximum;
                if (target is AstraLogBox text)
                {
                    int gutter = NativeScrollWidth();
                    target.SetBounds(0, 0, width + gutter, height);
                    int lines = text.IsHandleCreated ? NativeLines(text) : 1;
                    int visible = Math.Max(1, (height - 4) / Math.Max(1, text.Font.Height));
                    maximum = Math.Max(0, lines - visible) * Math.Max(1, text.Font.Height);
                    bar.SmallChange = Math.Max(1, text.Font.Height);
                }
                else if (target is AstraServerList list)
                {
                    target.SetBounds(0, 0, width, height);
                    maximum = Math.Max(0, list.ContentHeight - height);
                    bar.SmallChange = Math.Max(1, 24 * DeviceDpi / 96);
                }
                else
                {
                    int content = PageContentHeight();
                    target.SetBounds(0, -(int)Math.Round(motion.Position), width, content);
                    maximum = Math.Max(0, content - height);
                    bar.SmallChange = Math.Max(1, 24 * DeviceDpi / 96);
                }
                motion.SetMaximum(maximum);
                bool show = alwaysVisible || maximum > 0;
                if (bar.Visible == show) break;
                bar.Visible = show; PerformLayout();
            }
        }
        finally { arranging = false; }
        if (apply) ApplyPosition();
    }
    private void ApplyPosition()
    {
        if (applying || arranging || IsDisposed) return;
        applying = true;
        try
        {
            int offset = (int)Math.Round(motion.Position);
            if (target is AstraLogBox text && text.IsHandleCreated)
            {
                int first = NativeFirst(text), next = (int)Math.Round(motion.Position / Math.Max(1, text.Font.Height));
                if (first != next) SendMessage(text.Handle, 0xB6, IntPtr.Zero, (IntPtr)(next - first));
            }
            else if (target is AstraServerList list) list.SetScrollOffset(offset);
            else target.Top = -offset;
            bar.SetRange(motion.Maximum, Math.Max(1, viewport.Height), offset);
        }
        finally { applying = false; }
    }
    private void SyncNativeLog()
    {
        if (applying || arranging || target is not AstraLogBox log || !log.IsHandleCreated) return;
        int actual = NativeFirst(log) * Math.Max(1, log.Font.Height);
        animation.Stop(); RefreshExtentCore(apply: false);
        motion.Snap(actual); ApplyPosition();
    }
    private void RevealRow(int index)
    {
        if (target is AstraServerList list) Reveal(index * list.ItemHeight, list.ItemHeight);
    }
    private void Reveal(int top, int height)
    {
        if (top < motion.Destination) AnimateTo(top);
        else if (top + height > motion.Destination + viewport.Height) AnimateTo(top + height - viewport.Height);
    }
    private void AnimateBy(double pixels) { motion.MoveBy(pixels); StartAnimation(); }
    private void AnimateTo(double pixels) { motion.MoveTo(pixels); StartAnimation(); }
    private void StartAnimation()
    {
        if (!motion.Moving) return;
        if (!animation.Enabled) { previousFrame = Stopwatch.GetTimestamp(); animation.Start(); }
    }
    private void AnimateFrame()
    {
        if (!Visible || IsDisposed) { animation.Stop(); return; }
        long now = Stopwatch.GetTimestamp();
        double seconds = (now - previousFrame) / (double)Stopwatch.Frequency; previousFrame = now;
        motion.Advance(seconds); ApplyPosition();
        if (!motion.Moving) animation.Stop();
    }
    internal bool ScrollWheel(int delta)
    {
        if (!Visible || !Enabled || delta == 0) return false;
        RefreshExtent();
        int lines = SystemInformation.MouseWheelScrollLines;
        if (lines == 0) return true;
        double amount = -(delta / 120d) * (lines < 0 ? viewport.Height : lines * bar.SmallChange);
        double previous = motion.Destination;
        motion.MoveBy(amount);
        if (Math.Abs(previous - motion.Destination) < .001) return false;
        StartAnimation(); return true;
    }
    internal void StopMotion() { animation.Stop(); motion.Snap(motion.Position); ApplyPosition(); }
    private int NativeScrollWidth()
    {
        try { return GetSystemMetricsForDpi(2, (uint)DeviceDpi); }
        catch (EntryPointNotFoundException) { return SystemInformation.VerticalScrollBarWidth; }
    }
    private static int NativeLines(TextBox text) => (int)SendMessage(text.Handle, 0xBA, IntPtr.Zero, IntPtr.Zero);
    private static int NativeFirst(TextBox text) => (int)SendMessage(text.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero);
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        double offset = motion.Position * DeviceDpi / Math.Max(1d, lastDpi); lastDpi = DeviceDpi;
        base.OnDpiChangedAfterParent(e); bar.Width = 18 * DeviceDpi / 96;
        animation.Stop(); RefreshExtent(); motion.Snap(offset); ApplyPosition();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { animation.Dispose(); AstraWheelRouter.Unregister(this); }
        base.Dispose(disposing);
    }
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}

// Route wheel input to the innermost host under the pointer, including labels
// and buttons. A boundary forwards to the parent page; dropdowns retain input.
internal sealed class AstraWheelRouter : IMessageFilter
{
    private static readonly AstraWheelRouter Shared = new();
    private static readonly HashSet<AstraScrollHost> Hosts = [];
    internal static void Register(AstraScrollHost host)
    { if (Hosts.Count == 0) Application.AddMessageFilter(Shared); Hosts.Add(host); }
    internal static void Unregister(AstraScrollHost host)
    { Hosts.Remove(host); if (Hosts.Count == 0) Application.RemoveMessageFilter(Shared); }
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg is 0x201 or 0x204)
        {
            for (Control? target = Control.FromChildHandle(m.HWnd); target is not null; target = target.Parent)
                if (target is AstraScrollHost host) host.StopMotion();
            return false;
        }
        if (m.Msg != 0x20A || (Control.ModifierKeys & Keys.Control) != 0) return false;
        var point = new NativePoint { X = unchecked((short)(long)m.LParam), Y = unchecked((short)((long)m.LParam >> 16)) };
        Control? hit = Control.FromChildHandle(WindowFromPoint(point));
        var recipient = Control.FromChildHandle(m.HWnd);
        if (hit is null || recipient is null || hit.FindForm() != recipient.FindForm()) return false;
        for (Control? check = hit; check is not null; check = check.Parent)
            if (check is ComboBox) return false;
        int delta = unchecked((short)((long)m.WParam >> 16));
        for (Control? control = hit; control is not null; control = control.Parent)
            if (control is AstraScrollHost host && Hosts.Contains(host) && host.ScrollWheel(delta)) return true;
        return false;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
}
internal sealed class AstraLogBox : TextBox
{
    public event Action? ViewportChanged;
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg is 0x115 or 0x100 or 0x202 or 0xB6 || m.Msg == 0x200 && ((long)m.WParam & 1) != 0)
            ViewportChanged?.Invoke();
    }
    public void AppendEntry(string entry)
    {
        if (!IsHandleCreated) { AppendText(entry); return; }
        int first = (int)SendMessage(Handle, 0xCE, IntPtr.Zero, IntPtr.Zero);
        int count = (int)SendMessage(Handle, 0xBA, IntPtr.Zero, IntPtr.Zero);
        int visible = Math.Max(1, (ClientSize.Height - 4) / Math.Max(1, Font.Height));
        bool follow = first + visible >= count - 1;
        int start = SelectionStart, length = SelectionLength;
        SendMessage(Handle, 0xB, IntPtr.Zero, IntPtr.Zero); // WM_SETREDRAW
        try
        {
            AppendText(entry);
            if (follow) { SelectionStart = TextLength; SelectionLength = 0; ScrollToCaret(); }
            else
            {
                Select(start, length);
                int now = (int)SendMessage(Handle, 0xCE, IntPtr.Zero, IntPtr.Zero);
                SendMessage(Handle, 0xB6, IntPtr.Zero, (IntPtr)(first - now));
            }
        }
        finally { SendMessage(Handle, 0xB, (IntPtr)1, IntPtr.Zero); Invalidate(); ViewportChanged?.Invoke(); }
    }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
