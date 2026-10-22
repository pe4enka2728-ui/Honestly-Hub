namespace VpnZapretSwitch;

internal sealed record EpicWindowCandidate(nint Handle, Size ClientSize);
internal sealed class EpicWindowReadiness
{
    private EpicWindowCandidate? previous;
    private TimeSpan unchangedSince;
    internal nint Observe(IEnumerable<EpicWindowCandidate> windows, TimeSpan now)
    {
        var ready = windows.Where(w => w.Handle != 0 && EpicCaptureGeometry.IsUsable(w.ClientSize))
            .DistinctBy(w => w.Handle).ToArray();
        if (ready.Length > 1) throw new InvalidOperationException("Открыто несколько полноразмерных окон Epic. Оставьте одно окно лаунчера.");
        var current = ready.SingleOrDefault();
        if (current is null || previous != current)
        { previous = current; unchangedSince = now; return 0; }
        return now - unchangedSince >= TimeSpan.FromMilliseconds(500) ? current.Handle : 0;
    }
}
internal static class EpicCaptureGeometry
{
    internal static bool IsUsable(Size size) => size.Width >= 950 && size.Height >= 600 && FitsMemory(size);
    internal static bool FitsMemory(Size size) => size.Width is > 0 and <= 16384 && size.Height is > 0 and <= 8640 &&
        (long)size.Width * size.Height <= 40_000_000;
    internal static string Problem(Size size) => IsUsable(size)
        ? $"Рабочая область Epic {size.Width}×{size.Height} подходит, но основное окно не стало стабильным за 30 секунд. Дождитесь завершения загрузки и повторите."
        : !FitsMemory(size) && size.Width >= 950 && size.Height >= 600
        ? $"Область Epic {size.Width}×{size.Height} слишком велика для захвата. Уменьшите размер окна."
        : $"Окно Epic ещё не готово или его рабочая область мала: {size.Width}×{size.Height} пикселей. Требуется минимум 950×600. Разверните основное окно лаунчера и повторите.";
    internal static Size OcrSize(Size source, int maximum)
    {
        if (source.Width < 1 || source.Height < 1 || maximum < 1) throw new ArgumentOutOfRangeException(nameof(source));
        double factor = Math.Min(1, maximum / (double)Math.Max(source.Width, source.Height));
        return new(Math.Max(1,(int)Math.Floor(source.Width * factor)),Math.Max(1,(int)Math.Floor(source.Height * factor)));
    }
    internal static Rectangle RestoreOcrBounds(Rectangle bounds, Size source, Size scaled)
    {
        double x = source.Width / (double)scaled.Width, y = source.Height / (double)scaled.Height;
        return Rectangle.Intersect(new(Point.Empty,source),Rectangle.FromLTRB(
            (int)Math.Floor(bounds.Left*x),(int)Math.Floor(bounds.Top*y),(int)Math.Ceiling(bounds.Right*x),(int)Math.Ceiling(bounds.Bottom*y)));
    }
}
