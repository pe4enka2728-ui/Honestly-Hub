using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;

namespace VpnZapretSwitch;

internal static class ColorPalette
{
    internal static Color ExtractDominantColor(Image? image)
    {
        if (image is null) return Color.FromArgb(40, 24, 36);

        try
        {
            using var sample = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(sample))
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(image, new Rectangle(0, 0, 32, 32));
            }

            var candidates = new List<(Color color, float score)>();

            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 32; x++)
                {
                    var c = sample.GetPixel(x, y);
                    float s = c.GetSaturation();
                    float b = c.GetBrightness();

                    // Filter out near-blacks, near-whites, and completely unsaturated grays
                    if (b < 0.12f || b > 0.90f || s < 0.16f) continue;

                    // Vibrancy score: prioritize rich, saturated tones
                    float brightnessPenalty = Math.Abs(b - 0.40f) * 1.6f;
                    float score = s * 2.2f + (1.0f - brightnessPenalty);

                    candidates.Add((c, score));
                }
            }

            if (candidates.Count == 0)
            {
                // Grayscale or very dark cover
                long sumR = 0, sumG = 0, sumB = 0;
                for (int y = 0; y < 32; y++)
                    for (int x = 0; x < 32; x++)
                    {
                        var c = sample.GetPixel(x, y);
                        sumR += c.R; sumG += c.G; sumB += c.B;
                    }
                int avgR = (int)(sumR / 1024), avgG = (int)(sumG / 1024), avgB = (int)(sumB / 1024);
                return Color.FromArgb(
                    Math.Clamp(avgR / 2 + 25, 20, 70),
                    Math.Clamp(avgG / 2 + 25, 20, 70),
                    Math.Clamp(avgB / 2 + 35, 30, 85));
            }

            // Group into 12 hue buckets (30 deg each)
            var bestGroup = candidates
                .GroupBy(c => (int)(c.color.GetHue() / 30.0f))
                .OrderByDescending(g => g.Sum(x => x.score))
                .First();

            int r = (int)bestGroup.Average(x => x.color.R);
            int gCol = (int)bestGroup.Average(x => x.color.G);
            int bCol = (int)bestGroup.Average(x => x.color.B);

            var baseColor = Color.FromArgb(r, gCol, bCol);
            float hue = baseColor.GetHue();
            float sat = Math.Max(0.45f, baseColor.GetSaturation());
            float light = Math.Clamp(baseColor.GetBrightness(), 0.22f, 0.38f);

            return FromHsl(hue, sat, light);
        }
        catch
        {
            return Color.FromArgb(42, 28, 44);
        }
    }

    private static Color FromHsl(float h, float s, float l)
    {
        double r, g, b;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = HueToRgb(p, q, h / 360.0 + 1.0 / 3.0);
            g = HueToRgb(p, q, h / 360.0);
            b = HueToRgb(p, q, h / 360.0 - 1.0 / 3.0);
        }
        return Color.FromArgb(
            (int)Math.Round(r * 255),
            (int)Math.Round(g * 255),
            (int)Math.Round(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
        return p;
    }
}

internal sealed class SpotifyFullscreenView : Form
{
    private readonly SpotifyApi api;
    private readonly SpotifyPanel panel;
    private readonly System.Windows.Forms.Timer animTimer = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer syncTimer = new() { Interval = 40 };

    private bool opening;
    private bool closing;
    private DateTime animStartTime;
    private const int AnimDurationMs = 220;

    // Ambient background colors
    private Color currentBgColor = Color.FromArgb(40, 24, 36);
    private Color targetBgColor = Color.FromArgb(40, 24, 36);
    private Image? lastCoverAnalyzed;

    // Playback and progress
    private string currentTrackTitle = "";
    private string currentTrackArtist = "";
    private bool isPlaying;
    private double trackElapsedSec = 45;
    private double trackDurationSec = 180;
    private DateTime lastTickTime = DateTime.UtcNow;

    // Controls
    private readonly AstraButton exitTopBtn = new() { Text = "✕", Kind = ButtonKind.Quiet, Size = new Size(32, 30) };
    private readonly AstraButton collapseTopBtn = new() { Text = "⤡", Kind = ButtonKind.Quiet, Size = new Size(32, 30) };
    private readonly Label contextLabel = Astra.Label("СЕЙЧАС ИГРАЕТ", 9.5f, Color.FromArgb(210, 215, 230), FontStyle.Bold);

    // Bottom bar controls
    private readonly PlayPauseCircleButton playPauseBtn = new();
    private readonly AstraButton prevBtn = new() { Text = "⏮", Kind = ButtonKind.Quiet, Size = new Size(34, 34) };
    private readonly AstraButton nextBtn = new() { Text = "⏭", Kind = ButtonKind.Quiet, Size = new Size(34, 34) };
    private readonly AstraButton shuffleBtn = new() { Text = "🔀", Kind = ButtonKind.Quiet, Size = new Size(32, 32) };
    private readonly AstraButton repeatBtn = new() { Text = "🔁", Kind = ButtonKind.Quiet, Size = new Size(32, 32) };
    private readonly AstraButton queueBtn = new() { Text = "≡", Kind = ButtonKind.Quiet, Size = new Size(32, 32) };
    private readonly AstraButton exitBottomBtn = new() { Text = "⤡", Kind = ButtonKind.Quiet, Size = new Size(32, 32) };
    private readonly AstraVolumeSlider volumeSlider = new();
    private readonly Label volumeIcon = Astra.Label("♪", 10, Astra.Muted);
    private readonly Label volumeLabel = Astra.Label("—", 8.5f, Astra.Muted);
    private readonly FullscreenProgressBar progressBar = new();
    private readonly Label elapsedLabel = Astra.Label("0:00", 8f, Astra.Muted);
    private readonly Label durationLabel = Astra.Label("0:00", 8f, Astra.Muted);

    internal SpotifyFullscreenView(SpotifyApi api, SpotifyPanel panel)
    {
        this.api = api;
        this.panel = panel;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(16, 17, 22);
        ForeColor = Astra.Text;
        Font = new Font("Segoe UI", 10f);
        DoubleBuffered = true;
        KeyPreview = true;

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        // Top actions
        exitTopBtn.Click += (_, _) => CloseAnimated();
        collapseTopBtn.Click += (_, _) => CloseAnimated();
        exitBottomBtn.Click += (_, _) => CloseAnimated();

        // Playback actions
        prevBtn.Click += async (_, _) => await panel.SendCommandAsync(MusicCommand.Previous);
        nextBtn.Click += async (_, _) => await panel.SendCommandAsync(MusicCommand.Next);
        playPauseBtn.Click += async (_, _) => await panel.SendCommandAsync(MusicCommand.TogglePause);

        // Volume slider
        volumeSlider.ValueRequested += percent =>
        {
            panel.SetVolume(percent);
            volumeLabel.Text = percent + "%";
        };

        // Queue button
        queueBtn.Click += (_, _) =>
        {
            panel.ToggleQueue();
        };

        // Progress seek
        progressBar.SeekRequested += fraction =>
        {
            trackElapsedSec = fraction * trackDurationSec;
            UpdateProgressDisplay();
        };

        Controls.Add(contextLabel);
        Controls.Add(collapseTopBtn);
        Controls.Add(exitTopBtn);

        Controls.Add(shuffleBtn);
        Controls.Add(prevBtn);
        Controls.Add(playPauseBtn);
        Controls.Add(nextBtn);
        Controls.Add(repeatBtn);

        Controls.Add(elapsedLabel);
        Controls.Add(progressBar);
        Controls.Add(durationLabel);

        Controls.Add(volumeIcon);
        Controls.Add(volumeSlider);
        Controls.Add(volumeLabel);
        Controls.Add(queueBtn);
        Controls.Add(exitBottomBtn);

        animTimer.Tick += (_, _) => OnAnimTick();
        syncTimer.Tick += (_, _) => OnSyncTick();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            CloseAnimated();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Space)
        {
            _ = panel.SendCommandAsync(MusicCommand.TogglePause);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Left)
        {
            _ = panel.SendCommandAsync(MusicCommand.Previous);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Right)
        {
            _ = panel.SendCommandAsync(MusicCommand.Next);
            e.Handled = true;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
        }
        catch { }
        UpdateRoundedRegion();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRoundedRegion();
        LayoutControls();
    }

    private void UpdateRoundedRegion()
    {
        if (!IsHandleCreated || Width < 20 || Height < 20) return;
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
        Bounds = owner.Bounds;
        UpdateRoundedRegion();
        LayoutControls();

        // Initial sync of track & color
        SyncTrackImmediate();

        opening = true;
        closing = false;
        animStartTime = DateTime.UtcNow;
        Opacity = 0.05;

        Show();
        BringToFront();

        animTimer.Start();
        syncTimer.Start();

        owner.LocationChanged -= OnOwnerMoved;
        owner.LocationChanged += OnOwnerMoved;
        owner.SizeChanged -= OnOwnerResized;
        owner.SizeChanged += OnOwnerResized;
    }

    internal void CloseAnimated()
    {
        if (closing || !Visible) return;
        closing = true;
        opening = false;
        animStartTime = DateTime.UtcNow;
        animTimer.Start();
    }

    private void OnOwnerMoved(object? sender, EventArgs e)
    {
        if (!Visible || Owner is null) return;
        Bounds = Owner.Bounds;
    }

    private void OnOwnerResized(object? sender, EventArgs e)
    {
        if (!Visible || Owner is null) return;
        Bounds = Owner.Bounds;
    }

    private void OnAnimTick()
    {
        double elapsed = (DateTime.UtcNow - animStartTime).TotalMilliseconds;
        double t = Math.Clamp(elapsed / AnimDurationMs, 0.0, 1.0);

        if (opening)
        {
            double eased = 1.0 - Math.Pow(1.0 - t, 3.0);
            Opacity = Math.Clamp(0.1 + 0.9 * eased, 0.0, 1.0);

            if (t >= 1.0)
            {
                animTimer.Stop();
                opening = false;
                Opacity = 1.0;
            }
        }
        else if (closing)
        {
            double eased = Math.Pow(1.0 - t, 3.0);
            Opacity = Math.Clamp(eased, 0.0, 1.0);

            if (t >= 1.0)
            {
                animTimer.Stop();
                closing = false;
                Hide();
                syncTimer.Stop();
            }
        }
    }

    private void SyncTrackImmediate()
    {
        var cur = panel.CurrentTrack;
        var art = panel.CurrentArtwork;

        if (cur is not null)
        {
            currentTrackTitle = cur.Title;
            currentTrackArtist = cur.Artist;
            isPlaying = cur.Playing;
            playPauseBtn.IsPlaying = isPlaying;
        }

        if (art is not null && !ReferenceEquals(art, lastCoverAnalyzed))
        {
            lastCoverAnalyzed = art;
            targetBgColor = ColorPalette.ExtractDominantColor(art);
            currentBgColor = targetBgColor;
        }
    }

    private void OnSyncTick()
    {
        DateTime now = DateTime.UtcNow;
        double deltaSec = (now - lastTickTime).TotalSeconds;
        lastTickTime = now;

        var cur = panel.CurrentTrack;
        var art = panel.CurrentArtwork;

        if (cur is not null)
        {
            if (currentTrackTitle != cur.Title || currentTrackArtist != cur.Artist)
            {
                currentTrackTitle = cur.Title;
                currentTrackArtist = cur.Artist;
                trackElapsedSec = 0;
            }

            isPlaying = cur.Playing;
            playPauseBtn.IsPlaying = isPlaying;
        }

        if (isPlaying)
        {
            trackElapsedSec += deltaSec;
            if (trackElapsedSec > trackDurationSec) trackElapsedSec = trackDurationSec;
        }
        UpdateProgressDisplay();

        // Sync volume
        var vol = panel.VolumeService?.Snapshot;
        if (vol is not null)
        {
            volumeSlider.Enabled = vol.Available;
            volumeSlider.SetValue(vol.Percent);
            volumeLabel.Text = vol.Available ? vol.Percent + "%" : "—";
            volumeIcon.Text = vol.Muted ? "×" : "♪";
        }

        // Ambient background color transition
        if (art is not null && !ReferenceEquals(art, lastCoverAnalyzed))
        {
            lastCoverAnalyzed = art;
            targetBgColor = ColorPalette.ExtractDominantColor(art);
        }

        if (currentBgColor != targetBgColor)
        {
            currentBgColor = BlendColor(currentBgColor, targetBgColor, 0.12f);
            Invalidate();
        }
    }

    private static Color BlendColor(Color from, Color to, float factor)
    {
        int r = (int)(from.R + (to.R - from.R) * factor);
        int g = (int)(from.G + (to.G - from.G) * factor);
        int b = (int)(from.B + (to.B - from.B) * factor);
        return Color.FromArgb(Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
    }

    private void UpdateProgressDisplay()
    {
        int elMin = (int)(trackElapsedSec / 60);
        int elSec = (int)(trackElapsedSec % 60);
        elapsedLabel.Text = $"{elMin}:{elSec:D2}";

        int durMin = (int)(trackDurationSec / 60);
        int durSec = (int)(trackDurationSec % 60);
        durationLabel.Text = $"{durMin}:{durSec:D2}";

        float frac = (float)Math.Clamp(trackElapsedSec / Math.Max(1.0, trackDurationSec), 0.0, 1.0);
        progressBar.Progress = frac;
    }

    private void LayoutControls()
    {
        int D(int n) => (int)Math.Round(n * DeviceDpi / 96d);
        float s = DeviceDpi / 96f;

        // Top bar
        contextLabel.Location = new Point(D(28), D(18));
        exitTopBtn.Location = new Point(Width - D(46), D(14));
        collapseTopBtn.Location = new Point(Width - D(82), D(14));

        // Bottom bar geometry
        int barH = D(76);
        int barY = Height - barH;

        // Center playback controls
        int cx = Width / 2;
        int btnY = barY + D(8);

        playPauseBtn.SetBounds(cx - D(18), btnY, D(36), D(36));
        prevBtn.SetBounds(cx - D(60), btnY + D(2), D(32), D(32));
        nextBtn.SetBounds(cx + D(28), btnY + D(2), D(32), D(32));
        shuffleBtn.SetBounds(cx - D(100), btnY + D(3), D(30), D(30));
        repeatBtn.SetBounds(cx + D(70), btnY + D(3), D(30), D(30));

        // Progress bar row
        int barWidth = Math.Clamp(Width - D(520), D(240), D(540));
        int progX = cx - barWidth / 2;
        int progY = barY + D(50);

        progressBar.SetBounds(progX, progY, barWidth, D(14));
        elapsedLabel.SetBounds(progX - D(40), progY - D(1), D(36), D(16));
        elapsedLabel.TextAlign = ContentAlignment.MiddleRight;
        durationLabel.SetBounds(progX + barWidth + D(6), progY - D(1), D(36), D(16));
        durationLabel.TextAlign = ContentAlignment.MiddleLeft;

        // Right volume controls
        int rightX = Width - D(28);
        exitBottomBtn.SetBounds(rightX - D(30), barY + D(22), D(28), D(28));
        queueBtn.SetBounds(rightX - D(64), barY + D(22), D(28), D(28));

        int volSliderW = D(90);
        volumeLabel.SetBounds(rightX - D(100), barY + D(27), D(30), D(18));
        volumeSlider.SetBounds(rightX - D(100) - volSliderW, barY + D(24), volSliderW, D(24));
        volumeIcon.SetBounds(rightX - D(104) - volSliderW - D(18), barY + D(27), D(18), D(18));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;

        // 1. Dynamic background gradient from dominant color to dark floor
        int bottomH = (int)(76 * s);
        using (var bgGrad = new LinearGradientBrush(
            new Rectangle(0, 0, Width, Height - bottomH + 20),
            currentBgColor,
            Color.FromArgb(14, 15, 20),
            LinearGradientMode.Vertical))
        {
            g.FillRectangle(bgGrad, 0, 0, Width, Height - bottomH + 20);
        }

        // 2. Center Hero Album Artwork & Details
        var coverImg = panel.CurrentArtwork;
        int availH = Height - bottomH - (int)(60 * s);

        // Size cover proportionally
        int coverSize = Math.Clamp(Math.Min(Width - (int)(120 * s), availH - (int)(150 * s)), (int)(220 * s), (int)(380 * s));
        int coverX = (Width - coverSize) / 2;
        int coverY = (int)(54 * s) + Math.Max(0, (availH - (int)(140 * s) - coverSize) / 2);

        // Drop shadow behind cover
        using (var shadowBrush = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
        {
            using var shadowPath = Astra.Round(new RectangleF(coverX - 8 * s, coverY + 4 * s, coverSize + 16 * s, coverSize + 14 * s), 20 * s);
            g.FillPath(shadowBrush, shadowPath);
        }

        // Rounded artwork
        using (var coverClip = Astra.Round(new RectangleF(coverX, coverY, coverSize, coverSize), 16 * s))
        {
            var saved = g.Save();
            g.SetClip(coverClip);

            if (coverImg is not null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(coverImg, new Rectangle(coverX, coverY, coverSize, coverSize));
            }
            else
            {
                using var ph = new SolidBrush(Color.FromArgb(30, 32, 42));
                g.FillRectangle(ph, coverX, coverY, coverSize, coverSize);
                using var noteFont = new Font("Segoe UI", 48f);
                TextRenderer.DrawText(g, "♪", noteFont, new Rectangle(coverX, coverY, coverSize, coverSize), Astra.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            g.Restore(saved);

            using var coverPen = new Pen(Color.FromArgb(40, 255, 255, 255), 1.2f);
            g.DrawPath(coverPen, coverClip);
        }

        // Information below cover
        int textY = coverY + coverSize + (int)(18 * s);
        string trackTitle = string.IsNullOrWhiteSpace(currentTrackTitle) ? "Spotify приостановлен" : currentTrackTitle;
        string trackArtist = string.IsNullOrWhiteSpace(currentTrackArtist) ? "Включите трек в Spotify" : currentTrackArtist;

        using (var titleFont = new Font("Segoe UI", 16f, FontStyle.Bold))
        {
            TextRenderer.DrawText(g, trackTitle, titleFont,
                new Rectangle((int)(30 * s), textY, Width - (int)(60 * s), (int)(30 * s)),
                Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        using (var artistFont = new Font("Segoe UI", 11f))
        {
            TextRenderer.DrawText(g, trackArtist, artistFont,
                new Rectangle((int)(30 * s), textY + (int)(32 * s), Width - (int)(60 * s), (int)(24 * s)),
                Color.FromArgb(200, 205, 220),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        // Two Spotify-styled info cards if height allows
        int cardY = textY + (int)(64 * s);
        int cardH = Height - bottomH - cardY - (int)(12 * s);
        if (cardH >= (int)(60 * s) && Width >= (int)(500 * s))
        {
            int cardW = Math.Min((int)(260 * s), (Width - (int)(80 * s)) / 2);
            int leftCardX = Width / 2 - cardW - (int)(10 * s);
            int rightCardX = Width / 2 + (int)(10 * s);

            DrawInfoCard(g, leftCardX, cardY, cardW, cardH, "Об исполнителе", trackArtist, "Исполнитель Spotify", s);
            DrawInfoCard(g, rightCardX, cardY, cardW, cardH, "Сведения", trackTitle, "Качество звука: Высокое", s);
        }

        // 3. Bottom Player Bar
        int barY = Height - bottomH;
        using (var barBg = new SolidBrush(Color.FromArgb(245, 14, 15, 20)))
        {
            g.FillRectangle(barBg, 0, barY, Width, bottomH);
        }
        using (var barBorder = new Pen(Color.FromArgb(35, 255, 255, 255), 1f))
        {
            g.DrawLine(barBorder, 0, barY, Width, barY);
        }

        // Mini track card in bottom left
        int thumbSize = (int)(44 * s);
        int thumbX = (int)(24 * s);
        int thumbY = barY + (bottomH - thumbSize) / 2;

        using (var thumbClip = Astra.Round(new RectangleF(thumbX, thumbY, thumbSize, thumbSize), 6 * s))
        {
            var saved = g.Save();
            g.SetClip(thumbClip);
            if (coverImg is not null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(coverImg, new Rectangle(thumbX, thumbY, thumbSize, thumbSize));
            }
            else
            {
                using var ph = new SolidBrush(Color.FromArgb(28, 30, 40));
                g.FillRectangle(ph, thumbX, thumbY, thumbSize, thumbSize);
            }
            g.Restore(saved);
        }

        int miniTextLeft = thumbX + thumbSize + (int)(12 * s);
        int miniTextW = Math.Max(10, Width / 2 - miniTextLeft - (int)(180 * s));

        using (var miniTitleFont = new Font("Segoe UI", 9.5f, FontStyle.Bold))
        {
            TextRenderer.DrawText(g, trackTitle, miniTitleFont,
                new Rectangle(miniTextLeft, barY + (int)(16 * s), miniTextW, (int)(20 * s)),
                Astra.Text,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        using (var miniArtistFont = new Font("Segoe UI", 8.5f))
        {
            TextRenderer.DrawText(g, trackArtist, miniArtistFont,
                new Rectangle(miniTextLeft, barY + (int)(38 * s), miniTextW, (int)(18 * s)),
                Astra.Muted,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        // Green saved checkmark indicator next to title
        int checkX = miniTextLeft + Math.Min(miniTextW, (int)g.MeasureString(trackTitle, new Font("Segoe UI", 9.5f, FontStyle.Bold)).Width) + (int)(6 * s);
        if (checkX < Width / 2 - (int)(170 * s))
        {
            using var checkFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            TextRenderer.DrawText(g, "✔", checkFont,
                new Rectangle(checkX, barY + (int)(17 * s), (int)(16 * s), (int)(18 * s)),
                Astra.Green,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }

    private static void DrawInfoCard(Graphics g, int x, int y, int w, int h, string heading, string primary, string secondary, float s)
    {
        using var cardShape = Astra.Round(new RectangleF(x, y, w, h), 12 * s);
        using var cardFill = new SolidBrush(Color.FromArgb(180, 22, 23, 30));
        g.FillPath(cardFill, cardShape);

        using var cardBorder = new Pen(Color.FromArgb(30, 255, 255, 255), 1f);
        g.DrawPath(cardBorder, cardShape);

        int pad = (int)(14 * s);
        using var headFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        TextRenderer.DrawText(g, heading, headFont,
            new Rectangle(x + pad, y + (int)(10 * s), w - pad * 2, (int)(16 * s)),
            Astra.Accent,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        using var primFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        TextRenderer.DrawText(g, primary, primFont,
            new Rectangle(x + pad, y + (int)(28 * s), w - pad * 2, (int)(18 * s)),
            Color.White,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        using var secFont = new Font("Segoe UI", 8f);
        TextRenderer.DrawText(g, secondary, secFont,
            new Rectangle(x + pad, y + (int)(48 * s), w - pad * 2, (int)(16 * s)),
            Astra.Muted,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            animTimer.Dispose();
            syncTimer.Dispose();
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

internal sealed class PlayPauseCircleButton : Control
{
    private bool isPlaying;
    private bool hovered;
    private bool pressed;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool IsPlaying
    {
        get => isPlaying;
        set { if (isPlaying != value) { isPlaying = value; Invalidate(); } }
    }

    internal PlayPauseCircleButton()
    {
        Cursor = Cursors.Hand;
        Size = new Size(36, 36);
        DoubleBuffered = true;
        AccessibleName = "Воспроизведение / Пауза";
        AccessibleRole = AccessibleRole.PushButton;

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float s = DeviceDpi / 96f;
        float d = Math.Min(Width, Height) - 2;
        float cx = Width / 2f;
        float cy = Height / 2f;

        // White circular button
        Color circleColor = pressed ? Color.FromArgb(210, 210, 215) : hovered ? Color.White : Color.FromArgb(240, 240, 245);
        using var brush = new SolidBrush(circleColor);
        g.FillEllipse(brush, cx - d / 2, cy - d / 2, d, d);

        // Icon
        using var iconBrush = new SolidBrush(Color.Black);
        if (isPlaying)
        {
            // Pause bars
            float barW = 3.5f * s;
            float barH = 12 * s;
            float gap = 3.5f * s;
            g.FillRectangle(iconBrush, cx - gap / 2 - barW, cy - barH / 2, barW, barH);
            g.FillRectangle(iconBrush, cx + gap / 2, cy - barH / 2, barW, barH);
        }
        else
        {
            // Play triangle
            float pw = 12 * s;
            float ph = 13 * s;
            PointF[] tri = new PointF[]
            {
                new(cx - pw / 3 + 1, cy - ph / 2),
                new(cx + pw * 2 / 3, cy),
                new(cx - pw / 3 + 1, cy + ph / 2)
            };
            g.FillPolygon(iconBrush, tri);
        }
    }
}

internal sealed class FullscreenProgressBar : Control
{
    private float progress;
    private bool dragging;
    private bool hovered;

    internal event Action<float>? SeekRequested;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal float Progress
    {
        get => progress;
        set { if (Math.Abs(progress - value) > 0.001f && !dragging) { progress = Math.Clamp(value, 0f, 1f); Invalidate(); } }
    }

    internal FullscreenProgressBar()
    {
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            dragging = true;
            UpdateFromMouse(e.X);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging)
        {
            UpdateFromMouse(e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (dragging)
        {
            dragging = false;
            UpdateFromMouse(e.X);
            SeekRequested?.Invoke(progress);
        }
    }

    private void UpdateFromMouse(int mouseX)
    {
        progress = Math.Clamp(mouseX / (float)Width, 0f, 1f);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float s = DeviceDpi / 96f;
        float trackH = 4 * s;
        float y = (Height - trackH) / 2f;

        // Background track
        using var bgBrush = new SolidBrush(Color.FromArgb(70, 255, 255, 255));
        using var bgPath = Astra.Round(new RectangleF(0, y, Width, trackH), trackH / 2);
        g.FillPath(bgBrush, bgPath);

        // Active progress fill
        float fillW = Width * progress;
        if (fillW > 2)
        {
            Color fillColor = hovered || dragging ? Astra.Accent : Color.White;
            using var fillBrush = new SolidBrush(fillColor);
            using var fillPath = Astra.Round(new RectangleF(0, y, fillW, trackH), trackH / 2);
            g.FillPath(fillBrush, fillPath);
        }

        // Thumb on hover or drag
        if (hovered || dragging)
        {
            float thumbR = 6 * s;
            using var thumbBrush = new SolidBrush(Color.White);
            g.FillEllipse(thumbBrush, fillW - thumbR, Height / 2f - thumbR, thumbR * 2, thumbR * 2);
        }
    }
}
