using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using OpenCvSharp;

namespace GwentCompanion.Platform.Windows.Vision;

public sealed record DevotionVisualCue(DateTimeOffset At, string SourceCardId, string Evidence);

/// <summary>A bounded, preview-conditioned effect check. It produces a likelihood cue,
/// not proof of starting-list membership or a general status/ability simulator.</summary>
public sealed class OakcrittersEffectRecognizer : IDisposable
{
    private Mat? _template;
    private DateTimeOffset _started, _last;
    private int _baselineBleeds, _votes;
    private bool _reported;
    public Action<string>? Trace { get; set; }
    public DevotionVisualCue? Observe(PixelFrame frame, GwentVisualObservation screen,
        IReadOnlyList<CardSighting> titles, DateTimeOffset at)
    {
        if (screen.IsCardSelectionOverlay || screen.View != GwentViewKind.Board) { Reset(); return null; }
        var preview = titles.FirstOrDefault(s => s.Side == PlayerSide.Opponent && s.Source == CardSightSource.PlayPreview);
        if (preview is not null && preview.Card.Id != "202680") { Reset(); return null; }
        if (_template is null && preview?.Card.Id == "202680")
        {
            using var pixels = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.BgraPixels);
            var r = preview.Region; var w = r.Right - r.Left; var h = r.Bottom - r.Top;
            var inside = new NormalizedRegion(r.Left + .12 * w, r.Top + .18 * h, r.Right - .12 * w, r.Bottom - .10 * h);
            using var crop = new Mat(pixels, RectFor(inside, frame));
            _template = new Mat(); Cv2.CvtColor(crop, _template, ColorConversionCodes.BGRA2GRAY);
            _started = at; _last = default; _votes = 0; _reported = false;
            _baselineBleeds = BleedingMarkers(frame);
        }
        if (_template is null || _reported || at - _started < TimeSpan.FromSeconds(1)) return null;
        if (at - _started > TimeSpan.FromSeconds(14)) { Reset(); return null; }
        if (at <= _last || at - _last < TimeSpan.FromMilliseconds(350)) return null;
        _last = at;
        var matches = FindCopies(frame);
        var ranged = matches.Count(p => p.Y < .30);
        var melee = matches.Count(p => p.Y is >= .30 and < .46);
        var bleeds = BleedingMarkers(frame);
        Trace?.Invoke($"Oak effect {at:HH:mm:ss.fff}: ranged={ranged}, melee={melee}, bleeding markers={bleeds}, baseline={_baselineBleeds}");
        // Ranged copy alone is normal. Melee copy or ranged copy + new bleeding is distinctive.
        var positive = melee >= 2 || ranged >= 2 && bleeds > _baselineBleeds;
        _votes = positive ? _votes + 1 : 0;
        if (_votes < 2) return null;
        _reported = true;
        return new(at, "202680", melee >= 2
            ? "Oakcritters preview followed by two matching melee-row appearances in repeated frames; Devotion likely. Copy/row provenance still needs review."
            : "Oakcritters preview followed by two matching ranged-row appearances and a new bleeding-shaped status on the other board in repeated frames; Devotion likely. Concurrent effects/status attribution remain possible.");
    }
    private List<Point2d> FindCopies(PixelFrame frame)
    {
        using var pixels = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.BgraPixels);
        var region = new NormalizedRegion(.24, .14, .79, .46); var rect = RectFor(region, frame);
        using var crop = new Mat(pixels, rect); using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGRA2GRAY);
        var peaks = new List<(Point2d Point, double Score)>();
        var bestScore = 0d;
        for (var fraction = .035; fraction <= .061; fraction += .003)
        foreach (var aspect in new[] { .78, .88, 1.0, 1.1 })
        {
            var width = (int)(frame.Width * fraction); var height = (int)Math.Round(width * _template!.Height / (double)_template.Width * aspect);
            using var template = new Mat(); Cv2.Resize(_template, template, new Size(width, height), 0, 0, InterpolationFlags.Area);
            if (height >= gray.Height) continue;
            using var scores = new Mat(); Cv2.MatchTemplate(gray, template, scores, TemplateMatchModes.CCoeffNormed);
            for (var n = 0; n < 4; n++)
            {
                Cv2.MinMaxLoc(scores, out _, out var maximum, out _, out var point);
                bestScore = Math.Max(bestScore, maximum);
                if (maximum < .80) break;
                peaks.Add((new((rect.X + point.X + width / 2d) / frame.Width, (rect.Y + point.Y + height / 2d) / frame.Height), maximum));
                Cv2.Rectangle(scores, new Rect(Math.Max(0, point.X - width / 2), Math.Max(0, point.Y - height / 2),
                    Math.Min(width, scores.Width - Math.Max(0, point.X - width / 2)), Math.Min(height, scores.Height - Math.Max(0, point.Y - height / 2))), Scalar.All(-1), -1);
            }
        }
        var result = new List<Point2d>();
        Trace?.Invoke($"Oak best correlation: {bestScore:F3}");
        foreach (var peak in peaks.OrderByDescending(p => p.Score))
            if (!result.Any(p => Math.Abs(p.X - peak.Point.X) < .036 && Math.Abs(p.Y - peak.Point.Y) < .08)) result.Add(peak.Point);
        return result;
    }
    public static int BleedingMarkers(PixelFrame frame)
    {
        using var mask = new Mat(frame.Height, frame.Width, MatType.CV_8UC1, Scalar.Black);
        for (var y = (int)(frame.Height * .48); y < frame.Height * .88; y++)
        for (var x = (int)(frame.Width * .24); x < frame.Width * .79; x++)
        {
            var p = frame.GetPixel(x, y);
            if (p.Red > 130 && p.Red > p.Green * 1.8 && p.Red > p.Blue * 1.6) mask.Set(y, x, (byte)255);
        }
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        return contours.Select(Cv2.BoundingRect).Count(r => r.Width / (double)frame.Width is >= .006 and <= .022 &&
            r.Height / (double)frame.Height is >= .035 and <= .082 && r.Height / (double)r.Width >= 1.65);
    }
    private static Rect RectFor(NormalizedRegion r, PixelFrame f) => new(r.PixelLeft(f.Width), r.PixelTop(f.Height),
        r.PixelRight(f.Width) - r.PixelLeft(f.Width), r.PixelBottom(f.Height) - r.PixelTop(f.Height));
    public void Reset() { _template?.Dispose(); _template = null; _votes = 0; _reported = false; }
    public void Dispose() => Reset();
}
