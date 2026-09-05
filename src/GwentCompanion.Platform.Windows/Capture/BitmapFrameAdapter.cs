using System.Windows.Media;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Platform.Windows.Capture;

public static class BitmapFrameAdapter
{
    public static PixelFrame ToPixelFrame(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return new PixelFrame(converted.PixelWidth, converted.PixelHeight, pixels);
    }
}
