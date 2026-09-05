using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Vision;

public sealed record CardArtReference(CardDefinition Card, VisualDescriptor Descriptor);

public sealed record CardArtMatch(CardDefinition Card, double Distance, double Confidence);
public sealed record AlignedCardArtMatch(CardDefinition Card, double Distance, NormalizedRegion Region);

public sealed class CardArtMatcher
{
    private readonly IReadOnlyList<CardArtReference> _references;

    public CardArtMatcher(IEnumerable<CardArtReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        _references = references.ToArray();
        if (_references.Count == 0)
        {
            throw new ArgumentException("At least one card-art reference is required.", nameof(references));
        }
    }

    public IReadOnlyList<CardArtMatch> Rank(
        PixelFrame frame,
        NormalizedRegion cardRegion,
        int maximumResults = 10)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (maximumResults <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        var observed = VisualDescriptor.Create(frame, cardRegion);
        return _references
            .Select(reference =>
            {
                var distance = observed.DistanceTo(reference.Descriptor);
                return new CardArtMatch(reference.Card, distance, 1d / (1d + distance));
            })
            .GroupBy(match => match.Card.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.MinBy(match => match.Distance)!)
            .OrderBy(match => match.Distance)
            .Take(maximumResults)
            .ToArray();
    }

    public IReadOnlyList<AlignedCardArtMatch> RankAligned(PixelFrame frame, NormalizedRegion region, int maximumResults = 3)
    {
        var shortlist = Rank(frame, region, 20).Select(item => item.Card.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var references = _references.Where(item => shortlist.Contains(item.Card.Id)).ToArray();
        var best = new Dictionary<string, AlignedCardArtMatch>(StringComparer.OrdinalIgnoreCase);
        var width = region.Right - region.Left;
        var height = region.Bottom - region.Top;
        foreach (var scale in new[] { 0.94, 1.0, 1.06 })
        foreach (var dx in new[] { -0.055, 0.0, 0.055 })
        foreach (var dy in new[] { -0.12, -0.06, 0.0, 0.06, 0.12 })
        {
            var centerX = (region.Left + region.Right) / 2 + width * dx;
            var centerY = (region.Top + region.Bottom) / 2 + height * dy;
            var adjusted = new NormalizedRegion(
                centerX - width * scale / 2, centerY - height * scale / 2,
                centerX + width * scale / 2, centerY + height * scale / 2);
            var descriptor = VisualDescriptor.Create(frame, adjusted);
            foreach (var reference in references)
            {
                var distance = descriptor.DistanceTo(reference.Descriptor);
                if (!best.TryGetValue(reference.Card.Id, out var previous) || distance < previous.Distance)
                    best[reference.Card.Id] = new AlignedCardArtMatch(reference.Card, distance, adjusted);
            }
        }
        return best.Values.OrderBy(item => item.Distance).Take(maximumResults).ToArray();
    }

    public double IdentityDistance(PixelFrame frame, NormalizedRegion region, string cardId)
    {
        var references = _references.Where(item => item.Card.Id.Equals(cardId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (references.Length == 0) return double.PositiveInfinity;
        var observed = VisualDescriptor.Create(frame, region);
        return references.Min(reference => observed.DistanceTo(reference.Descriptor));
    }
}

public sealed class VisualDescriptor
{
    private const int Columns = 10;
    private const int Rows = 14;
    private readonly float[] _values;

    private VisualDescriptor(float[] values)
    {
        _values = values;
    }

    public static VisualDescriptor Create(PixelFrame frame) =>
        Create(frame, new NormalizedRegion(0, 0, 1, 1));

    public static VisualDescriptor Create(PixelFrame frame, NormalizedRegion region)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var width = region.Right - region.Left;
        var height = region.Bottom - region.Top;
        var inner = new NormalizedRegion(
            region.Left + width * 0.10,
            region.Top + height * 0.08,
            region.Right - width * 0.10,
            region.Bottom - height * 0.08);
        var rawRed = new float[Columns * Rows];
        var rawGreen = new float[Columns * Rows];
        var rawBlue = new float[Columns * Rows];

        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var cell = new NormalizedRegion(
                    inner.Left + (inner.Right - inner.Left) * column / Columns,
                    inner.Top + (inner.Bottom - inner.Top) * row / Rows,
                    inner.Left + (inner.Right - inner.Left) * (column + 1d) / Columns,
                    inner.Top + (inner.Bottom - inner.Top) * (row + 1d) / Rows);
                var average = Average(frame, cell);
                var index = row * Columns + column;
                rawRed[index] = average.Red / 255f;
                rawGreen[index] = average.Green / 255f;
                rawBlue[index] = average.Blue / 255f;
            }
        }

        var normalizedRed = Normalize(rawRed);
        var normalizedGreen = Normalize(rawGreen);
        var normalizedBlue = Normalize(rawBlue);
        var values = new float[Columns * Rows * 5];
        for (var index = 0; index < rawRed.Length; index++)
        {
            var sum = Math.Max(0.05f, rawRed[index] + rawGreen[index] + rawBlue[index]);
            var offset = index * 5;
            values[offset] = normalizedRed[index];
            values[offset + 1] = normalizedGreen[index];
            values[offset + 2] = normalizedBlue[index];
            values[offset + 3] = rawRed[index] / sum;
            values[offset + 4] = rawBlue[index] / sum;
        }

        return new VisualDescriptor(values);
    }

    public double DistanceTo(VisualDescriptor other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (_values.Length != other._values.Length)
        {
            throw new InvalidOperationException("Visual descriptors have incompatible dimensions.");
        }

        double sum = 0;
        for (var index = 0; index < _values.Length; index++)
        {
            var difference = _values[index] - other._values[index];
            sum += difference * difference;
        }

        return Math.Sqrt(sum / _values.Length);
    }

    private static PixelColor Average(PixelFrame frame, NormalizedRegion region)
    {
        var left = region.PixelLeft(frame.Width);
        var top = region.PixelTop(frame.Height);
        var right = Math.Max(left + 1, region.PixelRight(frame.Width));
        var bottom = Math.Max(top + 1, region.PixelBottom(frame.Height));
        long red = 0;
        long green = 0;
        long blue = 0;
        var count = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var color = frame.GetPixel(x, y);
                red += color.Red;
                green += color.Green;
                blue += color.Blue;
                count++;
            }
        }

        return new PixelColor(
            (byte)(red / count),
            (byte)(green / count),
            (byte)(blue / count));
    }

    private static float[] Normalize(float[] values)
    {
        var mean = values.Average();
        var variance = values.Sum(value => (value - mean) * (value - mean)) / values.Length;
        var deviation = Math.Max(0.08, Math.Sqrt(variance));
        return values.Select(value => (float)((value - mean) / deviation)).ToArray();
    }
}
