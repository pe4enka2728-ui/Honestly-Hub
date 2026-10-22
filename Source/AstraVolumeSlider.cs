using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace VpnZapretSwitch;

internal sealed class AstraVolumeSlider : Control
{
    private int value;
    private bool dragging, hovered;
    public event Action<int>? ValueRequested;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int Value => value;
    internal AstraVolumeSlider()
    {
        AccessibleName = "Громкость Spotify"; AccessibleRole = AccessibleRole.Slider;
        Cursor = Cursors.Hand; TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    }
    internal void SetValue(int next)
    {
        if (dragging) return; next = Math.Clamp(next, 0, 100);
        if (value != next) { value = next; Invalidate(); if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1); }
    }
    private void Request(int next, bool commit = true)
    {
        value = Math.Clamp(next, 0, 100); Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1); if (commit) ValueRequested?.Invoke(value);
    }
    private int Inset => Math.Max(5, 6 * DeviceDpi / 96);
    private void At(int x) => Request((int)Math.Round((x - Inset) * 100d / Math.Max(1, Width - Inset * 2)), commit: false);
    protected override void OnMouseDown(MouseEventArgs e)
    { base.OnMouseDown(e); if (e.Button != MouseButtons.Left || !Enabled) return; Focus(); dragging = true; Capture = true; At(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) At(e.X); }
    protected override void OnMouseUp(MouseEventArgs e)
    { base.OnMouseUp(e); if (e.Button == MouseButtons.Left && dragging) { At(e.X); dragging = false; Capture = false; ValueRequested?.Invoke(value); } }
    protected override void OnMouseCaptureChanged(EventArgs e)
    { bool commit = dragging; dragging = false; Invalidate(); base.OnMouseCaptureChanged(e); if (commit) ValueRequested?.Invoke(value); }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left: case Keys.Down: Request(value - 2); break;
            case Keys.Right: case Keys.Up: Request(value + 2); break;
            case Keys.Home: Request(0); break; case Keys.End: Request(100); break;
            case Keys.PageUp: Request(value + 10); break; case Keys.PageDown: Request(value - 10); break;
            default: return;
        }
        e.Handled = true; e.SuppressKeyPress = true;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using var background = new SolidBrush(BackColor); g.FillRectangle(background, ClientRectangle);
        if (Width <= Inset * 2 || Height < 2) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float mid = Height / 2f, left = Inset, right = Width - Inset;
        using var track = new Pen(Astra.Input, Math.Max(3, 3 * DeviceDpi / 96f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(track, left, mid, right, mid);
        float thumb = left + (right - left) * value / 100;
        using var fill = new Pen(Enabled ? Astra.Accent : Astra.Border, track.Width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (value > 0) g.DrawLine(fill, left, mid, thumb, mid);
        float radius = (hovered || Focused || dragging ? 5 : 4) * DeviceDpi / 96f;
        using var brush = new SolidBrush(Enabled ? Astra.Accent : Astra.Muted);
        g.FillEllipse(brush, thumb - radius, mid - radius, radius * 2, radius * 2);
    }
    protected override AccessibleObject CreateAccessibilityInstance() => new VolumeAccess(this);
    private sealed class VolumeAccess(AstraVolumeSlider owner) : ControlAccessibleObject(owner)
    {
        public override string? Value
        {
            get => owner.Value + "%";
            set { if (owner.Enabled && int.TryParse(value?.Trim().TrimEnd('%'), out int next)) owner.Request(next); }
        }
    }
}
