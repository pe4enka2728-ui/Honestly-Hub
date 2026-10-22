using System.Drawing;
using System.Text.RegularExpressions;

namespace VpnZapretSwitch;

internal sealed record EpicFriendNameRegion(Rectangle Bounds, Rectangle RowBounds);

// Geometry and whole-line agreement are independent of the requested nickname.
// Never substitute lookalike letters/digits or extract a nickname from a longer name.
internal static class EpicFriendNameVision
{
    internal static int ResultTop(IReadOnlyList<EpicOcrLine> lines, EpicOcrLine header)
    {
        var captions = lines.Where(l => l.Bounds.Left >= header.Bounds.Left-header.Bounds.Height*4 &&
            l.Bounds.Top > header.Bounds.Bottom+header.Bounds.Height*2 &&
            Regex.IsMatch(EpicFriendVision.Words(l.Text), @"^(найден[оы]?\s+[0-9]+\s+игрок(?:а|ов)?|found\s+[0-9]+\s+(?:players?|results?)|[0-9]+\s+(?:players?|results?)\s+found)$",RegexOptions.CultureInvariant))
            .ToArray();
        return captions.Length == 0 ? header.Bounds.Bottom+header.Bounds.Height*5 : captions.Min(l=>l.Bounds.Bottom);
    }
    internal static EpicFriendNameRegion[] Regions(IReadOnlyList<EpicOcrLine> lines, EpicOcrLine header, Size frame, double scale)
    {
        if(scale is < .75 or > 3) return [];
        int left=header.Bounds.Left-(int)Math.Round(6*scale),right=frame.Width-(int)Math.Round(100*scale);
        if(left<0 || right-left<40*scale) return [];
        int top=ResultTop(lines,header);
        var seeds=new List<EpicOcrLine>();
        foreach(var line in lines.Where(l=>l.Bounds.Top>top && l.Bounds.Bottom<frame.Height-5*scale &&
            l.Bounds.Left>frame.Width*.60 && l.Bounds.Left>left+5*scale && l.Bounds.Right<right-5*scale).OrderBy(l=>l.Bounds.Top))
        {
            if(seeds.Any(old=>Math.Abs(EpicFriendVision.Center(old.Bounds).Y-EpicFriendVision.Center(line.Bounds).Y)<Math.Max(8*scale,Math.Min(old.Bounds.Height,line.Bounds.Height)*.75))) continue;
            seeds.Add(line);
        }
        return seeds.Take(32).Select(line=>new EpicFriendNameRegion(new Rectangle(left,line.Bounds.Top-(int)Math.Round(11*scale),right-left,
            Math.Max((int)Math.Round(40*scale),line.Bounds.Height+(int)Math.Round(24*scale))),line.Bounds))
            .Where(r=>r.Bounds.Top>top && r.Bounds.Bottom<=frame.Height).ToArray();
    }
    internal static EpicOcrLine? Consensus(EpicOcrLine[] original, EpicOcrLine[] contrast, Rectangle region, double scale)
    {
        if(original.Length!=1 || contrast.Length!=1) return null;
        var a=original[0];var b=contrast[0];
        if(!EpicFriendNames.Exact(a.Text,b.Text) || a.Text.Trim().Length is < 1 or > 64 ||
            a.Text.Any(char.IsControl) || !EpicFriendVision.SamePlace(a.Bounds,b.Bounds)) return null;
        bool CompleteWidth(Rectangle r)=>r.Left>region.Left+5*scale && r.Right<region.Right-8*scale;
        if(!CompleteWidth(a.Bounds) || !CompleteWidth(b.Bounds) || a.Bounds.Top<=region.Top+2*scale ||
            a.Bounds.Bottom>=region.Bottom-2*scale || b.Bounds.Top<region.Top || b.Bounds.Bottom>region.Bottom) return null;
        // The inverted pass can return taller OCR boxes. Use the original pixels' box
        // for the row/button coordinates; the second pass confirms the full text only.
        return a with {Text=a.Text.Trim()};
    }
    internal static void ReplaceRow(List<EpicOcrLine> lines, Rectangle region, EpicOcrLine? confirmed)
    {
        // An unconfirmed row must not fall back to the broad OCR spelling (for example O
        // instead of 0). The typed search query remains outside this result region.
        lines.RemoveAll(l=>l.Bounds.IntersectsWith(region) && EpicFriendVision.Center(l.Bounds).Y>region.Top &&
            EpicFriendVision.Center(l.Bounds).Y<region.Bottom);
        if(confirmed is not null) lines.Add(confirmed);
    }
}
