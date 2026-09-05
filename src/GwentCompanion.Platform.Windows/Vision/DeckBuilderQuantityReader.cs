using System.IO;
using GwentCompanion.Core.Vision;
using OpenCvSharp;

namespace GwentCompanion.Platform.Windows.Vision;

/// <summary>Matches the static black-on-parchment ×2 UI glyph, never card artwork.</summary>
public sealed class DeckBuilderQuantityReader : IDisposable
{
    private readonly Mat _template;
    public const double MinimumScore = .86;
    public DeckBuilderQuantityReader()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vision-assets", "deck-builder-copies-x2.png");
        if (!File.Exists(path)) throw new FileNotFoundException("The deck-builder quantity reference is missing.", path);
        _template = Cv2.ImRead(path, ImreadModes.Grayscale);
        Cv2.GaussianBlur(_template, _template, new Size(3, 3), .65);
    }

    public double Score(PixelFrame frame, NormalizedRegion region)
    {
        using var pixels = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.BgraPixels);
        var rect = new Rect(region.PixelLeft(frame.Width), region.PixelTop(frame.Height),
            region.PixelRight(frame.Width) - region.PixelLeft(frame.Width), region.PixelBottom(frame.Height) - region.PixelTop(frame.Height));
        using var crop = new Mat(pixels, rect); using var gray = new Mat(); using var normalized = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGRA2GRAY);
        // Reference was read from a 1706×961 client image. Normalize the UI scale before comparing.
        var width = (int)Math.Round(rect.Width * 1706d / frame.Width);
        var height = (int)Math.Round(rect.Height * 961d / frame.Height);
        if (width < _template.Width || height < _template.Height) return 0;
        Cv2.Resize(gray, normalized, new Size(width, height), 0, 0, InterpolationFlags.Cubic);
        // Suppress subpixel raster differences at fractional desktop scaling, not glyph structure.
        Cv2.GaussianBlur(normalized, normalized, new Size(3, 3), .65);
        using var scores = new Mat();
        Cv2.MatchTemplate(normalized, _template, scores, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(scores, out double _, out double maximum);
        return double.IsFinite(maximum) ? maximum : 0;
    }
    public void Dispose() => _template.Dispose();
}
