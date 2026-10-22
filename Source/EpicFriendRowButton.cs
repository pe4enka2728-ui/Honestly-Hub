using System.Drawing;

namespace VpnZapretSwitch;

internal sealed record EpicFriendSendTarget(EpicOcrLine Name, Rectangle Action, EpicFriendStatus? ExistingStatus, bool InRow);

internal static class EpicFriendRowButton
{
    // Only the 19x19 add-person glyph from the supplied Epic screenshot, with its
    // background subtracted. No account name, avatar or other user data is stored.
    private static readonly byte[] AddGlyph = Convert.FromBase64String(
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAJF1dJAAAAAAAAAAAAAAAAAAAQ/n///lCAAAAAAAAAAAAAAAAAOf/////5gAAAAAAAAAAAAAAAAD6//////oAAAAAAAAAAAAAAAAA8P/////vAAAAAAAAAAAAAAAAAJb/////kwAAAAAAAAAAAAAAAAABacDAaAEAAAAAAAAAAAAAAAAAAAAAABB8xcV8EAAAAAAAAAAAKmhzURDR//f3/9EQAAAAAAAAFfz//2Z8//+Mif//ewAAAAAAAJL///84xfeGRESG98QAAAAAAAL3////OMX3hUREhvjEAAAAAAAX/////2Z8//+MjP//ewAAAAAACXBzc3NRENH/9/j/zw8AAAAAAAAAAAAAAAAQe8TEew8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==");

    internal static Rectangle? Find(Bitmap bitmap, double scale, EpicOcrLine? exactResult)
    {
        if (exactResult is null || scale < .75 || scale > 3 || exactResult.Bounds.Left < bitmap.Width * .60) return null;
        int x = bitmap.Width - (int)Math.Round(74 * scale), y = EpicFriendVision.Center(exactResult.Bounds).Y;
        int half = (int)Math.Round(17 * scale);
        if (exactResult.Bounds.Right >= x - half || x + half >= bitmap.Width || y - half < 0 || y + half >= bitmap.Height) return null;
        double best = double.MaxValue; Point? found = null;
        for (int dy = -3; dy <= 3; dy++) for (int dx = -2; dx <= 2; dx++)
        {
            var point = new Point(x + dx, y + dy);
            double score = Score(bitmap, point, scale);
            if (score < best) { best = score; found = point; }
        }
        return best < 22 && found is { } center ? new(center.X - half, center.Y - half, half * 2 + 1, half * 2 + 1) : null;
    }

    // Epic replaces Add with a green check and a separate Cancel (X) button.
    // Recognize both in the exact player's row; neither is ever clicked here.
    internal static bool HasPending(Bitmap bitmap, double scale, EpicOcrLine? exactResult)
    {
        if(exactResult is null || scale < .75 || scale > 3 || exactResult.Bounds.Left < bitmap.Width*.60) return false;
        int cx=bitmap.Width-(int)Math.Round(118*scale), cy=EpicFriendVision.Center(exactResult.Bounds).Y;
        int rx=(int)Math.Ceiling(15*scale), ry=(int)Math.Ceiling(12*scale);
        if(exactResult.Bounds.Right >= cx-rx || cx-rx<0 || cx+rx>=bitmap.Width || cy-ry<0 || cy+ry>=bitmap.Height) return false;
        var remaining=new HashSet<Point>();
        for(int y=cy-ry;y<=cy+ry;y++) for(int x=cx-rx;x<=cx+rx;x++)
        { var c=bitmap.GetPixel(x,y); if(c.G>=65 && c.G-c.R>=15 && c.G-c.B>=8) remaining.Add(new(x,y)); }
        int checks=0;
        while(remaining.Count>0)
        {
            var start=remaining.First(); remaining.Remove(start); var points=new List<Point>(); var queue=new Queue<Point>(); queue.Enqueue(start);
            while(queue.Count>0)
            {
                var p=queue.Dequeue(); points.Add(p);
                for(int dy=-1;dy<=1;dy++) for(int dx=-1;dx<=1;dx++) { var next=new Point(p.X+dx,p.Y+dy); if(remaining.Remove(next)) queue.Enqueue(next); }
            }
            if(CheckShape(points,scale)) checks++;
        }
        if(checks!=1) return false;
        int cancelX=bitmap.Width-(int)Math.Round(74*scale);
        for(int dy=-3;dy<=3;dy++) for(int dx=-2;dx<=2;dx++)
            if(CancelCross(bitmap,new(cancelX+dx,cy+dy),scale)) return true;
        return false;
    }
    private static bool CheckShape(List<Point> points,double scale)
    {
        if(points.Count<6*scale) return false;
        int left=points.Min(p=>p.X),right=points.Max(p=>p.X),top=points.Min(p=>p.Y),bottom=points.Max(p=>p.Y);
        double width=right-left,height=bottom-top;
        if(width<8*scale || width>20*scale || height<3*scale || height>13*scale) return false;
        var l=points.Where(p=>p.X<=left+width*.15).ToArray(); var r=points.Where(p=>p.X>=right-width*.15).ToArray();
        double lx=l.Average(p=>p.X),ly=l.Average(p=>p.Y),rx=r.Average(p=>p.X),ry=r.Average(p=>p.Y);
        double vx=points.Where(p=>p.Y>=bottom-Math.Max(0,(int)Math.Floor(scale*.5))).Average(p=>p.X),vy=bottom;
        if(vx<left+width*.20 || vx>left+width*.65 || vy-ly<1.2*scale || vy-ry<2*scale || ry>=ly-.8*scale) return false;
        double error=points.Average(p=>Math.Abs(p.Y-(p.X<=vx ? ly+(vy-ly)*(p.X-lx)/(vx-lx) : vy+(ry-vy)*(p.X-vx)/(rx-vx))));
        return error<=1.4*scale;
    }
    private static bool CancelCross(Bitmap bitmap,Point center,double scale)
    {
        bool Light(double dx,double dy)
        {
            int x=center.X+(int)Math.Round(dx*scale),y=center.Y+(int)Math.Round(dy*scale);
            int radius=Math.Max(1,(int)Math.Round(.5*scale));
            for(int yy=y-radius;yy<=y+radius;yy++) for(int xx=x-radius;xx<=x+radius;xx++)
            {
                if(xx<0 || yy<0 || xx>=bitmap.Width || yy>=bitmap.Height) continue;
                var c=bitmap.GetPixel(xx,yy);
                if(Math.Min(c.R,Math.Min(c.G,c.B))>=110 && Math.Max(c.R,Math.Max(c.G,c.B))-Math.Min(c.R,Math.Min(c.G,c.B))<60) return true;
            }
            return false;
        }
        int slash=0,backslash=0;
        foreach(int d in new[]{-4,-3,-2,2,3,4}) { if(Light(d,d)) backslash++; if(Light(d,-d)) slash++; }
        return slash>=5 && backslash>=5 && new[]{Light(0,-4),Light(0,4),Light(-4,0),Light(4,0)}.Count(v=>v)<=1;
    }

    private static double Score(Bitmap bitmap, Point center, double scale)
    {
        double Pixel(double dx, double dy)
        {
            double x = center.X + dx * scale, y = center.Y + dy * scale;
            int left = (int)Math.Floor(x), top = (int)Math.Floor(y);
            if (left < 0 || top < 0 || left + 1 >= bitmap.Width || top + 1 >= bitmap.Height) return 0;
            double Light(int px, int py) { var c = bitmap.GetPixel(px, py); return .2126*c.R + .7152*c.G + .0722*c.B; }
            double fx = x-left, fy = y-top;
            return (Light(left,top)*(1-fx)+Light(left+1,top)*fx)*(1-fy) + (Light(left,top+1)*(1-fx)+Light(left+1,top+1)*fx)*fy;
        }
        double background = new[] { Pixel(-9,-9), Pixel(9,-9), Pixel(-9,9), Pixel(9,9) }.Order().Take(2).Average();
        if (background > 140) return double.MaxValue;
        double Ink(double dx, double dy) => Math.Clamp((Pixel(dx,dy)-background)*255/(255-background),0,255);
        // The badge must contain a plus in all four directions. A clock, minus,
        // checkmark or disabled glyph must not be treated as an Add button.
        if (Ink(3.5,3.5) > 140 || new[] { Ink(2,3.5),Ink(5,3.5),Ink(3.5,2),Ink(3.5,5) }.Any(v => v > 190) ||
            new[] { Ink(2,2),Ink(5,2),Ink(2,5),Ink(5,5) }.Any(v => v < 180)) return double.MaxValue;
        double error = 0; int white = 0, matched = 0;
        for (int py = 0; py < 19; py++) for (int px = 0; px < 19; px++)
        {
            byte expected = AddGlyph[py*19+px]; double actual = Ink(px-9,py-9);
            error += Math.Abs(actual-expected);
            if (expected > 200) { white++; if (actual > 170) matched++; }
        }
        return matched >= white*.80 ? error/AddGlyph.Length : double.MaxValue;
    }
}
