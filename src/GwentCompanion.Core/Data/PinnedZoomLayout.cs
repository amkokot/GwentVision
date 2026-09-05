namespace GwentCompanion.Core.Data;

public readonly record struct ZoomRect(double X, double Y, double Width, double Height);
public sealed record PinnedZoomLayout(ZoomRect Image, ZoomRect SourcePixels, ZoomRect Lens, ZoomRect Panel)
{
    public const double Magnification = 3;
    public static ZoomRect Fit(double width, double height, double imageWidth, double imageHeight)
    {
        if (new[] { width, height, imageWidth, imageHeight }.Any(value => !double.IsFinite(value) || value <= 0)) return default;
        var scale = Math.Min(width / imageWidth, height / imageHeight);
        return new((width - imageWidth * scale) / 2, (height - imageHeight * scale) / 2, imageWidth * scale, imageHeight * scale);
    }
    public static PinnedZoomLayout? At(double width, double height, double imageWidth, double imageHeight, double x, double y)
    {
        var image = Fit(width, height, imageWidth, imageHeight);
        if (width < 80 || height < 80 || image.Width <= 0 || !double.IsFinite(x) || !double.IsFinite(y) ||
            x < image.X || y < image.Y || x > image.X + image.Width || y > image.Y + image.Height) return null;
        var panelWidth = Math.Min(260, Math.Min(width - 16, image.Width * Magnification));
        var panelHeight = Math.Min(175, Math.Min(height - 16, image.Height * Magnification));
        var scale = image.Width / imageWidth;
        var sourceWidth = panelWidth / (scale * Magnification); var sourceHeight = panelHeight / (scale * Magnification);
        var source = new ZoomRect(Math.Clamp((x - image.X) / scale - sourceWidth / 2, 0, Math.Max(0, imageWidth - sourceWidth)),
            Math.Clamp((y - image.Y) / scale - sourceHeight / 2, 0, Math.Max(0, imageHeight - sourceHeight)), sourceWidth, sourceHeight);
        var panel = new ZoomRect(x > width / 2 ? 8 : width - panelWidth - 8, y > height / 2 ? 8 : height - panelHeight - 8, panelWidth, panelHeight);
        return new(image, source, new(image.X + source.X * scale, image.Y + source.Y * scale, source.Width * scale, source.Height * scale), panel);
    }
}
