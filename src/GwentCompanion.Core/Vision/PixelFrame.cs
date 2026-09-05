namespace GwentCompanion.Core.Vision;

public sealed class PixelFrame
{
    public PixelFrame(int width, int height, byte[] bgraPixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive.");
        }

        ArgumentNullException.ThrowIfNull(bgraPixels);
        if (bgraPixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("The BGRA buffer length does not match the frame dimensions.", nameof(bgraPixels));
        }

        Width = width;
        Height = height;
        BgraPixels = bgraPixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] BgraPixels { get; }

    public PixelColor GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        var offset = (y * Width + x) * 4;
        return new PixelColor(
            BgraPixels[offset + 2],
            BgraPixels[offset + 1],
            BgraPixels[offset]);
    }
}

public readonly record struct PixelColor(byte Red, byte Green, byte Blue);

public readonly record struct NormalizedRegion(double Left, double Top, double Right, double Bottom)
{
    public int PixelLeft(int width) => Math.Clamp((int)Math.Round(Left * width), 0, width - 1);
    public int PixelTop(int height) => Math.Clamp((int)Math.Round(Top * height), 0, height - 1);
    public int PixelRight(int width) => Math.Clamp((int)Math.Round(Right * width), 1, width);
    public int PixelBottom(int height) => Math.Clamp((int)Math.Round(Bottom * height), 1, height);
}
