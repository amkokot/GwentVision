namespace GwentCompanion.Core.Vision;

public sealed record MoveHistoryCardCandidate(NormalizedRegion Region, double Score);

public sealed class MoveHistoryCardLocator
{
    // Positions within the captured GWENT client area. The older calibration
    // screenshots included a pillar-boxed window frame; live capture does not.
    private static readonly double[] ColumnStarts = [0.045, 0.109];

    public IReadOnlyList<MoveHistoryCardCandidate> Locate(PixelFrame frame, int maximumCards = 12)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (maximumCards <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCards));
        }

        if (new GwentVisualStateDetector().Analyze(frame).View != GwentViewKind.MoveHistory)
        {
            return Array.Empty<MoveHistoryCardCandidate>();
        }

        const double cardWidth = 0.052;
        const double cardHeight = 0.138;
        var yStep = Math.Max(2, frame.Height / 180);
        var scored = new List<MoveHistoryCardCandidate>();
        foreach (var left in ColumnStarts)
        {
            var topStart = (int)Math.Round(frame.Height * 0.14);
            var topEnd = (int)Math.Round(frame.Height * (0.84 - cardHeight));
            for (var top = topStart; top <= topEnd; top += yStep)
            {
                var region = new NormalizedRegion(
                    left,
                    top / (double)frame.Height,
                    left + cardWidth,
                    top / (double)frame.Height + cardHeight);
                scored.Add(new MoveHistoryCardCandidate(region, Cardness(frame, region)));
            }
        }

        var selected = new List<MoveHistoryCardCandidate>();
        foreach (var candidate in scored.OrderByDescending(item => item.Score))
        {
            if (candidate.Score < 0.30)
            {
                break;
            }

            if (selected.Any(existing => Overlap(existing.Region, candidate.Region) >= 0.35))
            {
                continue;
            }

            selected.Add(candidate);
            if (selected.Count == maximumCards)
            {
                break;
            }
        }

        return selected.OrderBy(item => item.Region.Top).ToArray();
    }

    private static double Cardness(PixelFrame frame, NormalizedRegion region)
    {
        var left = region.PixelLeft(frame.Width);
        var top = region.PixelTop(frame.Height);
        var right = region.PixelRight(frame.Width) - 1;
        var bottom = region.PixelBottom(frame.Height) - 1;
        var inset = Math.Max(2, frame.Width / 480);
        double borderDifference = 0;
        var borderSamples = 0;

        for (var y = top + inset; y < bottom - inset; y += 2)
        {
            borderDifference += Math.Abs(Luma(frame.GetPixel(left + inset, y)) - Luma(frame.GetPixel(Math.Max(0, left - inset), y)));
            borderDifference += Math.Abs(Luma(frame.GetPixel(right - inset, y)) - Luma(frame.GetPixel(Math.Min(frame.Width - 1, right + inset), y)));
            borderSamples += 2;
        }

        for (var x = left + inset; x < right - inset; x += 2)
        {
            borderDifference += Math.Abs(Luma(frame.GetPixel(x, top + inset)) - Luma(frame.GetPixel(x, Math.Max(0, top - inset))));
            borderDifference += Math.Abs(Luma(frame.GetPixel(x, bottom - inset)) - Luma(frame.GetPixel(x, Math.Min(frame.Height - 1, bottom + inset))));
            borderSamples += 2;
        }

        var values = new List<double>();
        for (var y = top + inset * 2; y < bottom - inset * 2; y += 4)
        {
            for (var x = left + inset * 2; x < right - inset * 2; x += 4)
            {
                values.Add(Luma(frame.GetPixel(x, y)));
            }
        }

        if (borderSamples == 0 || values.Count == 0)
        {
            return 0;
        }

        var mean = values.Average();
        var deviation = Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / values.Count);
        var borderScore = borderDifference / borderSamples / 90d;
        var textureScore = deviation / 75d;
        return Math.Clamp(borderScore * 0.62 + textureScore * 0.38, 0, 1);
    }

    private static double Luma(PixelColor color) =>
        color.Red * 0.2126 + color.Green * 0.7152 + color.Blue * 0.0722;

    private static double Overlap(NormalizedRegion first, NormalizedRegion second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        if (right <= left || bottom <= top)
        {
            return 0;
        }

        var intersection = (right - left) * (bottom - top);
        var smallerArea = Math.Min(
            (first.Right - first.Left) * (first.Bottom - first.Top),
            (second.Right - second.Left) * (second.Bottom - second.Top));
        return intersection / smallerArea;
    }
}
