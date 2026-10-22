using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;

namespace VpnZapretSwitch;

internal sealed class SpotifyQueueFlyout : Form
{
    private readonly SpotifyApi api;
    private readonly SpotifyPanel panel;
    private readonly System.Windows.Forms.Timer animTimer = new() { Interval = 15 };

    private readonly Label headerTitle = Astra.Label("Очередь", 11, Astra.Text, FontStyle.Bold);
    private readonly AstraButton fullscreenBtn = new() { Text = "⤢", Kind = ButtonKind.Quiet, Size = new Size(28, 26) };
    private readonly AstraButton closeBtn = new() { Text = "✕", Kind = ButtonKind.Quiet, Size = new Size(28, 26) };

    private readonly NowPlayingCard nowPlayingCard;
    private readonly Label nextSectionHeader = Astra.Label("ДАЛЕЕ В ОЧЕРЕДИ", 8f, Astra.Muted, FontStyle.Bold);
    private readonly AstraFlowPanel queueFlow = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        BackColor = Astra.Surface,
        Margin = Padding.Empty,
        Padding = Padding.Empty
    };

    private readonly AstraScrollHost scrollHost;
    private readonly Label emptyLabel = Astra.Label("Очередь пуста\nВключите плейлист или альбом в Spotify", 9.5f, Astra.Muted);
    private readonly AstraButton connectPromptBtn = new() { Text = "Связать Spotify", Kind = ButtonKind.Primary, Size = new Size(160, 32) };

    private bool opening;
    private bool closing;
    private int targetHeight;
    private int targetWidth;
    private int anchorLeft;
    private int anchorBottom;
    private DateTime animStartTime;
    private const int AnimDurationMs = 200;
    private List<string> loadedUris = new();

    internal bool IsOpen => Visible && !closing;

    internal SpotifyQueueFlyout(SpotifyApi api, SpotifyPanel panel)
    {
        this.api = api;
        this.panel = panel;
        nowPlayingCard = new NowPlayingCard(panel);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Astra.Surface;
        ForeColor = Astra.Text;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        headerTitle.AutoSize = true;
        closeBtn.Click += (_, _) => CloseAnimated();
        fullscreenBtn.Click += (_, _) =>
        {
            var owner = Owner ?? FindForm();
            CloseAnimated();
            if (owner is not null) panel.OpenFullscreenView(owner);
        };

        emptyLabel.AutoSize = false;
        emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
        emptyLabel.Visible = false;

        connectPromptBtn.Visible = false;
        connectPromptBtn.Click += (_, _) =>
        {
            CloseAnimated();
            if (Owner is Form mainForm) panel.OpenConnection(mainForm);
        };

        var queueContainer = new AstraCanvas { BackColor = Astra.Surface, AutoSize = true };
        queueContainer.Controls.Add(queueFlow);
        scrollHost = new AstraScrollHost(queueContainer, Astra.Surface, alwaysVisible: false)
        {
            Dock = DockStyle.None
        };

        Controls.Add(headerTitle);
        Controls.Add(fullscreenBtn);
        Controls.Add(closeBtn);
        Controls.Add(nowPlayingCard);
        Controls.Add(nextSectionHeader);
        Controls.Add(scrollHost);
        Controls.Add(emptyLabel);
        Controls.Add(connectPromptBtn);

        animTimer.Tick += (_, _) => OnAnimTick();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int preference = 2; // DWMWCP_ROUND (round corners with drop shadow)
            DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref preference, sizeof(int));
        }
        catch { }
        UpdateRoundedRegion();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRoundedRegion();
    }

    private void UpdateRoundedRegion()
    {
        if (!IsHandleCreated || Width < 10 || Height < 10) return;
        try
        {
            float s = DeviceDpi / 96f;
            int r = (int)Math.Round(14 * s);
            IntPtr hrgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r * 2, r * 2);
            if (hrgn != IntPtr.Zero)
            {
                var old = Region;
                Region = Region.FromHrgn(hrgn);
                DeleteObject(hrgn);
                old?.Dispose();
            }
        }
        catch { }
    }

    internal void OpenAnimated(Form owner)
    {
        Owner = owner;
        UpdateAnchorCoordinates();

        targetHeight = (int)Math.Round(380 * DeviceDpi / 96d);
        targetWidth = Math.Max((int)Math.Round(310 * DeviceDpi / 96d), panel.Width + (int)Math.Round(16 * DeviceDpi / 96d));

        Size = new Size(targetWidth, targetHeight);
        UpdateRoundedRegion();
        LayoutControls();

        opening = true;
        closing = false;
        animStartTime = DateTime.UtcNow;

        int slideDistance = (int)Math.Round(16 * DeviceDpi / 96d);
        int finalTop = anchorBottom - targetHeight;

        Left = anchorLeft;
        Top = finalTop + slideDistance;
        Opacity = 0.05;

        Show();
        BringToFront();

        animTimer.Start();
        _ = LoadQueueAsync();

        owner.LocationChanged -= OnOwnerMoved;
        owner.LocationChanged += OnOwnerMoved;
        owner.SizeChanged -= OnOwnerMoved;
        owner.SizeChanged += OnOwnerMoved;
    }

    internal void CloseAnimated()
    {
        if (closing || !Visible) return;
        closing = true;
        opening = false;
        animStartTime = DateTime.UtcNow;
        animTimer.Start();
    }

    private void UpdateAnchorCoordinates()
    {
        try
        {
            Point pt = panel.PointToScreen(Point.Empty);
            anchorLeft = pt.X;
            anchorBottom = pt.Y - (int)Math.Round(6 * DeviceDpi / 96d);
        }
        catch { }
    }

    private void OnOwnerMoved(object? sender, EventArgs e)
    {
        if (!Visible) return;
        UpdateAnchorCoordinates();
        Left = anchorLeft;
        Top = anchorBottom - targetHeight;
    }

    private void OnAnimTick()
    {
        double elapsed = (DateTime.UtcNow - animStartTime).TotalMilliseconds;
        double t = Math.Clamp(elapsed / AnimDurationMs, 0.0, 1.0);
        int slideDist = (int)Math.Round(16 * DeviceDpi / 96d);
        int targetTop = anchorBottom - targetHeight;

        if (opening)
        {
            double eased = 1.0 - Math.Pow(1.0 - t, 3.0); // cubic ease-out
            Top = targetTop + (int)Math.Round(slideDist * (1.0 - eased));
            Opacity = Math.Clamp(0.1 + 0.9 * eased, 0.0, 1.0);

            if (t >= 1.0)
            {
                animTimer.Stop();
                opening = false;
                Top = targetTop;
                Opacity = 1.0;
            }
        }
        else if (closing)
        {
            double eased = Math.Pow(1.0 - t, 3.0); // cubic ease-in
            Top = targetTop + (int)Math.Round(slideDist * (1.0 - eased));
            Opacity = Math.Clamp(eased, 0.0, 1.0);

            if (t >= 1.0)
            {
                animTimer.Stop();
                closing = false;
                Hide();
                panel.SetQueueActive(false);
            }
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            panel.SetQueueActive(false);
        }
    }

    private void LayoutControls()
    {
        if (scrollHost is null || nowPlayingCard is null || emptyLabel is null || connectPromptBtn is null) return;
        int D(int n) => (int)Math.Round(n * DeviceDpi / 96d);
        int pad = D(12);
        int w = Width - pad * 2;

        headerTitle.Location = new Point(pad, D(12));
        closeBtn.Location = new Point(Width - pad - D(28), D(10));
        fullscreenBtn.Location = new Point(Width - pad - D(58), D(10));

        nowPlayingCard.SetBounds(pad, D(46), w, D(68));
        nextSectionHeader.Location = new Point(pad, D(124));

        int scrollY = D(144);
        int scrollH = Math.Max(D(60), Height - scrollY - pad);
        scrollHost.SetBounds(pad, scrollY, w, scrollH);

        emptyLabel.SetBounds(pad, scrollY + D(20), w, D(60));
        connectPromptBtn.Location = new Point((Width - connectPromptBtn.Width) / 2, scrollY + D(30));

        int rowW = w - (int)Math.Round(22 * DeviceDpi / 96d);
        foreach (Control c in queueFlow.Controls)
        {
            c.Width = rowW;
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        LayoutControls();
    }

    internal async Task LoadQueueAsync()
    {
        if (!api.Connection.Linked)
        {
            nowPlayingCard.Visible = false;
            nextSectionHeader.Visible = false;
            scrollHost.Visible = false;
            emptyLabel.Text = "Свяжите Spotify с HonestlyTUN,\nчтобы просматривать очередь треков";
            emptyLabel.Visible = true;
            connectPromptBtn.Visible = true;
            return;
        }

        connectPromptBtn.Visible = false;
        nowPlayingCard.Visible = true;
        nextSectionHeader.Visible = true;
        scrollHost.Visible = true;

        var queue = await api.GetQueueAsync();

        if (queue is null)
        {
            nowPlayingCard.UpdateTrack(null);
            emptyLabel.Text = "Нет активного воспроизведения в Spotify";
            emptyLabel.Visible = true;
            return;
        }

        // 1. Now Playing
        nowPlayingCard.UpdateTrack(queue.CurrentlyPlaying);

        // 2. Queue Items
        var items = queue.Items;

        if (items.Count == 0)
        {
            emptyLabel.Text = "Очередь пуста\nВключите альбом или плейлист в Spotify";
            emptyLabel.Visible = true;
            queueFlow.Controls.Clear();
            loadedUris.Clear();
            return;
        }

        emptyLabel.Visible = false;

        // Check if items are unchanged to avoid rebuilding and flicker
        var newUris = items.Select(x => x.Uri).ToList();
        if (loadedUris.Count == newUris.Count && loadedUris.SequenceEqual(newUris))
        {
            return;
        }

        int D(int n) => (int)Math.Round(n * DeviceDpi / 96d);
        int pad = D(12);
        int w = Width - pad * 2;
        int rowW = w - (int)Math.Round(22 * DeviceDpi / 96d);

        queueFlow.SuspendLayout();
        queueFlow.Controls.Clear();

        for (int i = 0; i < items.Count; i++)
        {
            int queueIdx = i;
            var item = items[i];
            var row = new QueueTrackRow(item, rowW);

            row.Click += async (_, _) =>
            {
                row.SetHighlight(true);
                // Skip to this track in queue without wiping or resetting the queue
                bool ok = await api.SkipToQueueItemAsync(queueIdx);
                if (ok)
                {
                    await Task.Delay(500);
                    await LoadQueueAsync();
                }
            };

            queueFlow.Controls.Add(row);
        }

        loadedUris = newUris;
        queueFlow.ResumeLayout(true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float s = DeviceDpi / 96f;
        float radius = 14 * s;
        using var shape = Astra.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), radius);
        using var fill = new SolidBrush(Astra.Surface);
        g.FillPath(fill, shape);

        using var stroke = new Pen(Astra.Border, 1.2f);
        g.DrawPath(stroke, shape);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            animTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("gdi32.dll", EntryPoint = "CreateRoundRectRgn")]
    private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}

internal sealed class NowPlayingCard : Control
{
    private readonly SpotifyPanel panel;
    private SpotifyQueueItem? track;

    internal NowPlayingCard(SpotifyPanel panel)
    {
        this.panel = panel;
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    internal void UpdateTrack(SpotifyQueueItem? item)
    {
        track = item;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;

        // Rounded card background
        using var shape = Astra.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 10 * s);
        using var fill = new SolidBrush(Astra.Input);
        g.FillPath(fill, shape);

        // Artwork thumbnail
        int imgSize = (int)(44 * s);
        int imgX = (int)(10 * s);
        int imgY = (int)((Height - imgSize) / 2);
        var imgRect = new Rectangle(imgX, imgY, imgSize, imgSize);
        float imgRadius = 6 * s;

        Image? cover = null;
        if (track is not null && !string.IsNullOrWhiteSpace(track.ImageUrl))
        {
            cover = SpotifyImageCache.GetImage(track.ImageUrl, () =>
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    try { BeginInvoke(new Action(Invalidate)); } catch { }
                }
            });
        }
        cover ??= panel.CurrentArtwork;

        using var imgClip = Astra.Round(new RectangleF(imgRect.X, imgRect.Y, imgRect.Width, imgRect.Height), imgRadius);
        var saved = g.Save();
        g.SetClip(imgClip);

        if (cover is not null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(cover, imgRect);
        }
        else
        {
            using var phBrush = new SolidBrush(Astra.Surface);
            g.FillRectangle(phBrush, imgRect);
            using var noteFont = new Font("Segoe UI", 12f);
            TextRenderer.DrawText(g, "♪", noteFont, imgRect, Astra.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        g.Restore(saved);

        using var imgBorder = new Pen(Color.FromArgb(40, 255, 255, 255), 1f);
        g.DrawPath(imgBorder, imgClip);

        // Text info
        int textLeft = imgX + imgSize + (int)(10 * s);
        int durationW = (int)(55 * s);
        int rightPad = (int)(10 * s);
        int textW = Math.Max(20, Width - textLeft - durationW - rightPad);

        // Header "СЕЙЧАС ИГРАЕТ"
        using var headerFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        TextRenderer.DrawText(g, "СЕЙЧАС ИГРАЕТ", headerFont,
            new Rectangle(textLeft, (int)(10 * s), textW, (int)(14 * s)),
            Astra.Accent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        // Track title
        string title = track?.Name ?? "Трек не выбран";
        using var titleFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        TextRenderer.DrawText(g, title, titleFont,
            new Rectangle(textLeft, (int)(25 * s), textW, (int)(18 * s)),
            Astra.Text,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // Track artist
        string artist = track?.Artist ?? (panel.IsDisposed ? "" : "Spotify приостановлен");
        using var artistFont = new Font("Segoe UI", 8.5f);
        TextRenderer.DrawText(g, artist, artistFont,
            new Rectangle(textLeft, (int)(44 * s), textW, (int)(16 * s)),
            Astra.Muted,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // Duration (on the right, never blocked)
        string duration = track?.DurationText ?? "—";
        TextRenderer.DrawText(g, duration, artistFont,
            new Rectangle(Width - durationW - rightPad, (int)(44 * s), durationW, (int)(16 * s)),
            Astra.Muted,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }
}

internal sealed class QueueTrackRow : Control
{
    private readonly SpotifyQueueItem item;
    private bool hovered, pressed, active;

    internal QueueTrackRow(SpotifyQueueItem item, int width)
    {
        this.item = item;
        Width = width;
        Height = 44;
        Cursor = Cursors.Hand;
        BackColor = Astra.Surface;
        Font = new Font("Segoe UI", 9f);
        DoubleBuffered = true;
        AccessibleName = $"{item.Name} - {item.Artist} ({item.DurationText})";
        AccessibleRole = AccessibleRole.PushButton;

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
    }

    internal void SetHighlight(bool value)
    {
        active = value;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;

        using var bg = new SolidBrush(BackColor);
        g.FillRectangle(bg, ClientRectangle);

        // Hover or active background
        if (active || hovered || pressed)
        {
            using var shape = Astra.Round(new RectangleF(2, 2, Width - 4, Height - 4), 8 * s);
            using var fill = new SolidBrush(active ? Astra.ActiveSurface : pressed ? Astra.Input : Color.FromArgb(42, 43, 56));
            g.FillPath(fill, shape);

            if (active)
            {
                using var pen = new Pen(Astra.Accent, 1f);
                g.DrawPath(pen, shape);
            }
        }

        // Cover artwork thumbnail
        int imgSize = (int)(34 * s);
        int imgX = (int)(6 * s);
        int imgY = (int)((Height - imgSize) / 2);
        var imgRect = new Rectangle(imgX, imgY, imgSize, imgSize);
        float imgRadius = 4 * s;

        var coverImg = SpotifyImageCache.GetImage(item.ImageUrl, () =>
        {
            if (!IsDisposed && IsHandleCreated)
            {
                try { BeginInvoke(new Action(Invalidate)); } catch { }
            }
        });

        using var imgClip = Astra.Round(new RectangleF(imgRect.X, imgRect.Y, imgRect.Width, imgRect.Height), imgRadius);
        var saved = g.Save();
        g.SetClip(imgClip);

        if (coverImg is not null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(coverImg, imgRect);
        }
        else
        {
            using var phBrush = new SolidBrush(Astra.Input);
            g.FillRectangle(phBrush, imgRect);
            using var iconFont = new Font("Segoe UI", 9f);
            TextRenderer.DrawText(g, "♪", iconFont, imgRect, Astra.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        g.Restore(saved);

        using var imgBorder = new Pen(Color.FromArgb(40, 255, 255, 255), 1f);
        g.DrawPath(imgBorder, imgClip);

        // Geometry for texts
        int textLeft = imgX + imgSize + (int)(8 * s);
        int durationW = (int)(55 * s);
        int rightPad = (int)(12 * s);
        int textW = Math.Max(20, Width - textLeft - durationW - rightPad - (int)(6 * s));

        // Track title
        using var titleFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        TextRenderer.DrawText(g, item.Name, titleFont,
            new Rectangle(textLeft, (int)(5 * s), textW, (int)(18 * s)),
            hovered || active ? Astra.Text : Color.FromArgb(230, 230, 238),
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // Artist name
        using var artistFont = new Font("Segoe UI", 8f);
        TextRenderer.DrawText(g, item.Artist, artistFont,
            new Rectangle(textLeft, (int)(23 * s), textW, (int)(16 * s)),
            Astra.Muted,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // Duration (guaranteed clear of scrollbar)
        TextRenderer.DrawText(g, item.DurationText, artistFont,
            new Rectangle(Width - durationW - rightPad, 0, durationW, Height),
            Astra.Muted,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }
}
