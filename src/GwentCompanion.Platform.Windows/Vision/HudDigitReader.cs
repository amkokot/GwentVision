using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Vision;
using OpenCvSharp;

namespace GwentCompanion.Platform.Windows.Vision;

public sealed record HudGlyph(char Digit, double Aspect, string Mask, bool Emit = true);
public enum HudNumberTone { Unknown, Base, Boosted, Damaged }
public sealed record HudNumberReading(int Value, HudNumberTone Tone);
/// <summary>Small fixed-font scoreboard reader. Glyph shape, not a guessed score or board sum, determines the value.</summary>
public sealed class HudDigitReader(IReadOnlyList<HudGlyph> templates, bool boardPower = false)
{
    private sealed record CompiledGlyph(char Digit, double Aspect, byte[] Mask, bool Emit);
    private readonly CompiledGlyph[] _templates = templates.Select(t => new CompiledGlyph(t.Digit, t.Aspect, Convert.FromBase64String(t.Mask), t.Emit)).ToArray();
    public static Action<string>? Trace { get; set; }
    private static readonly Lazy<HudDigitReader> Default = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vision-assets", "score-digits.json");
        return new(File.Exists(path) ? JsonSerializer.Deserialize<HudGlyph[]>(File.ReadAllText(path)) ?? [] : []);
    });
    private static readonly Lazy<HudDigitReader> Coins = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vision-assets", "coin-digits.json");
        return new(File.Exists(path) ? JsonSerializer.Deserialize<HudGlyph[]>(File.ReadAllText(path)) ?? [] : []);
    });
    private static readonly Lazy<HudDigitReader> Rank = new(() =>
    {
        HudGlyph[] Load(string name)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "vision-assets", name);
            return File.Exists(path) ? JsonSerializer.Deserialize<HudGlyph[]>(File.ReadAllText(path)) ?? [] : [];
        }
        // The shield uses the scoreboard family, with a separately retained rank-3
        // glyph because its narrower cut otherwise resembles the scoreboard zero.
        return new(Load("score-digits.json").Concat(Load("rank-digits.json")).ToArray());
    });
    public static int? ReadDefault(PixelFrame frame, NormalizedRegion region) => Default.Value.Read(frame, region);
    public static int? ReadCoin(PixelFrame frame, NormalizedRegion region) => Coins.Value.Read(frame, region);
    public static int? ReadRank(PixelFrame frame, NormalizedRegion region) => Rank.Value.Read(frame, region);
    private sealed record Shape(double Aspect, byte[] Pixels, HudNumberTone Tone);
    public int? Read(PixelFrame frame, NormalizedRegion region) => ReadDetailed(frame, region)?.Value;
    public HudNumberReading? ReadDetailed(PixelFrame frame, NormalizedRegion region)
    {
        var shapes = Extract(frame, region, boardPower); if (shapes.Count is < 1 or > 4 || _templates.Length == 0) return null;
        var digits = new List<char>();
        foreach (var shape in shapes)
        {
            var ranked = _templates.Select(template => (template.Digit, template.Emit, Error: Distance(shape, template)))
                .GroupBy(item => item.Digit).Select(group => group.MinBy(item => item.Error)).OrderBy(item => item.Error).ToArray();
            Trace?.Invoke("Glyph ranking " + string.Join(", ", ranked.Take(2).Select(t => $"{t.Digit}:{t.Error:F3}")));
            if (!ranked[0].Emit || ranked[0].Error > (boardPower ? .14 : .18) || ranked.Length > 1 && ranked[1].Error - ranked[0].Error < (boardPower ? .04 : .035)) return null;
            digits.Add(ranked[0].Digit);
        }
        if (!int.TryParse(new string(digits.ToArray()), out var value)) return null;
        var tones = shapes.Select(shape => shape.Tone).Where(tone => tone != HudNumberTone.Unknown).Distinct().ToArray();
        return new(value, tones.Length == 1 ? tones[0] : HudNumberTone.Unknown);
    }
    public static IReadOnlyList<HudGlyph> Train(PixelFrame frame, NormalizedRegion region, string label, bool boardPower = false)
    {
        var shapes = Extract(frame, region, boardPower);
        if (shapes.Count != label.Length) throw new InvalidOperationException($"Glyph segmentation: {shapes.Count} components for {label}");
        return shapes.Select((shape, index) => new HudGlyph(label[index], shape.Aspect, Convert.ToBase64String(shape.Pixels))).ToArray();
    }
    private static double Distance(Shape shape, CompiledGlyph template)
    {
        var reference = template.Mask; var best = double.MaxValue;
        if (reference.Length != 384) return best;
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            double error = 0;
            for (var y = 0; y < 24; y++)
            for (var x = 0; x < 16; x++)
            {
                var xx = x + dx; var yy = y + dy;
                var value = xx < 0 || xx >= 16 || yy < 0 || yy >= 24 ? 0 : shape.Pixels[yy * 16 + xx];
                error += Math.Abs(reference[y * 16 + x] - value) / 255.0;
            }
            best = Math.Min(best, error / 384);
        }
        return best + Math.Abs(shape.Aspect - template.Aspect) * .25;
    }
    private static IReadOnlyList<Shape> Extract(PixelFrame frame, NormalizedRegion region, bool boardPower)
    {
        var x = region.PixelLeft(frame.Width); var y = region.PixelTop(frame.Height);
        var width = region.PixelRight(frame.Width) - x; var height = region.PixelBottom(frame.Height) - y;
        if (width <= 0 || height <= 0) return [];
        using var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        for (var yy = 0; yy < height; yy++)
        for (var xx = 0; xx < width; xx++)
        {
            var c = frame.GetPixel(x + xx, y + yy);
            var white = Math.Min(c.Red, Math.Min(c.Green, c.Blue)) >= 160;
            if (boardPower) white = Math.Min(c.Red, Math.Min(c.Green, c.Blue)) >= 125 &&
                Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue)) <= 90;
            var gold = c.Red >= 160 && c.Green >= 135 && c.Blue < c.Green * .9;
            var green = c.Green >= 150 && c.Green > c.Red * 1.22 && c.Green > c.Blue * 1.12;
            var red = c.Red >= 155 && c.Red > c.Green * 1.25 && c.Red > c.Blue * 1.2;
            if (boardPower ? white || green || red : white || gold) mask.Set(yy, xx, (byte)255);
        }
        using var labels = new Mat(); using var stats = new Mat(); using var centers = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centers);
        var components = new List<(int Label, Rect Rect)>();
        for (var i = 1; i < count; i++)
        {
            var r = new Rect(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2), stats.At<int>(i, 3));
            Trace?.Invoke($"Glyph component {r}: area {stats.At<int>(i, 4)}, crop {width}x{height}");
            var compactHudGlyph = width <= 30 && height <= 24;
            if (r.Height >= Math.Max(boardPower ? 4 : 0, height * .35) && r.Height <= height * (boardPower ? .96 : .9) && r.Width >= 2 &&
                stats.At<int>(i, 4) >= r.Height * (boardPower || compactHudGlyph ? .8 : 1.5)) components.Add((i, r));
        }
        if (components.Count is < 1 or > 4) return [];
        var result = new List<Shape>();
        foreach (var component in components.OrderBy(item => item.Rect.X))
        {
            var rect = component.Rect;
            using var crop = new Mat(mask, rect); using var resized = new Mat();
            Cv2.Resize(crop, resized, new Size(16, 24), 0, 0, InterpolationFlags.Area);
            var pixels = new byte[384]; System.Runtime.InteropServices.Marshal.Copy(resized.Data, pixels, 0, pixels.Length);
            var whitePixels = 0; var greenPixels = 0; var redPixels = 0;
            if (boardPower)
            {
                for (var yy = rect.Top; yy < rect.Bottom; yy++)
                for (var xx = rect.Left; xx < rect.Right; xx++)
                {
                    if (labels.At<int>(yy, xx) != component.Label) continue;
                    var c = frame.GetPixel(x + xx, y + yy);
                    if (c.Red >= 155 && c.Red > c.Green * 1.25 && c.Red > c.Blue * 1.2) redPixels++;
                    else if (c.Green >= 150 && c.Green > c.Red * 1.22 && c.Green > c.Blue * 1.12) greenPixels++;
                    else if (Math.Min(c.Red, Math.Min(c.Green, c.Blue)) >= 125 &&
                             Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue)) <= 90) whitePixels++;
                }
            }
            var tone = !boardPower ? HudNumberTone.Unknown : redPixels > greenPixels && redPixels > whitePixels ? HudNumberTone.Damaged :
                greenPixels > redPixels && greenPixels > whitePixels ? HudNumberTone.Boosted :
                whitePixels > redPixels && whitePixels > greenPixels ? HudNumberTone.Base : HudNumberTone.Unknown;
            Trace?.Invoke($"Glyph tone {tone}: white={whitePixels}, green={greenPixels}, red={redPixels}");
            result.Add(new((double)rect.Width / rect.Height, pixels, tone));
        }
        return result;
    }
}
