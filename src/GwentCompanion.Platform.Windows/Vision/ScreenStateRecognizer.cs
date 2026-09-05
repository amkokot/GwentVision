using GwentCompanion.Core.Vision;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using System.IO;
using OpenCvSharp;

namespace GwentCompanion.Platform.Windows.Vision;

public sealed record VisibleTextLine(string Text, NormalizedRegion Region);

/// <summary>Only reads the visible screen header. No game process or private state is accessed.</summary>
public sealed class ScreenStateRecognizer : IDisposable
{
    private readonly GwentVisualStateDetector _detector = new();
    private readonly OcrEngine _ocr = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
        ?? throw new InvalidOperationException("Windows English OCR is unavailable. Install the English language OCR feature before running recognition.");
    private readonly Mat? _graveyardHeader = LoadHeader();
    private sealed record OcrSnapshot(byte[] Pixels, IReadOnlyList<VisibleTextLine> Lines);
    private readonly Dictionary<string, OcrSnapshot> _textCache = [];
    private readonly Queue<string> _textKeys = new();
    private string? _endVote;
    public bool UseTextCache { get; set; } = true;
    public int OcrCalls { get; private set; }
    public int OcrCacheHits { get; private set; }
    public int CachedTextBytes { get; private set; }

    public async Task<GwentVisualObservation> AnalyzeAsync(PixelFrame frame)
    {
        var observation = _detector.Analyze(frame);
        // Top-center headings distinguish redraw, tutor/Create choices, deck and graveyard
        // from actual play. Their backgrounds vary with the selected battlefield.
        if (HasGraveyardHeader(frame)) return observation with
        {
            IsCardSelectionOverlay = true, CardSelectionConfidence = 1, ScreenHeader = "GRAVEYARD (header shape)",
        };
        var header = await ReadAsync(frame, new NormalizedRegion(0.27, 0.0, 0.75, 0.125)).ConfigureAwait(false);
        // The forfeit/disconnect result is a centered modal, not a top heading.
        // Require both its fixed layout and repeated complete result text.
        if (observation.MatchHudVisible == false && observation.TooltipRegion is { Left: >= .30, Right: <= .70, Top: >= .30, Bottom: <= .75 })
        {
            var modal = string.Join('\n', (await ReadLinesAsync(frame, new(.35, .39, .66, .54), enhance: false).ConfigureAwait(false))
                .Select(line => line.Text)).ToUpperInvariant();
            var ended = (modal.Contains("GAME OVER", StringComparison.Ordinal) || modal.Contains("GAME OUER", StringComparison.Ordinal)) &&
                modal.Contains("FORFEIT", StringComparison.Ordinal);
            if (ended && _endVote == "GAME OVER") header = "GAME OVER";
            _endVote = ended ? "GAME OVER" : null;
        }
        else _endVote = null;
        // Verified against the latest result screenshot: the decorative V is read as U.
        // Normalize only this exact heading, never arbitrary OCR letters or numeric ratings.
        if (header.Trim().Equals("UICTORY", StringComparison.OrdinalIgnoreCase)) header = "VICTORY";
        var overlay = IsOverlayHeader(header);
        return observation with
        {
            IsCardSelectionOverlay = observation.IsCardSelectionOverlay || overlay,
            CardSelectionConfidence = overlay ? 1 : observation.CardSelectionConfidence,
            ScreenHeader = string.IsNullOrWhiteSpace(header) ? null : header,
        };
    }

    private static bool IsOverlayHeader(string header)
    {
        var compact = string.Concat(header.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        return new[] { "CHOOSE", "SELECT", "PICK", "FICK", "CARDTOPLAY", "CARDTODISCARD", "GRAVEYARD", "GRAUEYARD", "DECK", "ROUND", "ROIJND", "REDRAW", "VICTORY", "DEFEAT", "DRAW", "GAMEOVER" }
            .Any(word => compact.Contains(word, StringComparison.Ordinal));
    }

    public async Task<string> ReadAsync(PixelFrame frame, NormalizedRegion region)
        => string.Join('\n', (await ReadLinesAsync(frame, region).ConfigureAwait(false)).Select(line => line.Text));

    public async Task<IReadOnlyList<VisibleTextLine>> ReadLinesAsync(PixelFrame frame, NormalizedRegion region,
        int scale = 3, bool enhance = true, bool whiteLetterMask = false, bool smooth = false)
    {
        if (scale is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(scale));
        var pixels = OcrPixels.Build(frame, region, scale, enhance, whiteLetterMask);
        return await ReadPixelsAsync(frame, region, scale, pixels, smooth).ConfigureAwait(false);
    }

    /// <summary>One optional style-aware retry, sharing the bounded OCR cache and exact source coordinates.</summary>
    public async Task<IReadOnlyList<VisibleTextLine>> ReadTitleLinesAsync(PixelFrame frame, NormalizedRegion region)
    {
        var width = region.PixelRight(frame.Width) - region.PixelLeft(frame.Width);
        var height = region.PixelBottom(frame.Height) - region.PixelTop(frame.Height);
        if (width < 8 || height < 8) return [];
        var scale = 4;
        // Keep supplemental OCR inside both the engine's dimension limit and the
        // existing 4 MiB cache budget, including on high-resolution captures.
        while (scale >= 2 && (width * scale > OcrEngine.MaxImageDimension || height * scale > OcrEngine.MaxImageDimension ||
            4L * width * height * scale * scale > 4 * 1024 * 1024)) scale--;
        if (scale < 2) return [];
        var pixels = TitleTextStyle.TryBuild(frame, region, scale);
        return pixels is null ? [] : await ReadPixelsAsync(frame, region, scale, pixels, smooth: true).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<VisibleTextLine>> ReadPixelsAsync(PixelFrame frame, NormalizedRegion region,
        int scale, byte[] pixels, bool smooth)
    {
        var left = region.PixelLeft(frame.Width); var top = region.PixelTop(frame.Height);
        var width = (region.PixelRight(frame.Width) - left) * scale;
        var height = (region.PixelBottom(frame.Height) - top) * scale;
        if (smooth)
        {
            using var nearest = Mat.FromPixelData(height, width, MatType.CV_8UC4, pixels);
            using var original = new Mat(); using var enlarged = new Mat();
            Cv2.Resize(nearest, original, new Size(width / scale, height / scale), 0, 0, InterpolationFlags.Area);
            Cv2.Resize(original, enlarged, new Size(width, height), 0, 0, InterpolationFlags.Cubic);
            System.Runtime.InteropServices.Marshal.Copy(enlarged.Data, pixels, 0, pixels.Length);
        }
        // The hash selects an entry; byte equality proves identical OCR input.
        // Geometry is part of the key so cached bounding boxes remain exact.
        var key = UseTextCache ? $"{frame.Width},{frame.Height},{left},{top},{width},{height},{scale}:" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels)) : null;
        if (key is not null && _textCache.TryGetValue(key, out var cached) && pixels.AsSpan().SequenceEqual(cached.Pixels))
        { OcrCacheHits++; return cached.Lines; }
        if (key is not null && _textCache.ContainsKey(key))
        { _textCache.Clear(); _textKeys.Clear(); CachedTextBytes = 0; }
        using var writer = new DataWriter();
        writer.WriteBytes(pixels);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8,
            width, height, BitmapAlphaMode.Ignore);
        OcrCalls++;
        var result = await _ocr.RecognizeAsync(bitmap);
        var lines = Array.AsReadOnly(result.Lines.Where(line => line.Words.Count > 0).Select(line => new VisibleTextLine(line.Text,
            new NormalizedRegion(
                (left + line.Words.Min(word => word.BoundingRect.Left) / scale) / frame.Width,
                (top + line.Words.Min(word => word.BoundingRect.Top) / scale) / frame.Height,
                (left + line.Words.Max(word => word.BoundingRect.Right) / scale) / frame.Width,
                (top + line.Words.Max(word => word.BoundingRect.Bottom) / scale) / frame.Height))).ToArray());
        if (key is not null && pixels.Length <= 4 * 1024 * 1024)
        {
            while (_textKeys.Count >= 48 || CachedTextBytes + pixels.Length > 4 * 1024 * 1024)
            {
                var oldest = _textKeys.Dequeue(); CachedTextBytes -= _textCache[oldest].Pixels.Length; _textCache.Remove(oldest);
            }
            _textCache[key] = new(pixels, lines); _textKeys.Enqueue(key); CachedTextBytes += pixels.Length;
        }
        return lines;
    }

    private bool HasGraveyardHeader(PixelFrame frame)
    {
        if (_graveyardHeader is null) return false;
        using var image = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.BgraPixels);
        using var crop = new Mat(image, new Rect((int)(frame.Width * 0.38), (int)(frame.Height * 0.02),
            (int)(frame.Width * 0.25), (int)(frame.Height * 0.08)));
        using var gray = new Mat(); using var normalized = new Mat(); using var scores = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGRA2GRAY);
        Cv2.Resize(gray, normalized, new Size(240, 43), 0, 0, InterpolationFlags.Area);
        Cv2.MatchTemplate(normalized, _graveyardHeader, scores, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(scores, out double _, out double maximum);
        return maximum >= 0.80;
    }

    private static Mat? LoadHeader()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vision-assets", "graveyard-header.png");
        return File.Exists(path) ? Cv2.ImRead(path, ImreadModes.Grayscale) : null;
    }

    public void Dispose() => _graveyardHeader?.Dispose();
}
