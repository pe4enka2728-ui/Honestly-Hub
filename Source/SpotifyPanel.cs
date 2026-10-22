using System.Drawing.Drawing2D;
using System.IO;

namespace VpnZapretSwitch;

internal sealed class SpotifyPanel : Panel
{
    private readonly MediaIconButton previous = new(MediaIcon.Previous, "Предыдущий трек в Spotify");
    private readonly MediaIconButton pause = new(MediaIcon.Play, "Воспроизвести Spotify");
    private readonly MediaIconButton next = new(MediaIcon.Next, "Следующий трек в Spotify");
    private readonly AlbumCover cover = new();
    private readonly MusicWave wave = new();
    private readonly AstraVolumeSlider volume = new();
    private readonly AstraButton connection = new() { Text = "Связать Spotify", Kind = ButtonKind.Quiet };
    private readonly SpotifyApi api = new();
    private SpotifyConnectionForm? connectionForm;
    private readonly Label volumeIcon = Astra.Label("♪", 11, Astra.Muted);
    private readonly Label volumeLabel = Astra.Label("—", 8, Astra.Muted);
    private readonly Label title = Astra.Label("Spotify", 9, Astra.Text);
    private readonly Label artist = Astra.Label("Откройте трек", 8, Astra.Muted);
    private readonly ToolTip hints = new();
    private readonly System.Windows.Forms.Timer tick = new() { Interval = 33 };
    private SpotifyMedia? media;
    private AudioSpectrum? audio;
    private SpotifyVolume? spotifyVolume;
    private MusicSnapshot? displayed;
    private byte[]? artBytes;
    private bool showConnectionButton;
    private string audioNote = "";
    private string volumeNote = "";
    private bool commanding, released;
    internal int DesiredHeight => api.Connection.Linked ? 196 : 232;
    internal SpotifyPanel()
    {
        BackColor = Astra.Sidebar; DoubleBuffered = true; Margin = Padding.Empty;
        AccessibleName = "Spotify и визуализация звука";
        foreach (var button in new[] { previous, pause, next })
        {
            button.BackColor = BackColor; button.Enabled = false; Controls.Add(button);
            hints.SetToolTip(button, button.AccessibleName);
        }
        foreach (var text in new[] { title, artist })
        { text.AutoSize = false; text.AutoEllipsis = true; text.BackColor = BackColor; Controls.Add(text); }
        cover.BackColor = BackColor; wave.BackColor = BackColor;
        cover.Click += (_, _) => ToggleQueue();
        hints.SetToolTip(cover, "Очередь треков Spotify (нажмите, чтобы открыть/закрыть)");
        Controls.Add(cover); Controls.Add(wave);
        volume.BackColor = BackColor; volume.Enabled = false;
        volumeIcon.AutoSize = false; volumeIcon.TextAlign = ContentAlignment.MiddleLeft; volumeIcon.BackColor = BackColor;
        volumeLabel.AutoSize = false; volumeLabel.TextAlign = ContentAlignment.MiddleRight; volumeLabel.BackColor = BackColor;
        Controls.Add(volumeIcon); Controls.Add(volume); Controls.Add(volumeLabel);
        hints.SetToolTip(volume, "Громкость внутри Spotify · синхронизация в обе стороны");
        hints.SetToolTip(volumeIcon, "Громкость Spotify");
        volume.ValueRequested += percent => { spotifyVolume?.SetVolume(percent); volumeLabel.Text = percent + "%"; };
        showConnectionButton = !api.Connection.Linked;
        connection.Visible = showConnectionButton;
        Controls.Add(connection); connection.Click += (_, _) => { if (FindForm() is { } owner) OpenConnection(owner); };
        previous.Click += async (_, _) => await SendAsync(MusicCommand.Previous);
        pause.Click += async (_, _) => await SendAsync(MusicCommand.TogglePause);
        next.Click += async (_, _) => await SendAsync(MusicCommand.Next);
        tick.Tick += (_, _) => UpdateMusic();
    }
    internal void Start()
    {
        if (released || media is not null) return;
        media = new SpotifyMedia(); audio = new AudioSpectrum(); spotifyVolume = new SpotifyVolume(api); tick.Start();
    }
    internal void OpenConnection(Form owner)
    {
        if (connectionForm is null || connectionForm.IsDisposed) connectionForm = new SpotifyConnectionForm(api);
        if (!connectionForm.Visible) connectionForm.Show(owner);
        connectionForm.Activate();
    }
    private SpotifyQueueFlyout? queueFlyout;
    internal void ToggleQueue()
    {
        if (queueFlyout is not null && queueFlyout.IsOpen)
        {
            queueFlyout.CloseAnimated();
            cover.Active = false;
        }
        else
        {
            var owner = FindForm();
            if (owner is null) return;
            if (queueFlyout is null || queueFlyout.IsDisposed)
            {
                queueFlyout = new SpotifyQueueFlyout(api, this);
            }
            cover.Active = true;
            queueFlyout.OpenAnimated(owner);
        }
    }
    internal void SetQueueActive(bool active)
    {
        cover.Active = active;
    }
    internal Bitmap? CurrentArtwork => cover.CurrentImage;
    private SpotifyFullscreenView? fullscreenView;
    internal void OpenFullscreenView(Form? owner = null)
    {
        owner ??= FindForm();
        if (owner is null) return;
        if (fullscreenView is null || fullscreenView.IsDisposed)
        {
            fullscreenView = new SpotifyFullscreenView(api, this);
        }
        fullscreenView.OpenAnimated(owner);
    }
    internal SpotifyApi Api => api;
    internal MusicSnapshot? CurrentTrack => displayed;
    internal SpotifyVolume? VolumeService => spotifyVolume;
    internal Task SendCommandAsync(MusicCommand command) => SendAsync(command);
    internal void SetVolume(int percent) => spotifyVolume?.SetVolume(percent);
    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        UpdateLayoutHeight();
    }
    private void UpdateLayoutHeight()
    {
        if (Parent is TableLayoutPanel tlp)
        {
            var pos = tlp.GetPositionFromControl(this);
            if (pos.Row >= 0 && pos.Row < tlp.RowStyles.Count)
            {
                float target = (float)Math.Round(DesiredHeight * DeviceDpi / 96.0);
                if (Math.Abs(tlp.RowStyles[pos.Row].Height - target) > 0.5f)
                {
                    tlp.RowStyles[pos.Row].Height = target;
                    tlp.MinimumSize = new Size(0, DesiredHeight == 232 ? 688 : 652);
                    tlp.PerformLayout();
                }
            }
        }
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (previous is null) return;
        int D(int n) => (int)Math.Round(n * DeviceDpi / 96d);
        int inset = D(4), width = Math.Max(1, ClientSize.Width - inset * 2), cell = width / 3;
        previous.SetBounds(inset, D(2), cell, D(34));
        pause.SetBounds(inset + cell, D(2), cell, D(34));
        next.SetBounds(inset + 2 * cell, D(2), width - 2 * cell, D(34));
        cover.SetBounds(inset, D(46), D(46), D(46));
        wave.SetBounds(inset + D(56), D(47), Math.Max(1, width - D(56)), D(44));
        title.SetBounds(inset, D(104), width, D(21));
        artist.SetBounds(inset, D(127), width, D(20));
        volumeIcon.SetBounds(inset, D(160), D(19), D(26));
        volume.SetBounds(inset + D(20), D(160), Math.Max(1, width - D(55)), D(26));
        volumeLabel.SetBounds(inset + width - D(34), D(160), D(34), D(26));
        if (showConnectionButton) connection.SetBounds(inset, D(191), width, D(29));
    }
    private void UpdateMusic()
    {
        if (released || media is null || audio is null) return;
        var data = media.Snapshot;
        if (!ReferenceEquals(displayed, data))
        {
            displayed = data;
            title.Text = data.Title;
            artist.Text = string.IsNullOrWhiteSpace(data.Artist) ? (data.Present ? "Spotify" : data.Note) : data.Artist;
            title.AccessibleName = "Текущий трек Spotify: " + data.Title;
            hints.SetToolTip(title, data.Title); hints.SetToolTip(artist, artist.Text);
            pause.Icon = data.Playing ? MediaIcon.Pause : MediaIcon.Play;
            pause.AccessibleName = data.Playing ? "Пауза Spotify" : "Воспроизвести Spotify";
            hints.SetToolTip(pause, pause.AccessibleName);
            if (!ReferenceEquals(artBytes, data.Artwork))
            {
                artBytes = data.Artwork; Bitmap? artwork = null;
                if (artBytes is not null)
                {
                    try
                    {
                        using var input = new MemoryStream(artBytes, writable: false);
                        using var image = Image.FromStream(input);
                        if (image.Width <= 4096 && image.Height <= 4096)
                        {
                            artwork = new Bitmap(144, 144);
                            using var g = Graphics.FromImage(artwork);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(image, new Rectangle(0, 0, 144, 144));
                        }
                    }
                    catch { artwork?.Dispose(); artwork = null; }
                }
                cover.SetImage(artwork);
            }
            cover.AccessibleName = data.Present ? "Обложка текущего трека Spotify" : "Обложка отсутствует";
            SetButtons();
        }
        if (audioNote != audio.Note) { audioNote = audio.Note; hints.SetToolTip(wave, audioNote); }
        wave.UpdateLevels(audio.ReadLevels());
        var output = spotifyVolume!.Snapshot;
        bool linked = api.Connection.Linked;
        bool shouldShow = !linked;
        if (showConnectionButton != shouldShow)
        {
            showConnectionButton = shouldShow;
            connection.Visible = shouldShow;
            UpdateLayoutHeight();
        }
        if (shouldShow && connection.Text != "Связать Spotify")
        {
            connection.Text = "Связать Spotify";
            connection.Invalidate();
        }
        if (volumeNote != output.Note)
        {
            volumeNote = output.Note; hints.SetToolTip(volume, volumeNote); hints.SetToolTip(volumeIcon, volumeNote);
            volume.AccessibleDescription = volumeNote;
        }
        volume.Enabled = output.Available;
        volume.SetValue(output.Percent);
        volumeLabel.Text = output.Available ? volume.Value + "%" : "—";
        volumeIcon.Text = output.Muted ? "×" : "♪";
    }
    private void SetButtons()
    {
        previous.Enabled = !commanding && displayed?.Previous == true;
        pause.Enabled = !commanding && displayed?.TogglePause == true;
        next.Enabled = !commanding && displayed?.Next == true;
    }
    private async Task SendAsync(MusicCommand command)
    {
        if (commanding || released || media is null) return;
        commanding = true; SetButtons();
        try
        {
            bool accepted = await media.SendAsync(command);
            if (!released && !accepted) hints.Show("Spotify не принял команду", this, new Point(0, 0), 1800);
        }
        finally
        {
            commanding = false;
            if (!released) { UpdateMusic(); SetButtons(); }
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !released)
        {
            released = true; tick.Dispose(); queueFlyout?.Dispose(); fullscreenView?.Dispose(); connectionForm?.Close(); media?.Dispose(); audio?.Dispose(); spotifyVolume?.Dispose(); api.Dispose(); hints.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal enum MediaIcon { Previous, Play, Pause, Next }
internal sealed class MediaIconButton : Control, IButtonControl
{
    private MediaIcon icon;
    private bool hovered, pressed;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal MediaIcon Icon { get => icon; set { icon = value; Invalidate(); } }
    [System.ComponentModel.DefaultValue(DialogResult.None)]
    public DialogResult DialogResult { get; set; }
    internal MediaIconButton(MediaIcon icon, string name)
    {
        this.icon = icon; AccessibleName = name; AccessibleRole = AccessibleRole.PushButton;
        Cursor = Cursors.Hand; TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.ResizeRedraw, true);
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
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { pressed = true; Invalidate(); e.Handled = true; e.SuppressKeyPress = true; }
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        { bool click = pressed; pressed = false; if (click) PerformClick(); Invalidate(); e.Handled = true; }
        base.OnKeyUp(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; using var background = new SolidBrush(BackColor); g.FillRectangle(background, ClientRectangle); g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f, x = Width / 2f, y = Height / 2f;
        Color ink = !Enabled ? Color.FromArgb(70, 72, 86) : pressed ? Astra.Text : hovered || Focused ? Astra.Accent : Astra.Muted;
        using var brush = new SolidBrush(ink);
        if (icon == MediaIcon.Pause)
        {
            g.FillRectangle(brush, x - 5 * s, y - 6 * s, 3 * s, 12 * s);
            g.FillRectangle(brush, x + 2 * s, y - 6 * s, 3 * s, 12 * s);
        }
        else if (icon == MediaIcon.Play)
            g.FillPolygon(brush, new PointF[] { new(x - 4 * s, y - 6.5f * s), new(x + 6 * s, y), new(x - 4 * s, y + 6.5f * s) });
        else
        {
            float direction = icon == MediaIcon.Next ? 1 : -1;
            g.FillPolygon(brush, new PointF[] { new(x - direction * 5 * s, y - 6 * s), new(x + direction * 4 * s, y), new(x - direction * 5 * s, y + 6 * s) });
            g.FillRectangle(brush, x + (direction > 0 ? 5 : -7) * s, y - 6 * s, 2 * s, 12 * s);
        }
    }
    protected override AccessibleObject CreateAccessibilityInstance() => new MusicButtonAccess(this);
    private sealed class MusicButtonAccess(MediaIconButton owner) : ControlAccessibleObject(owner)
    {
        public override string DefaultAction => "Нажать";
        public override void DoDefaultAction() => owner.PerformClick();
    }
}

internal sealed class AlbumCover : Control
{
    private Bitmap? image;
    private bool hovered;
    private bool active;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Active
    {
        get => active;
        set { active = value; Invalidate(); }
    }

    internal Bitmap? CurrentImage => image;

    internal AlbumCover()
    {
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    internal void SetImage(Bitmap? value) { var old = image; image = value; old?.Dispose(); Invalidate(); }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using var background = new SolidBrush(BackColor);
        g.FillRectangle(background, ClientRectangle);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float s = DeviceDpi / 96f;
        using var shape = Astra.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 7 * s);
        var saved = g.Save();
        g.SetClip(shape);

        if (image is not null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, ClientRectangle);
        }
        else
        {
            using var fill = new SolidBrush(Astra.Input);
            g.FillRectangle(fill, ClientRectangle);
            using var pen = new Pen(Astra.Muted, Math.Max(1, s));
            float sc = Width / 46f;
            g.DrawLine(pen, 20 * sc, 15 * sc, 20 * sc, 31 * sc);
            g.DrawLine(pen, 20 * sc, 15 * sc, 30 * sc, 12 * sc);
            g.DrawLine(pen, 30 * sc, 12 * sc, 30 * sc, 28 * sc);
            using var ink = new SolidBrush(Astra.Muted);
            g.FillEllipse(ink, 13 * sc, 28 * sc, 8 * sc, 5 * sc);
            g.FillEllipse(ink, 23 * sc, 25 * sc, 8 * sc, 5 * sc);
        }

        // Overlay on hover or active (queue icon overlay)
        if (hovered || active)
        {
            using var scrim = new SolidBrush(Color.FromArgb(active ? 160 : 120, 18, 17, 26));
            g.FillRectangle(scrim, ClientRectangle);

            using var iconPen = new Pen(active ? Astra.Accent : Color.White, 1.8f * s);
            float cx = Width / 2f, cy = Height / 2f;
            g.DrawLine(iconPen, cx - 8 * s, cy - 6 * s, cx + 8 * s, cy - 6 * s);
            g.DrawLine(iconPen, cx - 8 * s, cy, cx + 8 * s, cy);
            g.DrawLine(iconPen, cx - 8 * s, cy + 6 * s, cx + 3 * s, cy + 6 * s);

            using var playBrush = new SolidBrush(active ? Astra.Accent : Color.White);
            g.FillPolygon(playBrush, new PointF[] { new(cx + 4 * s, cy + 3.5f * s), new(cx + 9 * s, cy + 6 * s), new(cx + 4 * s, cy + 8.5f * s) });
        }

        g.Restore(saved);

        if (active)
        {
            using var borderPen = new Pen(Astra.Accent, 1.8f);
            g.DrawPath(borderPen, shape);
        }
        else if (hovered)
        {
            using var borderPen = new Pen(Astra.Muted, 1f);
            g.DrawPath(borderPen, shape);
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) { image?.Dispose(); image = null; } base.Dispose(disposing); }
}

internal sealed class MusicWave : Control
{
    private readonly float[] smooth = new float[AudioSpectrum.Bands];
    internal MusicWave()
    {
        AccessibleName = "Волна звука устройства вывода Windows";
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    internal void UpdateLevels(float[] levels)
    {
        bool changed = false;
        for (int i = 0; i < smooth.Length; i++)
        {
            float target = levels[i], rate = target > smooth[i] ? .6f : .16f;
            float next = smooth[i] + (target - smooth[i]) * rate;
            if (next < .002f) next = 0;
            changed |= Math.Abs(next - smooth[i]) > .001f; smooth[i] = next;
        }
        if (changed) Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; using var background = new SolidBrush(BackColor); g.FillRectangle(background, ClientRectangle); g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Width < 2 || Height < 2) return;
        float cell = Width / (float)smooth.Length, width = Math.Max(1, cell * .55f), center = Height / 2f;
        using var gradient = new LinearGradientBrush(ClientRectangle, Astra.Accent, Color.FromArgb(203, 94, 119), LinearGradientMode.Horizontal);
        for (int i = 0; i < smooth.Length; i++)
        {
            float height = Math.Max(2 * DeviceDpi / 96f, smooth[i] * (Height - 4));
            using var shape = Astra.Round(new RectangleF(i * cell + (cell - width) / 2, center - height / 2, width, height), width / 2);
            g.FillPath(gradient, shape);
        }
    }
}
