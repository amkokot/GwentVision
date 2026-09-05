using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using OpenCvSharp;

namespace GwentCompanion.Platform.Windows.Vision;

/// <summary>Recognizes entire large choice-card rectangles, without producing play events.</summary>
public sealed class DeckPlayChoiceRecognizer(IEnumerable<(CardDefinition Card, string Path)> references,
    IReadOnlyList<CardDefinition> catalog)
{
    private CardArtMatcher? _matcher;
    private bool _loaded;
    private readonly HoverTitleIndex _titles = new(catalog);
    public Action<string>? Trace { get; set; }
    public async Task<DeckPlayChoiceReading?> ReadAsync(PixelFrame frame, GwentVisualObservation screen, ScreenStateRecognizer reader)
    {
        var artwork = Read(frame, screen);
        var highlighted = await ReadHighlightedTitleAsync(frame, screen, reader).ConfigureAwait(false);
        return artwork is null
            ? highlighted is null ? null : new([], Complete: false, HighlightedCard: highlighted)
            : artwork with { HighlightedCard = highlighted };
    }

    private async Task<CardDefinition?> ReadHighlightedTitleAsync(PixelFrame frame, GwentVisualObservation screen, ScreenStateRecognizer reader)
    {
        if (!screen.IsCardSelectionOverlay || !DeckPlayResolutionTracker.IsPlayChoiceHeader(screen.ScreenHeader) ||
            screen.TooltipRegion is not { Left: >= .60, Top: >= .15 and <= .42, Right: >= .80 } tooltip) return null;
        // Highlighted choice details use GWENT's fixed right-side card panel. The
        // coarse beige-component top can begin above or below the dark title strip,
        // so it is only a presence/side gate and must not anchor this crop.
        var header = new NormalizedRegion(.76, .18, .97, .32);
        var lines = await reader.ReadLinesAsync(frame, header, scale: 3, enhance: true, smooth: false).ConfigureAwait(false);
        Trace?.Invoke($"choice tooltip={tooltip} header={header} title=" + string.Join('|', lines.Select(line => line.Text + " " + line.Region)));
        foreach (var line in lines.Where(line => line.Region.Top is >= .20 and <= .28 &&
                     line.Region.Bottom <= .30 && line.Region.Bottom - line.Region.Top >= .012))
        {
            var exact = _titles.Read(line.Text);
            if (exact is not null) return exact;
            // This equivalence is accepted only on the fixed choice-title strip.
            // The resolution tracker separately requires the same selected title
            // on two frames, a compatible printed deck tutor and overlay closure.
            if (_titles.ReadChoiceGlyphEquivalent(line.Text) is { } equivalent) return equivalent;
        }
        return null;
    }
    public DeckPlayChoiceReading? Read(PixelFrame frame, GwentVisualObservation screen)
    {
        if (!screen.IsCardSelectionOverlay || !DeckPlayResolutionTracker.IsPlayChoiceHeader(screen.ScreenHeader)) return null;
        if (!_loaded)
        {
            _loaded = true;
            var targets = catalog.SelectMany(card => DeckPlayResolutionTracker.NamedTargets(card, catalog)).Select(card => card.Id).ToHashSet();
            var art = new List<CardArtReference>();
            foreach (var reference in references.Where(item => targets.Contains(item.Card.Id)))
            {
                using var source = Cv2.ImRead(reference.Path, ImreadModes.Color);
                if (source.Empty()) continue;
                using var bgra = new Mat(); Cv2.CvtColor(source, bgra, ColorConversionCodes.BGR2BGRA);
                var pixels = new byte[bgra.Width * bgra.Height * 4];
                System.Runtime.InteropServices.Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
                art.Add(new(reference.Card, VisualDescriptor.Create(new(bgra.Width, bgra.Height, pixels))));
            }
            if (art.Select(item => item.Card.Id).Distinct().Count() >= 2) _matcher = new(art);
        }
        if (_matcher is null) return null;
        using var input = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.BgraPixels);
        using var gray = new Mat(); using var edges = new Mat();
        Cv2.CvtColor(input, gray, ColorConversionCodes.BGRA2GRAY);
        Cv2.Canny(gray, edges, 45, 110);
        // Hover text can join the highlighted card's outer contour, making its
        // bounding box too wide. Recover independently visible long card edges
        // before closing contours; the same artwork/margin checks still apply.
        using var borderEdges = new Mat();
        Cv2.Canny(gray, borderEdges, 20, 60);
        var verticals = Cv2.HoughLinesP(borderEdges, 1, Math.PI / 180, 70, frame.Height * .27, frame.Height * .025)
            .Where(line => Math.Abs(line.P1.X - line.P2.X) <= frame.Width * .025 &&
                Math.Min(line.P1.Y,line.P2.Y) >= frame.Height * .15 && Math.Max(line.P1.Y,line.P2.Y) <= frame.Height * .82)
            .OrderBy(line => (line.P1.X + line.P2.X) / 2.0).ToArray();
        var columns = new List<Rect>();
        foreach (var line in verticals)
        {
            var r = new Rect(Math.Min(line.P1.X,line.P2.X),Math.Min(line.P1.Y,line.P2.Y),
                Math.Abs(line.P1.X-line.P2.X)+1,Math.Abs(line.P1.Y-line.P2.Y)+1);
            if (columns.Count > 0 && r.X - columns[^1].Right < frame.Width * .02)
            {
                var previous = columns[^1];
                columns[^1] = new(previous.X, Math.Min(previous.Y,r.Y),Math.Max(previous.Right,r.Right)-previous.X,
                    Math.Max(previous.Bottom,r.Bottom)-Math.Min(previous.Y,r.Y));
            }
            else columns.Add(r);
        }
        var edged = new List<Rect>();
        for (var i = 0; i < columns.Count; i++)
        for (var j = i+1; j < columns.Count; j++)
        {
            var l = columns[i]; var r = columns[j];
            var top = Math.Min(l.Y,r.Y); var bottom = Math.Max(l.Bottom,r.Bottom);
            if (Math.Abs(l.Y-r.Y) > frame.Height*.055 && Math.Abs(l.Bottom-r.Bottom) > frame.Height*.055 ||
                Math.Min(l.Height,r.Height) < (bottom-top)*.70) continue;
            edged.Add(new(l.X,top,r.Right-l.X,bottom-top));
        }
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.MorphologyEx(edges, edges, MorphTypes.Close, kernel);
        Cv2.FindContours(edges, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        var regions = contours.Select(Cv2.BoundingRect).Concat(edged).Where(rect =>
            rect.Width >= frame.Width * .12 && rect.Width <= frame.Width * .22 &&
            rect.Height >= frame.Height * .38 && rect.Height <= frame.Height * .58 &&
            rect.Y >= frame.Height * .15 && rect.Bottom <= frame.Height * .82 &&
            rect.Width / (double)rect.Height is > .5 and < .8).OrderByDescending(rect => rect.Width * rect.Height).ToArray();
        var distinct = new List<Rect>();
        foreach (var rect in regions)
            if (!distinct.Any(other => Math.Abs(other.X - rect.X) < frame.Width * .04)) distinct.Add(rect);
        if (distinct.Count is < 1 or > 5) return null;
        var cards = new List<CardDefinition>();
        var complete = true;
        foreach (var rect in distinct.OrderBy(rect => rect.X))
        {
            var ranked = _matcher.RankAligned(frame, new(rect.X / (double)frame.Width, rect.Y / (double)frame.Height,
                rect.Right / (double)frame.Width, rect.Bottom / (double)frame.Height));
            if (ranked.Count < 2 || ranked[0].Distance > .40 || ranked[1].Distance - ranked[0].Distance < .25)
                complete = false;
            else cards.Add(ranked[0].Card);
        }
        return new(cards, complete);
    }
}
