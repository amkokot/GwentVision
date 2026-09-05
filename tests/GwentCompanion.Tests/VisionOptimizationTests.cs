using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;
using OpenCvSharp;

internal static class VisionOptimizationTests
{
    public static void CheckAppearanceDecoders(string root)
    {
        var different = 0; var count = 0; double maximum = 0;
        foreach (var file in new[] { "portraits", "premium-frames", "observed-art" }.SelectMany(folder =>
                     Directory.GetFiles(Path.Combine(root, "GwentCompanion/cache", folder), "*.jpg", SearchOption.AllDirectories)))
        {
            using var cv = Cv2.ImRead(file, ImreadModes.Color); if (cv.Empty()) continue;
            using var bgra = new Mat(); Cv2.CvtColor(cv, bgra, ColorConversionCodes.BGR2BGRA);
            var bytes = new byte[bgra.Width * bgra.Height * 4]; System.Runtime.InteropServices.Marshal.Copy(bgra.Data, bytes, 0, bytes.Length);
            var a = VisualDescriptor.Create(new PixelFrame(bgra.Width, bgra.Height, bytes));
            using var stream = File.OpenRead(file);
            var b = VisualDescriptor.Create(BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]));
            var delta = a.DistanceTo(b); count++; maximum = Math.Max(maximum, delta); if (delta != 0) different++;
        }
        Console.WriteLine($"Appearance decoder comparison: {count} images, {different} non-identical descriptors, max distance {maximum:R}");
        if (different != 0) throw new InvalidOperationException("Appearance reuse is not lossless");
    }
    public static async Task RunAsync(string root)
    {
        var random = new Random(25); var data = new byte[97 * 61 * 4]; random.NextBytes(data);
        var frame = new PixelFrame(97, 61, data);
        foreach (var region in new[] { new NormalizedRegion(0, 0, 1, 1), new NormalizedRegion(.12, .19, .72, .87) })
        foreach (var scale in new[] { 1, 2, 3, 4 })
        foreach (var enhance in new[] { false, true })
        foreach (var white in new[] { false, true })
        {
            var actual = OcrPixels.Build(frame, region, scale, enhance, white);
            var left = region.PixelLeft(frame.Width); var top = region.PixelTop(frame.Height);
            var width = (region.PixelRight(frame.Width) - left) * scale; var height = (region.PixelBottom(frame.Height) - top) * scale;
            var expected = new byte[width * height * 4];
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            {
                var c = frame.GetPixel(left + x / scale, top + y / scale); var gray = (c.Red + c.Green + c.Blue) / 3;
                var value = (byte)(white ? Math.Min(c.Red, Math.Min(c.Green, c.Blue)) >= 145 ? 0 : 255 : enhance ? Math.Clamp(gray * 2 - 75, 0, 255) : gray);
                var i = (y * width + x) * 4; expected[i] = expected[i + 1] = expected[i + 2] = value; expected[i + 3] = 255;
            }
            if (!actual.SequenceEqual(expected)) throw new InvalidOperationException("OCR preprocessing changed pixels.");
        }
        using var source = File.OpenRead(Path.Combine(root, "GwentCompanion/sessions/20260828-112523/frame-000283-112553873.jpg"));
        var recorded = BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
        using var reader = new ScreenStateRecognizer();
        var clock = Stopwatch.StartNew();
        foreach (var region in new[] { new NormalizedRegion(.27, 0, .75, .125), new NormalizedRegion(.79, .1, .96, .45), new NormalizedRegion(.90, .315, .995, .385) })
        foreach (var smooth in new[] { false, true })
        {
            reader.UseTextCache = false;
            var expected = JsonSerializer.Serialize(await reader.ReadLinesAsync(recorded, region, smooth: smooth));
            reader.UseTextCache = true;
            for (var i = 0; i < 3; i++)
                if (JsonSerializer.Serialize(await reader.ReadLinesAsync(recorded, region, smooth: smooth)) != expected) throw new InvalidOperationException("Cached OCR changed text/bounds.");
        }
        if (reader.OcrCacheHits < 12 || reader.CachedTextBytes > 4 * 1024 * 1024) throw new InvalidOperationException("OCR reuse/budget failed.");
        Console.WriteLine($"PASS exact OCR pixels and recorded text/bounds; calls={reader.OcrCalls}, hits={reader.OcrCacheHits}, bytes={reader.CachedTextBytes}; {clock.Elapsed.TotalSeconds:F2}s.");
        await RecordedScoreTests.HeldOutScoresAsync(root);
    }
}
