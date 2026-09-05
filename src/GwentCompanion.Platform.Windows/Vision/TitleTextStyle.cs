using GwentCompanion.Core.Vision;

namespace GwentCompanion.Platform.Windows.Vision;

/// <summary>Optional bright-title preprocessing; never used for prose, HUD digits or artwork.</summary>
public static class TitleTextStyle
{
    public static bool IsBrightTitleLine(PixelFrame frame, NormalizedRegion region)
    {
        if (region.Bottom - region.Top < .012) return false;
        var white = 0; var dark = 0; var total = 0;
        for (var y = region.PixelTop(frame.Height); y < region.PixelBottom(frame.Height); y++)
        for (var x = region.PixelLeft(frame.Width); x < region.PixelRight(frame.Width); x++)
        {
            var color = frame.GetPixel(x, y);
            if (Math.Min(color.Red, Math.Min(color.Green, color.Blue)) >= 170) white++;
            if (Math.Max(color.Red, Math.Max(color.Green, color.Blue)) < 120) dark++;
            total++;
        }
        return total > 0 && white / (double)total is > .075 and < .60 && dark >= total * .50;
    }

    public static bool CoversBrightText(PixelFrame frame, NormalizedRegion line, NormalizedRegion strip)
    {
        // Do not accept a valid short suffix when OCR silently dropped a prefix.
        // Count actual bright strokes outside the returned word bounds; no vocabulary
        // or expected-deck assumption supplies the missing letters.
        var top = line.PixelTop(frame.Height); var bottom = line.PixelBottom(frame.Height);
        var pad = Math.Max(2, (int)Math.Ceiling(frame.Width * .0015));
        var left = line.PixelLeft(frame.Width) - pad; var right = line.PixelRight(frame.Width) + pad;
        var consecutive = 0;
        for (var x = strip.PixelLeft(frame.Width); x < strip.PixelRight(frame.Width); x++)
        {
            if (x >= left && x <= right) { consecutive = 0; continue; }
            var white = 0;
            for (var y = top; y < bottom; y++)
            {
                var color = frame.GetPixel(x, y);
                if (Math.Min(color.Red, Math.Min(color.Green, color.Blue)) >= 170) white++;
            }
            consecutive = white >= Math.Max(2, (bottom - top) * .15) ? consecutive + 1 : 0;
            if (consecutive >= 2) return false;
        }
        return true;
    }

    public static byte[]? TryBuild(PixelFrame frame, NormalizedRegion region, int scale)
    {
        if (scale is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(scale));
        var left = region.PixelLeft(frame.Width); var top = region.PixelTop(frame.Height);
        var width = region.PixelRight(frame.Width) - left; var height = region.PixelBottom(frame.Height) - top;
        if (width < 8 || height < 8 || width > frame.Width * .40 || height > frame.Height * .25) return null;
        var tones = new byte[width * height]; var histogram = new int[256];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var color = frame.GetPixel(left + x, top + y);
            // White/cream title strokes survive all channels; saturated faction colours
            // and glow do not. Keep intermediate edge shades instead of a hard mask.
            var tone = Math.Min(color.Red, Math.Min(color.Green, color.Blue));
            tones[y * width + x] = tone; histogram[tone]++;
        }
        int Percentile(double fraction)
        {
            var count = 0; var target = (int)Math.Ceiling(tones.Length * fraction);
            for (var i = 0; i < histogram.Length; i++) if ((count += histogram[i]) >= target) return i;
            return 255;
        }
        var dark = Percentile(.70); var light = Percentile(.98);
        // Abstain on parchment, fades and flat patches. This is a pixel transform,
        // not proof of a title: callers still have to validate layout and identity.
        if (dark > 110 || light < 150 || light - dark < 70) return null;
        var stride = checked(width * scale * 4); var pixels = new byte[checked(stride * height * scale)];
        for (var y = 0; y < height; y++)
        {
            var row = y * scale * stride;
            for (var x = 0; x < width; x++)
            {
                var value = (byte)(255 - Math.Clamp((tones[y * width + x] - dark) * 255 / (light - dark), 0, 255));
                for (var repeat = 0; repeat < scale; repeat++)
                {
                    var offset = row + (x * scale + repeat) * 4;
                    pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value; pixels[offset + 3] = 255;
                }
            }
            for (var repeat = 1; repeat < scale; repeat++) pixels.AsSpan(row, stride).CopyTo(pixels.AsSpan(row + repeat * stride, stride));
        }
        return pixels;
    }
}
