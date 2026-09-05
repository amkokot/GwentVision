using System.Runtime.InteropServices;
using GwentCompanion.Core.Vision;
using OpenCvSharp;

namespace GwentCompanion.Platform.Windows.Vision;

public sealed record VisionFramePreparation(
    PixelFrame Frame,
    bool Supported,
    string? Warning = null,
    bool CroppedLetterbox = false,
    bool Downscaled = false);

/// <summary>
/// Keeps live recognition inside its validated 16:9 coordinate system. Ordinary
/// resolution changes are scaled once, while clearly black letter/pillar boxes
/// are removed. Unknown layouts fail closed instead of producing deck evidence
/// from the wrong screen regions.
/// </summary>
public static class VisionFrameNormalizer
{
    public const int MinimumWidth = 960;
    public const int MinimumHeight = 540;
    public const int MaximumWidth = 1920;
    public const int MaximumHeight = 1080;
    public const double TargetAspectRatio = 16d / 9d;
    private const double AspectTolerance = .035;
    private const double MinimumBarFraction = .02;
    private const double RequiredDarkBarRatio = .90;

    public static VisionFramePreparation Prepare(PixelFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var viewport = new Viewport(0, 0, frame.Width, frame.Height);
        var aspect = frame.Width / (double)frame.Height;
        var cropped = false;

        if (Math.Abs(aspect - TargetAspectRatio) > AspectTolerance)
        {
            if (!TryFindLetterboxedViewport(frame, out viewport))
            {
                return new(frame, false,
                    $"Recognition paused: the captured GWENT client is {frame.Width}×{frame.Height} " +
                    $"({aspect:F2}:1). Use a 16:9 game resolution or a mode with black letterboxing.");
            }

            cropped = true;
        }

        if (viewport.Width < MinimumWidth || viewport.Height < MinimumHeight)
        {
            return new(frame, false,
                $"Recognition paused: the usable GWENT image is {viewport.Width}×{viewport.Height}. " +
                $"Use at least {MinimumWidth}×{MinimumHeight}.");
        }

        var scale = Math.Min(1d, Math.Min(MaximumWidth / (double)viewport.Width, MaximumHeight / (double)viewport.Height));
        var outputWidth = Math.Max(1, (int)Math.Round(viewport.Width * scale));
        var outputHeight = Math.Max(1, (int)Math.Round(viewport.Height * scale));
        var downscaled = outputWidth != viewport.Width || outputHeight != viewport.Height;
        if (!cropped && !downscaled) return new(frame, true);

        using var source = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.BgraPixels);
        using var region = new Mat(source, new Rect(viewport.Left, viewport.Top, viewport.Width, viewport.Height));
        using var normalized = new Mat();
        if (downscaled)
            Cv2.Resize(region, normalized, new Size(outputWidth, outputHeight), 0, 0, InterpolationFlags.Area);
        else
            region.CopyTo(normalized);

        var pixels = new byte[checked(normalized.Width * normalized.Height * 4)];
        Marshal.Copy(normalized.Data, pixels, 0, pixels.Length);
        return new(new PixelFrame(normalized.Width, normalized.Height, pixels), true, CroppedLetterbox: cropped,
            Downscaled: downscaled);
    }

    private static bool TryFindLetterboxedViewport(PixelFrame frame, out Viewport viewport)
    {
        var aspect = frame.Width / (double)frame.Height;
        if (aspect > TargetAspectRatio)
        {
            var width = Math.Min(frame.Width, (int)Math.Round(frame.Height * TargetAspectRatio));
            var left = (frame.Width - width) / 2;
            var rightWidth = frame.Width - left - width;
            if (left >= frame.Width * MinimumBarFraction && rightWidth >= frame.Width * MinimumBarFraction &&
                DarkRatio(frame, 0, 0, left, frame.Height) >= RequiredDarkBarRatio &&
                DarkRatio(frame, left + width, 0, rightWidth, frame.Height) >= RequiredDarkBarRatio)
            {
                viewport = new(left, 0, width, frame.Height);
                return true;
            }
        }
        else
        {
            var height = Math.Min(frame.Height, (int)Math.Round(frame.Width / TargetAspectRatio));
            var top = (frame.Height - height) / 2;
            var bottomHeight = frame.Height - top - height;
            if (top >= frame.Height * MinimumBarFraction && bottomHeight >= frame.Height * MinimumBarFraction &&
                DarkRatio(frame, 0, 0, frame.Width, top) >= RequiredDarkBarRatio &&
                DarkRatio(frame, 0, top + height, frame.Width, bottomHeight) >= RequiredDarkBarRatio)
            {
                viewport = new(0, top, frame.Width, height);
                return true;
            }
        }

        viewport = default;
        return false;
    }

    private static double DarkRatio(PixelFrame frame, int left, int top, int width, int height)
    {
        if (width <= 0 || height <= 0) return 0;
        var stepX = Math.Max(1, width / 48);
        var stepY = Math.Max(1, height / 48);
        var dark = 0;
        var samples = 0;
        for (var y = top; y < top + height; y += stepY)
        for (var x = left; x < left + width; x += stepX)
        {
            var color = frame.GetPixel(x, y);
            var luminance = color.Red * .2126 + color.Green * .7152 + color.Blue * .0722;
            if (luminance <= 30 && Math.Max(color.Red, Math.Max(color.Green, color.Blue)) <= 45) dark++;
            samples++;
        }

        return samples == 0 ? 0 : dark / (double)samples;
    }

    private readonly record struct Viewport(int Left, int Top, int Width, int Height);
}
