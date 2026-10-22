namespace VpnZapretSwitch;

internal sealed class ZapretInfoPanel : AstraPanel
{
    private ZapretRuntimeStatus? data;
    public void SetData(ZapretRuntimeStatus value) { data = value; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float scale = DeviceDpi / 96f;
        using var font = new Font("Segoe UI", 8.5f);
        string Text(string prefix, bool? running) => prefix + (running is true ? " is running." : running is false ? " is NOT running." : " status is unknown.");
        string[] lines = [Text("\"zapret\" service", data?.ZapretService), Text("\"WinDivert\" service", data?.WinDivertService), Text("Bypass (winws.exe)", data?.BypassProcess)];
        for (int i = 0; i < lines.Length; i++)
        {
            var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
            var color = i < 2 ? Astra.Muted : data?.BypassProcess is true ? Astra.Green : data?.BypassProcess is false ? Astra.Red : Astra.Amber;
            TextRenderer.DrawText(e.Graphics, lines[i], font, new Rectangle((int)(12 * scale), (int)((9 + i * 26) * scale), Width - (int)(24 * scale), (int)(23 * scale)), color, flags);
        }
        AccessibleName = string.Join(" ", lines);
    }
}
