using System.ComponentModel;

namespace VpnZapretSwitch;

// A pixel-scrolled, virtualized list. Only visible rows are painted; it never
// creates a native ListBox scrollbar and never jumps by a whole server row.
internal sealed class AstraServerList : Control
{
    private IncyServer[] items = [];
    private int selectedIndex = -1, offset, itemHeight = 62;
    private string? pressedId;
    public IReadOnlyList<IncyServer> Items => items;
    public IncyServer? SelectedItem => selectedIndex >= 0 && selectedIndex < items.Length ? items[selectedIndex] : null;
    public int ContentHeight => items.Length * itemHeight;
    public int ScrollOffset => offset;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ItemHeight
    {
        get => itemHeight;
        set { itemHeight = Math.Max(1, value); ExtentChanged?.Invoke(); Invalidate(); }
    }
    public event DrawItemEventHandler? DrawItem;
    public event Action? ExtentChanged;
    public event Action<int>? RevealRequested;
    public event Action<IncyServer>? ServerClicked;
    public AstraServerList()
    {
        BackColor = Astra.Surface; ForeColor = Astra.Text;
        AccessibleName = "Серверы VPN"; AccessibleRole = AccessibleRole.List; TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    }
    public void SetData(IReadOnlyList<IncyServer> values)
    {
        if (items.SequenceEqual(values)) return;
        string? focused = SelectedItem?.Id;
        items = values.ToArray();
        selectedIndex = Array.FindIndex(items, s => s.Id == focused);
        offset = Math.Clamp(offset, 0, Math.Max(0, ContentHeight - ClientSize.Height));
        ExtentChanged?.Invoke(); Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
    }
    public void SetScrollOffset(int value)
    {
        int next = Math.Clamp(value, 0, Math.Max(0, ContentHeight - ClientSize.Height));
        if (offset == next) return;
        offset = next; Invalidate();
    }
    public int IndexFromPoint(Point point)
    {
        if (!ClientRectangle.Contains(point)) return -1;
        int index = (point.Y + offset) / itemHeight;
        return index >= 0 && index < items.Length ? index : -1;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        using var background = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(background, e.ClipRectangle);
        int first = Math.Max(0, (offset + e.ClipRectangle.Top) / itemHeight);
        int last = Math.Min(items.Length, (offset + e.ClipRectangle.Bottom + itemHeight - 1) / itemHeight);
        for (int index = first; index < last; index++)
        {
            var bounds = new Rectangle(0, index * itemHeight - offset, ClientSize.Width, itemHeight);
            var state = index == selectedIndex && Focused ? DrawItemState.Focus : DrawItemState.None;
            var args = new DrawItemEventArgs(e.Graphics, Font, bounds, index, state, ForeColor, BackColor);
            DrawItem?.Invoke(this, args);
        }
        base.OnPaint(e);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            Focus(); int index = IndexFromPoint(e.Location);
            pressedId = index >= 0 ? items[index].Id : null;
            SelectIndex(index, reveal: false);
        }
        base.OnMouseDown(e);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int index = IndexFromPoint(e.Location);
        string? pressed = pressedId; pressedId = null;
        if (e.Button == MouseButtons.Left && index >= 0 && items[index].Id == pressed) ServerClicked?.Invoke(items[index]);
    }
    private void SelectIndex(int index, bool reveal)
    {
        if (index < 0 || index >= items.Length) return;
        selectedIndex = index;
        if (reveal) RevealRequested?.Invoke(index);
        AccessibilityNotifyClients(AccessibleEvents.Focus, index);
        Invalidate();
    }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || items.Length == 0) return;
        int page = Math.Max(1, ClientSize.Height / itemHeight);
        int index = e.KeyCode switch
        {
            Keys.Up => Math.Max(0, selectedIndex - 1), Keys.Down => Math.Min(items.Length - 1, selectedIndex + 1),
            Keys.Home => 0, Keys.End => items.Length - 1,
            Keys.PageUp => Math.Max(0, selectedIndex - page), Keys.PageDown => Math.Min(items.Length - 1, selectedIndex + page),
            _ => -1
        };
        if (index < 0) return;
        SelectIndex(index, reveal: true); e.Handled = true; e.SuppressKeyPress = true;
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override AccessibleObject CreateAccessibilityInstance() => new ListAccessibility(this);
    private sealed class ListAccessibility(AstraServerList owner) : ControlAccessibleObject(owner)
    {
        public override int GetChildCount() => owner.items.Length;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < owner.items.Length ? new RowAccessibility(owner, index, this) : null;
    }
    private sealed class RowAccessibility(AstraServerList owner, int index, AccessibleObject parent) : AccessibleObject
    {
        public override AccessibleObject Parent => parent;
        public override AccessibleRole Role => AccessibleRole.ListItem;
        public override string? Name { get => index < owner.items.Length ? owner.items[index].Name : ""; set { } }
        public override string? Description => index < owner.items.Length ? owner.items[index].Description : "";
        public override AccessibleStates State => AccessibleStates.Selectable | (owner.selectedIndex == index ? AccessibleStates.Focused : 0);
        public override Rectangle Bounds => owner.RectangleToScreen(new Rectangle(0, index * owner.itemHeight - owner.offset, owner.Width, owner.itemHeight));
        public override void Select(AccessibleSelection flags) { owner.Focus(); owner.SelectIndex(index, reveal: true); }
    }
}
