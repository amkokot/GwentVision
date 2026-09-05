using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal static class VisionFrameCompatibilityTests
{
    public static void Run()
    {
        var standard = Frame(1280, 720, 58, 71, 84);
        var unchanged = VisionFrameNormalizer.Prepare(standard);
        Check(unchanged.Supported && ReferenceEquals(standard, unchanged.Frame) && !unchanged.Downscaled,
            "A normal 720p frame should pass without a copy.");

        foreach (var color in new[] { (R: (byte)35, G: (byte)60, B: (byte)42), (R: (byte)112, G: (byte)72, B: (byte)40),
                     (R: (byte)48, G: (byte)55, B: (byte)105) })
            Check(VisionFrameNormalizer.Prepare(Frame(1280, 720, color.R, color.G, color.B)).Supported,
                "Geometry support must not depend on the selected board palette.");
        var textured = TexturedFrame(1280, 720);
        Check(VisionFrameNormalizer.Prepare(textured).Supported,
            "Geometry support must not depend on board texture.");
        Paint(textured, 1222, 14, 34, 24, 235, 235, 235);
        var texturedBoard = new GwentVisualStateDetector().Analyze(textured);
        Check(texturedBoard.MatchHudVisible == true && !texturedBoard.IsCardSelectionOverlay,
            "A dark textured battlefield with an authenticated HUD must not become a global-darkness overlay.");

        var fourK = VisionFrameNormalizer.Prepare(Frame(3840, 2160, 65, 70, 75));
        Check(fourK.Supported && fourK.Downscaled && fourK.Frame.Width == 1920 && fourK.Frame.Height == 1080,
            "4K analysis should be capped at 1080p.");

        var letterboxed = Frame(2560, 1080, 0, 0, 0);
        Paint(letterboxed, 320, 0, 1920, 1080, 70, 82, 94);
        var cropped = VisionFrameNormalizer.Prepare(letterboxed);
        Check(cropped.Supported && cropped.CroppedLetterbox && cropped.Frame.Width == 1920 && cropped.Frame.Height == 1080,
            "Black ultrawide pillar boxes should be removed conservatively.");

        var stretched = VisionFrameNormalizer.Prepare(Frame(2560, 1080, 70, 82, 94));
        Check(!stretched.Supported && stretched.Warning?.Contains("Recognition paused", StringComparison.Ordinal) == true,
            "An unvalidated ultrawide layout must fail closed.");
        Check(!VisionFrameNormalizer.Prepare(Frame(800, 450, 70, 82, 94)).Supported,
            "Too-small frames must fail closed.");

        var screen = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null, MatchHudVisible: true);
        Check(CardVisionPipeline.ShouldScanBoard(screen, true), "A requested, authenticated board should be scanned.");
        Check(!CardVisionPipeline.ShouldScanBoard(screen with { MatchHudVisible = false }, true),
            "A missing match HUD must prevent an authoritative board scan.");
        Check(!CardVisionPipeline.ShouldScanBoard(screen with { FrameGeometrySupported = false }, true),
            "Unsupported geometry must prevent an authoritative board scan.");
        Check(!CardVisionPipeline.ShouldScanBoard(screen with { IsCardSelectionOverlay = true }, true),
            "A selection overlay must prevent an authoritative board scan.");

        Console.WriteLine("PASS frame compatibility: palette-independent 16:9 resolutions, bounded 4K, black-bar recovery, and fail-closed unknown layouts.");
    }

    private static PixelFrame Frame(int width, int height, byte red, byte green, byte blue)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = blue; pixels[i + 1] = green; pixels[i + 2] = red; pixels[i + 3] = 255;
        }
        return new(width, height, pixels);
    }

    private static PixelFrame TexturedFrame(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        uint state = 0x9E3779B9;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            state = state * 1664525 + 1013904223;
            pixels[i] = (byte)(8 + (state & 15));
            pixels[i + 1] = (byte)(10 + ((state >> 8) & 17));
            pixels[i + 2] = (byte)(9 + ((state >> 16) & 16));
            pixels[i + 3] = 255;
        }
        return new(width, height, pixels);
    }

    private static void Paint(PixelFrame frame, int left, int top, int width, int height, byte red, byte green, byte blue)
    {
        for (var y = top; y < top + height; y++)
        for (var x = left; x < left + width; x++)
        {
            var offset = (y * frame.Width + x) * 4;
            frame.BgraPixels[offset] = blue; frame.BgraPixels[offset + 1] = green;
            frame.BgraPixels[offset + 2] = red; frame.BgraPixels[offset + 3] = 255;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
