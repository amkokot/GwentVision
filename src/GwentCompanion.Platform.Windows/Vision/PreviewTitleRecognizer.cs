using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using System.Globalization;
using System.Text;

namespace GwentCompanion.Platform.Windows.Vision;

/// <summary>Exact visible preview titles complement artwork; no fuzzy nearest-name guesses.</summary>
public sealed class PreviewTitleRecognizer(IEnumerable<CardDefinition> cards)
{
    public Action<string>? Trace { get; set; }
    public bool HasUnresolvedHeader { get; private set; }
    public bool UseTitleStyleFallback { get; set; } = true;
    private readonly Dictionary<string, CardDefinition> _names = cards.DistinctBy(card => card.Id)
        .Where(card => card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact)
        .GroupBy(card => Normalize(card.Name)).Where(group => group.Count() == 1 && group.Key.Length >= 5)
        .ToDictionary(group => group.Key, group => group.Single());
    // The game's capitals render V/U and I/L similarly. Only a complete, long,
    // uniquely matching title may use these equivalences, and its event still
    // needs a second frame.
    private readonly Dictionary<string, CardDefinition> _glyphNames = cards.DistinctBy(card => card.Id)
        .Where(card => card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact)
        .GroupBy(card => NormalizeGlyphs(card.Name))
        .Where(group => group.Count() == 1 && group.Key.Length >= 12)
        .ToDictionary(group => group.Key, group => group.Single());
    private readonly HashSet<string> _categories = cards.SelectMany(card => card.Categories)
        .Select(Normalize).ToHashSet(StringComparer.Ordinal);

    public async Task<IReadOnlyList<CardSighting>> RecognizeAsync(PixelFrame frame, GwentVisualObservation screen,
        ScreenStateRecognizer reader, IReadOnlyList<CardSighting>? accepted = null)
    {
        HasUnresolvedHeader = false;
        if (screen.IsCardSelectionOverlay) return [];
        var result = new List<CardSighting>();
        foreach (var (side, title, art) in new[]
        {
            // Long abilities move the entire tooltip down without moving its preview card.
            (PlayerSide.Opponent, new NormalizedRegion(.635, .137, .84, .355), new NormalizedRegion(.815, .136, .914, .399)),
            (PlayerSide.User, new NormalizedRegion(.635, .413, .85, .625), new NormalizedRegion(.833, .413, .934, .665)),
        })
        {
            if (accepted?.Any(item => item.Side == side && item.Source == CardSightSource.PlayPreview) == true) continue;
            var boundaries = (from left in new[] { .800, .815, .833 }
                from width in new[] { .092, .096, .100, .104 }
                select EnlargedCardPlayDetector.Score(frame, art with { Left = left, Right = left + width })).ToArray();
            Trace?.Invoke($"{side} strongest raw boundary: {boundaries.MaxBy(candidate => candidate.Score)}");
            var boundary = boundaries
                .Where(candidate =>
                    (candidate.EdgeClosure >= .25 && candidate.BoundaryContrast >= .12 && candidate.InteriorTexture >= .07) ||
                    // Dark bronze borders can blend into this battlefield. Keep a texture
                    // requirement and still require an exact white-letter adjacent title.
                    (candidate.EdgeClosure >= .22 && candidate.BoundaryContrast >= .10 && candidate.InteriorTexture >= .09))
                .MaxBy(candidate => candidate.Score);
            var weakBorder = boundary is null;
            if (weakBorder && screen.HasCardTooltip)
                boundary = boundaries.Where(candidate => candidate.EdgeClosure >= .13 && candidate.BoundaryContrast >= .12 && candidate.InteriorTexture >= .09)
                    .MaxBy(candidate => candidate.Score);
            Trace?.Invoke($"{side} preview boundary: {boundary}");
            if (boundary is null) continue;
            bool EligibleHeader(NormalizedRegion region) => IsPreviewHeader(frame, region) && HasAdjacentTooltip(frame, region, boundary.Region) &&
                !HasHeaderAbove(frame, region, boundary.Region) &&
                (!weakBorder || region.Top >= art.Top && region.Top - art.Top < .045);
            CardDefinition[] names = [];
            var glyphConfusion = false;
            var styledTitle = false;
            var sawHeader = false;
            var windows = new[] { title with { Left = .645, Right = side == PlayerSide.User ? .82 : .81,
                Bottom = side == PlayerSide.User ? .47 : .19 }, title };
            foreach (var window in windows)
            {
                var lines = await reader.ReadLinesAsync(frame, window).ConfigureAwait(false);
                if (lines.Count == 0 && window == title)
                    lines = await reader.ReadLinesAsync(frame, window, 4, enhance: false).ConfigureAwait(false);
                foreach (var line in lines) Trace?.Invoke($"{side} text: {line.Text} header={IsPreviewHeader(frame, line.Region)} {line.Region}");
                sawHeader |= lines.Any(line => IsPreviewHeader(frame, line.Region));
                // Category text is white on the same dark header as the title.
                // In particular, "Machine, Siege Engine" must never become Siege
                // after OCR retries crop away the surrounding words.
                var headerLines = lines.Where(line => EligibleHeader(line.Region) && !IsCategoryLine(line.Text))
                    .OrderBy(line => line.Region.Top).ToArray();
                var top = headerLines.FirstOrDefault()?.Region.Top;
                headerLines = headerLines.Where(line => top is not null && line.Region.Top - top < .009).ToArray();
                names = headerLines
                    .Select(line => Normalize(line.Text)).Distinct().Where(_names.ContainsKey)
                    .Select(name => _names[name]).DistinctBy(card => card.Id).ToArray();
                if (names.Length > 0) break;
                // Stylized capitals can be misread at one raster scale. Re-read the
                // actual header pixels; do not turn a fuzzy nearest name into evidence.
                foreach (var line in headerLines)
                {
                    // OCR can omit a whole stylized first word (e.g. Elven). Re-read
                    // the full adjacent header strip, not only the recognized fragment.
                    var crop = line.Region with { Left = Math.Min(line.Region.Left - .007, boundary.Region.Left - .175),
                        Right = Math.Max(line.Region.Right + .007, boundary.Region.Left - .006),
                        Top = line.Region.Top - .004, Bottom = line.Region.Bottom + .004 };
                    var retry = await reader.ReadLinesAsync(frame, crop, 2, enhance: false).ConfigureAwait(false);
                    var retryTexts = retry.Where(item => EligibleHeader(item.Region) && !IsCategoryLine(item.Text)).Select(item => item.Text).ToList();
                    foreach (var item in retry) Trace?.Invoke("Title retry: " + item.Text);
                    names = retry.Where(item => EligibleHeader(item.Region) && !IsCategoryLine(item.Text))
                        .Select(item => Normalize(item.Text)).Where(_names.ContainsKey)
                        .Select(name => _names[name]).DistinctBy(card => card.Id).ToArray();
                    if (names.Length == 0)
                    {
                        var masked = await reader.ReadLinesAsync(frame, crop, 4, enhance: false, whiteLetterMask: true, smooth: true).ConfigureAwait(false);
                        retryTexts.AddRange(masked.Where(item => EligibleHeader(item.Region) && !IsCategoryLine(item.Text)).Select(item => item.Text));
                        foreach (var item in masked) Trace?.Invoke("Title mask: " + item.Text);
                        names = masked.Where(item => EligibleHeader(item.Region) && !IsCategoryLine(item.Text))
                            .Select(item => Normalize(item.Text)).Where(_names.ContainsKey)
                            .Select(name => _names[name]).DistinctBy(card => card.Id).ToArray();
                    }
                    if (names.Length == 0)
                    {
                        var smooth = await reader.ReadLinesAsync(frame, crop, 4, enhance: false, smooth: true).ConfigureAwait(false);
                        retryTexts.AddRange(smooth.Where(item => EligibleHeader(item.Region) && !IsCategoryLine(item.Text)).Select(item => item.Text));
                        foreach (var item in smooth) Trace?.Invoke("Title smooth: " + item.Text);
                        names = smooth.Where(item => EligibleHeader(item.Region) && !IsCategoryLine(item.Text))
                            .Select(item => Normalize(item.Text)).Where(_names.ContainsKey)
                            .Select(name => _names[name]).DistinctBy(card => card.Id).ToArray();
                    }
                    if (names.Length == 0)
                    {
                        names = retryTexts.Where(text => text.Trim().Contains(' '))
                            .Select(NormalizeGlyphs).Where(_glyphNames.ContainsKey)
                            .Select(key => _glyphNames[key]).DistinctBy(card => card.Id).ToArray();
                        glyphConfusion = names.Length == 1;
                    }
                    if (names.Length > 0) break;
                }
                if (names.Length > 0) break;
            }
            if (names.Length == 0 && UseTitleStyleFallback)
            {
                // Only after every legacy path abstains. At most two small OCR calls,
                // exact catalog names only, with every original geometry/category gate.
                var styledNames = new List<CardDefinition>();
                foreach (var window in windows)
                {
                    var lines = await reader.ReadTitleLinesAsync(frame, window).ConfigureAwait(false);
                    foreach (var line in lines) Trace?.Invoke("Title style: " + line.Text);
                    sawHeader |= lines.Any(line => IsPreviewHeader(frame, line.Region));
                    var headers = lines.Where(line => EligibleHeader(line.Region) && TitleTextStyle.IsBrightTitleLine(frame, line.Region) && !IsCategoryLine(line.Text))
                        .OrderBy(line => line.Region.Top).ToArray();
                    var top = headers.FirstOrDefault()?.Region.Top;
                    foreach (var line in headers.Where(line => top is not null && line.Region.Top - top < .009))
                    {
                        var strip = window with { Left = Math.Min(window.Left, boundary.Region.Left - .175), Right = boundary.Region.Left - .006 };
                        if (TitleTextStyle.CoversBrightText(frame, line.Region, strip) && _names.TryGetValue(Normalize(line.Text), out var card))
                            styledNames.Add(card);
                    }
                }
                names = styledNames.DistinctBy(card => card.Id).ToArray();
                styledTitle = names.Length == 1;
            }
            if (names.Length != 1) { HasUnresolvedHeader |= sawHeader; continue; }
            // A supplemental read must not cancel the artwork worker's opportunity
            // to supply its existing, independently accepted evidence.
            HasUnresolvedHeader |= styledTitle;
            var needsConfirmation = weakBorder || glyphConfusion || styledTitle;
            result.Add(new CardSighting(names[0], side, CardSightSource.PlayPreview, boundary.Region, needsConfirmation ? .16 : .10, 1,
                (styledTitle ? "Exact visible preview title (adaptive title style): " : glyphConfusion ? "Complete visible preview title (U/V glyph equivalence): " : "Exact visible preview title: ") + names[0].Name + "; " +
                (needsConfirmation ? "repeated frames required" : "card-frame boundary also present"),
                NeedsTemporalConfirmation: needsConfirmation, IsSupplementalTitle: styledTitle));
        }
        return result;
    }

    private static bool IsPreviewHeader(PixelFrame frame, NormalizedRegion region)
    {
        // A nearby board hover can overlap the preview tooltip. Only the adjacent title
        // column qualifies; names mentioned in black ability text are not play evidence.
        var center = (region.Left + region.Right) / 2;
        if (center is < .705 or > .805 || region.Bottom - region.Top < .012) return false;
        var light = 0; var total = 0;
        for (var y = region.PixelTop(frame.Height); y < region.PixelBottom(frame.Height); y++)
        for (var x = region.PixelLeft(frame.Width); x < region.PixelRight(frame.Width); x++)
        {
            var color = frame.GetPixel(x, y);
            if (Math.Min(color.Red, Math.Min(color.Green, color.Blue)) >= 170) light++;
            total++;
        }
        // White lettering on a dark faction header, not the light parchment body.
        return total > 0 && light / (double)total is > .075 and < .60;
    }
    private static bool HasAdjacentTooltip(PixelFrame frame, NormalizedRegion header, NormalizedRegion art)
    {
        // A board-hover panel can occupy the same title column while a different
        // played card remains visible. Its parchment must actually reach the preview.
        var parchment = 0; var samples = 0;
        for (var y = header.Bottom + .06; y <= Math.Min(.97, header.Bottom + .115); y += .009)
        foreach (var x in new[] { art.Left - .010, art.Left - .016 })
        {
            var p = frame.GetPixel(Math.Clamp((int)(x * frame.Width), 0, frame.Width - 1), (int)(y * frame.Height));
            if (p.Red >= 105 && p.Green >= 85 && p.Blue >= 60 && p.Red > p.Blue * 1.10 && p.Green > p.Blue * 1.04) parchment++;
            samples++;
        }
        return parchment >= samples * .50;
    }
    private static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark &&
                            char.IsLetterOrDigit(character)))
        .ToUpperInvariant();
    // Complete long titles may use only these recurring capital-font OCR
    // equivalences. The normalized title must remain unique and is confirmed
    // on a second frame, so this does not introduce nearest-name guessing.
    private static string NormalizeGlyphs(string value) => Normalize(value).Replace('U', 'V').Replace('L', 'I');
    private static bool HasHeaderAbove(PixelFrame frame, NormalizedRegion line, NormalizedRegion art)
    {
        // Category rows share the header colour. Reject them when another line of
        // white letters on dark background is immediately above (even if OCR lost it).
        var whiteRows = 0;
        for (var y = line.Top - .048; y < line.Top - .009; y += 1d / frame.Height)
        {
            var white = 0; var dark = 0; var total = 0;
            for (var x = art.Left - .13; x < art.Left - .01; x += 1d / frame.Width)
            {
                if (y < 0 || x < 0) continue;
                var p = frame.GetPixel((int)(x * frame.Width), (int)(y * frame.Height));
                if (Math.Min(p.Red, Math.Min(p.Green, p.Blue)) >= 180) white++;
                if (Math.Max(p.Red, Math.Max(p.Green, p.Blue)) < 100) dark++;
                total++;
            }
            if (total > 0 && white >= total * .06 && dark >= total * .50) whiteRows++;
        }
        return whiteRows >= Math.Max(3, frame.Height * .005);
    }
    private bool IsCategoryLine(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        is { Length: > 0 } parts && parts.All(part => _categories.Contains(Normalize(part)));
}
