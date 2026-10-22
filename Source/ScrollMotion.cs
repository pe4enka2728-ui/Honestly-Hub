namespace VpnZapretSwitch;

// Pure time-based motion; repeated wheel events accumulate at the destination.
// No controls, windows, timers or network resources are created by this class.
internal sealed class ScrollMotion
{
    public double Position { get; private set; }
    public double Destination { get; private set; }
    public int Maximum { get; private set; }
    public bool Moving => Math.Abs(Destination - Position) > .01;
    public void SetMaximum(int maximum)
    {
        Maximum = Math.Max(0, maximum);
        Position = Math.Clamp(Position, 0, Maximum);
        Destination = Math.Clamp(Destination, 0, Maximum);
    }
    public void Snap(double value) => Position = Destination = Math.Clamp(value, 0, Maximum);
    public void MoveTo(double value) => Destination = Math.Clamp(value, 0, Maximum);
    public void MoveBy(double delta)
    {
        if (delta > 0 && Destination < Position || delta < 0 && Destination > Position) Destination = Position;
        MoveTo(Destination + delta);
    }
    public void Advance(double seconds)
    {
        if (seconds <= 0 || !double.IsFinite(seconds)) return;
        Position += (Destination - Position) * (1 - Math.Exp(-seconds / .065));
        if (Math.Abs(Destination - Position) < .25) Position = Destination;
    }
}
