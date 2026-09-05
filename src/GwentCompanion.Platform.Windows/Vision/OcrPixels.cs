using GwentCompanion.Core.Vision;

namespace GwentCompanion.Platform.Windows.Vision;

public static class OcrPixels
{
    // Exactly the previous nearest-neighbor grayscale transform, but convert each
    // source pixel once instead of scale*scale times. No interpolation/threshold change.
    public static byte[] Build(PixelFrame frame, NormalizedRegion region, int scale, bool enhance, bool whiteLetterMask)
    {
        if (scale is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(scale));
        var left = region.PixelLeft(frame.Width); var top = region.PixelTop(frame.Height);
        var columns = region.PixelRight(frame.Width) - left; var rows = region.PixelBottom(frame.Height) - top;
        var stride = checked(columns * scale * 4); var pixels = new byte[checked(stride * rows * scale)];
        for (var y = 0; y < rows; y++)
        {
            var row = y * scale * stride;
            for (var x = 0; x < columns; x++)
            {
                var c = frame.GetPixel(left + x, top + y); var gray = (c.Red + c.Green + c.Blue) / 3;
                var value = (byte)(whiteLetterMask ? Math.Min(c.Red, Math.Min(c.Green, c.Blue)) >= 145 ? 0 : 255 :
                    enhance ? Math.Clamp(gray * 2 - 75, 0, 255) : gray);
                for (var repeat = 0; repeat < scale; repeat++)
                { var offset = row + (x * scale + repeat) * 4; pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value; pixels[offset + 3] = 255; }
            }
            for (var repeat = 1; repeat < scale; repeat++) pixels.AsSpan(row, stride).CopyTo(pixels.AsSpan(row + repeat * stride, stride));
        }
        return pixels;
    }
}
