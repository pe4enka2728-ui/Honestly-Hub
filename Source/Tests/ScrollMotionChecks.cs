using VpnZapretSwitch;

internal static class ScrollMotionChecks
{
    // Pure arithmetic only; no UI, processes, network, files or real VPN state.
    public static int Run()
    {
        var gradual = New(); gradual.MoveTo(700);
        double previous = gradual.Position;
        for (int i = 0; i < 120; i++)
        {
            gradual.Advance(1d / 120);
            Assert(gradual.Position >= previous && gradual.Position <= 700, "Scroll overshoot or reversal");
            previous = gradual.Position;
        }
        Assert(!gradual.Moving && gradual.Position == 700, "Animation did not settle");

        var oneFrame = New(); var manyFrames = New();
        oneFrame.MoveTo(1900); manyFrames.MoveTo(1900);
        oneFrame.Advance(.13);
        for (int i = 0; i < 13; i++) manyFrames.Advance(.01);
        Assert(Math.Abs(oneFrame.Position - manyFrames.Position) < .00001, "Motion depends on the frame count");

        var wheel = New(); wheel.MoveBy(72); wheel.MoveBy(72); wheel.MoveBy(72);
        Assert(wheel.Destination == 216, "Wheel events were lost");
        wheel.Advance(.04); double atReverse = wheel.Position; wheel.MoveBy(-30);
        Assert(wheel.Destination < atReverse, "Opposite wheel direction kept the old momentum");

        var resized = New(); resized.Snap(1500); resized.MoveTo(1900); resized.SetMaximum(300);
        Assert(resized.Position == 300 && resized.Destination == 300 && !resized.Moving, "Resize did not clamp both positions");
        resized.SetMaximum(0); Assert(resized.Position == 0 && resized.Destination == 0, "Empty content retained scroll offset");

        var boundary = New(); boundary.MoveBy(-100); Assert(boundary.Destination == 0, "Top boundary crossed");
        boundary.MoveBy(100000); boundary.Advance(1); Assert(boundary.Position == 2000 && !boundary.Moving, "Bottom boundary crossed");

        var stopped = New(); stopped.MoveTo(900); stopped.Advance(.02); stopped.Snap(stopped.Position);
        double before = stopped.Position; stopped.Advance(10);
        Assert(stopped.Position == before && !stopped.Moving, "Pointer-down cancellation moved a server row");
        Console.WriteLine("PASS: 6 pure scrolling checks (settling, timing, wheel direction, resize, limits, cancellation)");
        return 6;
    }
    private static ScrollMotion New() { var motion = new ScrollMotion(); motion.SetMaximum(2000); return motion; }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
}
