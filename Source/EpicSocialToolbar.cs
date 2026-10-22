using System.Drawing;

namespace VpnZapretSwitch;

internal sealed record EpicSocialToolbarTarget(Point Add, Rectangle AddBounds, Rectangle SearchBounds);

// Navigation fallback for Epic's icon-only toolbar. A recognized social caption is
// required; both outlined buttons and their light glyphs must be present together.
// This never locates a player or a friend-request action.
internal static class EpicSocialToolbar
{
    internal static EpicSocialToolbarTarget? Find(Bitmap bitmap, double scale, EpicOcrLine? socialCaption)
    {
        if (socialCaption is null || scale < .75 || scale > 3 ||
            socialCaption.Bounds.Left < bitmap.Width * .60 || socialCaption.Bounds.Bottom > bitmap.Height / 2)
            return null;
        int radius = (int)Math.Round(18 * scale), gap = (int)Math.Round(44 * scale);
        int expectedX = bitmap.Width - (int)Math.Round(118 * scale);
        int minY = socialCaption.Bounds.Bottom + (int)Math.Round(30 * scale);
        var matches = new List<EpicSocialToolbarTarget>();
        for (int half = radius - 1; half <= radius + 1; half++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int x = expectedX + dx;
                for (int y = minY; y < bitmap.Height - half - 5 * scale; y++)
                {
                    if (!Button(bitmap, x, y, half, scale) || !Button(bitmap, x + gap, y, half, scale)) continue;
                    matches.Add(new(new(x, y), new(x - half, y - half, half * 2 + 1, half * 2 + 1),
                        new(x + gap - half, y - half, half * 2 + 1, half * 2 + 1)));
                }
            }
        if (matches.Count == 0) return null;
        // Nearby pixel fits describe the same toolbar. Two separate pairs are ambiguous.
        int firstY = matches.Min(m => m.Add.Y), lastY = matches.Max(m => m.Add.Y);
        if (lastY - firstY > radius) return null;
        double centerY = (firstY + lastY) / 2d;
        return matches.OrderBy(m => Math.Abs(m.Add.Y - centerY)).ThenBy(m => Math.Abs(m.Add.X - expectedX))
            .ThenBy(m => Math.Abs(m.AddBounds.Width - (radius * 2 + 1))).First();
    }

    private static bool Button(Bitmap bitmap, int x, int y, int half, double scale)
    {
        int outside = Math.Max(2, (int)Math.Round(3 * scale));
        if (x - half - outside < 0 || x + half + outside >= bitmap.Width || y - half - outside < 0 || y + half + outside >= bitmap.Height) return false;
        double Light(int px, int py)
        { var c = bitmap.GetPixel(px, py); return .2126 * c.R + .7152 * c.G + .0722 * c.B; }
        double outer = 0, inner = 0; int edges = 0;
        int offset = Math.Max(2, half / 3);
        foreach (int d in new[] { -offset, offset })
        {
            foreach (var edge in new[] { (x + d, y - half, x + d, y - half - outside), (x + d, y + half, x + d, y + half + outside),
                (x - half, y + d, x - half - outside, y + d), (x + half, y + d, x + half + outside, y + d) })
            {
                double background = Light(edge.Item3, edge.Item4); outer += background;
                if (Light(edge.Item1, edge.Item2) - background >= 6) edges++;
            }
        }
        if (edges < 6) return false;
        outer /= 8;
        int inset = half - Math.Max(4, (int)Math.Round(5 * scale));
        foreach (int dx in new[] { -inset, inset }) foreach (int dy in new[] { -inset, inset }) inner += Light(x + dx, y + dy);
        if (inner / 4 - outer < 3) return false;
        int bright = 0, core = Math.Max(4, (int)Math.Round(8 * scale)), step = Math.Max(1, (int)Math.Round(2 * scale));
        for (int dy = -core; dy <= core; dy += step)
            for (int dx = -core; dx <= core; dx += step)
                if (Light(x + dx, y + dy) - outer > 65) bright++;
        return bright >= 3;
    }
}
