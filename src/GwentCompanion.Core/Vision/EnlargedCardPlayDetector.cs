using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Vision;

public sealed record EnlargedCardCandidate(
    NormalizedRegion Region,
    double Score,
    double BoundaryContrast,
    double EdgeClosure,
    double InteriorTexture,
    double GoldBorderRatio,
    PlayerSide? SideHint = null,
    double SideConfidence = 0);

public sealed class EnlargedCardPlayDetector
{
    private static readonly SearchProfile[] SearchProfiles =
    [
        // Both previews are on the RIGHT; the opponent preview is higher.
        // The central battlefield and left leader animation are never play previews.
        new([0.088, 0.098, 0.108], 0.835, 0.925, 0.015, 0.48, 0.58, 0.015, PlayerSide.User, 0.98),
        new([0.088, 0.098, 0.108], 0.82, 0.92, 0.015, 0.24, 0.32, 0.015, PlayerSide.Opponent, 0.98),
    ];

    public EnlargedCardCandidate? Locate(PixelFrame frame)
    {
        return LocateAll(frame, 1).FirstOrDefault();
    }

    public IReadOnlyList<EnlargedCardCandidate> LocateAll(PixelFrame frame, int maximumResults = 4)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResults);
        var matches = new List<EnlargedCardCandidate>();
        foreach (var profile in SearchProfiles)
        {
            foreach (var normalizedWidth in profile.NormalizedWidths)
            {
                var pixelWidth = normalizedWidth * frame.Width;
                var normalizedHeight = pixelWidth / 0.70 / frame.Height;
                for (var centerY = profile.MinimumY; centerY <= profile.MaximumY; centerY += profile.StepY)
                {
                    for (var centerX = profile.MinimumX; centerX <= profile.MaximumX; centerX += profile.StepX)
                    {
                        var region = new NormalizedRegion(
                            centerX - normalizedWidth / 2,
                            centerY - normalizedHeight / 2,
                            centerX + normalizedWidth / 2,
                            centerY + normalizedHeight / 2);
                        var candidate = Score(frame, region) with
                        {
                            SideHint = profile.SideHint,
                            SideConfidence = profile.SideConfidence,
                        };
                        if (candidate.EdgeClosure < 0.32 || candidate.InteriorTexture < 0.11)
                        {
                            continue;
                        }

                        if (candidate.Score >= 0.285 && candidate.BoundaryContrast >= 0.18)
                        {
                            matches.Add(candidate);
                        }
                    }
                }
            }
        }

        if (matches.Count == 0)
        {
            return Array.Empty<EnlargedCardCandidate>();
        }

        var bestScores = matches.GroupBy(item => item.SideHint).ToDictionary(group => group.Key!.Value, group => group.Max(item => item.Score));
        var selected = new List<EnlargedCardCandidate>();
        foreach (var candidate in matches
                     .Where(item => item.Score >= bestScores[item.SideHint!.Value] - 0.035)
                     .OrderByDescending(item => item.Score))
        {
            if (selected.Any(item => IntersectionOverUnion(item.Region, candidate.Region) >= 0.32))
            {
                continue;
            }

            selected.Add(candidate);
            if (selected.Count >= maximumResults)
            {
                break;
            }
        }

        return selected;
    }

    public static EnlargedCardCandidate Score(PixelFrame frame, NormalizedRegion region)
    {
        var left = region.PixelLeft(frame.Width);
        var top = region.PixelTop(frame.Height);
        var right = region.PixelRight(frame.Width) - 1;
        var bottom = region.PixelBottom(frame.Height) - 1;
        var leftEdge = Edge(frame, left, top, right, bottom, EdgeKind.Left);
        var rightEdge = Edge(frame, left, top, right, bottom, EdgeKind.Right);
        var topEdge = Edge(frame, left, top, right, bottom, EdgeKind.Top);
        var bottomEdge = Edge(frame, left, top, right, bottom, EdgeKind.Bottom);
        var boundaryContrast = (leftEdge.Contrast + rightEdge.Contrast + topEdge.Contrast + bottomEdge.Contrast) / 4;
        var edgeClosure = Math.Min(
            (leftEdge.Closure + rightEdge.Closure) / 2,
            (topEdge.Closure + bottomEdge.Closure) / 2);
        var texture = InteriorTexture(frame, left, top, right, bottom);
        var gold = (leftEdge.Gold + rightEdge.Gold + topEdge.Gold + bottomEdge.Gold) / 4;
        var score = boundaryContrast * 0.48 + edgeClosure * 0.27 + texture * 0.17 + gold * 0.08;
        return new EnlargedCardCandidate(region, score, boundaryContrast, edgeClosure, texture, gold);
    }

    private static EdgeScore Edge(
        PixelFrame frame,
        int left,
        int top,
        int right,
        int bottom,
        EdgeKind edge)
    {
        double contrast = 0;
        var closure = 0;
        var gold = 0;
        var samples = 0;
        var horizontal = edge is EdgeKind.Top or EdgeKind.Bottom;
        var start = horizontal ? left + 4 : top + 4;
        var end = horizontal ? right - 4 : bottom - 4;
        for (var coordinate = start; coordinate <= end; coordinate += 4)
        {
            var (insideX, insideY, outsideX, outsideY) = edge switch
            {
                EdgeKind.Left => (left + 3, coordinate, left - 3, coordinate),
                EdgeKind.Right => (right - 3, coordinate, right + 3, coordinate),
                EdgeKind.Top => (coordinate, top + 3, coordinate, top - 3),
                _ => (coordinate, bottom - 3, coordinate, bottom + 3),
            };
            if ((uint)outsideX >= (uint)frame.Width || (uint)outsideY >= (uint)frame.Height)
            {
                continue;
            }

            var inside = frame.GetPixel(insideX, insideY);
            var outside = frame.GetPixel(outsideX, outsideY);
            var difference = ColorDistance(inside, outside);
            contrast += difference;
            if (difference >= 0.13)
            {
                closure++;
            }

            if (IsGold(inside))
            {
                gold++;
            }

            samples++;
        }

        return samples == 0
            ? default
            : new EdgeScore(contrast / samples, closure / (double)samples, gold / (double)samples);
    }

    private static double InteriorTexture(PixelFrame frame, int left, int top, int right, int bottom)
    {
        double total = 0;
        double squared = 0;
        var samples = 0;
        var insetX = Math.Max(4, (right - left) / 10);
        var insetY = Math.Max(4, (bottom - top) / 12);
        for (var y = top + insetY; y < bottom - insetY; y += 6)
        {
            for (var x = left + insetX; x < right - insetX; x += 6)
            {
                var color = frame.GetPixel(x, y);
                var luminance = (color.Red * 0.2126 + color.Green * 0.7152 + color.Blue * 0.0722) / 255;
                total += luminance;
                squared += luminance * luminance;
                samples++;
            }
        }

        if (samples == 0)
        {
            return 0;
        }

        var mean = total / samples;
        return Math.Sqrt(Math.Max(0, squared / samples - mean * mean));
    }

    private static double ColorDistance(PixelColor left, PixelColor right)
    {
        var red = left.Red - right.Red;
        var green = left.Green - right.Green;
        var blue = left.Blue - right.Blue;
        return Math.Sqrt(red * red + green * green + blue * blue) / 441.673;
    }

    private static bool IsGold(PixelColor color) =>
        color.Red >= 105 && color.Green >= 70 && color.Blue <= 115 &&
        color.Red >= color.Blue * 1.18 && color.Green >= color.Blue * 1.05;

    private static double IntersectionOverUnion(NormalizedRegion left, NormalizedRegion right)
    {
        var intersectionWidth = Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left));
        var intersectionHeight = Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top));
        var intersection = intersectionWidth * intersectionHeight;
        var leftArea = (left.Right - left.Left) * (left.Bottom - left.Top);
        var rightArea = (right.Right - right.Left) * (right.Bottom - right.Top);
        var union = leftArea + rightArea - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private enum EdgeKind
    {
        Left,
        Right,
        Top,
        Bottom,
    }

    private readonly record struct EdgeScore(double Contrast, double Closure, double Gold);

    private sealed record SearchProfile(
        double[] NormalizedWidths,
        double MinimumX,
        double MaximumX,
        double StepX,
        double MinimumY,
        double MaximumY,
        double StepY,
        PlayerSide? SideHint,
        double SideConfidence);
}
