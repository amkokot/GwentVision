using System.Text.RegularExpressions;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Platform.Windows.Vision;

public sealed record DeckBuilderQuantityEvidence(string CardId, NormalizedRegion Region, double Score, bool HasDoubleMarker);
public sealed record DeckBuilderNameCorrection(string CardId, string RawText, string MatchedName);
public sealed record DeckBuilderPage(IReadOnlyList<DeckCard> Cards, IReadOnlyList<VisibleTextLine> Lines,
    IReadOnlyList<DeckBuilderQuantityEvidence>? Quantities = null, IReadOnlyList<DeckBuilderNameCorrection>? NameCorrections = null,
    CardDefinition? Leader = null, CardDefinition? Stratagem = null, IReadOnlyList<VisibleTextLine>? UnresolvedRows = null);

/// <summary>Reads name strips only, so premium animations are irrelevant. The user selects the deck-list panel.</summary>
public sealed class DeckBuilderScanner : IDisposable
{
    private readonly ScreenStateRecognizer _text = new();
    private readonly DeckBuilderQuantityReader _quantity = new();
    private readonly IReadOnlyList<CardDefinition> _catalog;
    public static readonly NormalizedRegion LeftPanel = new(.01, .16, .30, .94);
    public static readonly NormalizedRegion RightPanel = new(.70, .16, .99, .94);
    public DeckBuilderScanner(IReadOnlyList<CardDefinition> catalog) => _catalog = catalog;

    public async Task<DeckBuilderPage> ReadAsync(PixelFrame frame, NormalizedRegion region)
    {
        // The list's name, stat and quantity columns must not compete in a single page OCR pass.
        // These column offsets are calibrated from a real builder scan; both sides retain the same layout.
        var factor = (region.Right - region.Left) / .29;
        var nameBand = new NormalizedRegion(region.Left + .052 * factor, region.Top,
            region.Left + .182 * factor, region.Bottom);
        var scale = Math.Clamp((int)(2400 / (frame.Height * (region.Bottom - region.Top))), 1, 4);
        var lines = await _text.ReadLinesAsync(frame, nameBand, scale, enhance: false, smooth: true).ConfigureAwait(false);
        var cards = new List<DeckCard>(); var quantities = new List<DeckBuilderQuantityEvidence>();
        var corrections = new List<DeckBuilderNameCorrection>();
        var unresolved = new List<VisibleTextLine>();
        var acceptedRows = new List<VisibleTextLine>();
        foreach (var line in lines.OrderBy(line => line.Region.Top))
        {
            var match = MatchLines([line], _catalog).SingleOrDefault();
            if (match is null)
            {
                match = MatchLines([line], _catalog, allowGlyphCorrection: true).SingleOrDefault();
                if (match is not null) corrections.Add(new(match.Card.Id, line.Text, match.Card.Name));
            }
            if (match is null) { unresolved.Add(line); continue; }
            var center = (line.Region.Top + line.Region.Bottom) / 2;
            // OCR can report the same physical row twice; only distinct rows establish separate copies.
            if (acceptedRows.Any(old => Math.Abs((old.Region.Top + old.Region.Bottom) / 2 - center) < .012)) continue;
            acceptedRows.Add(line);
            if (!match.Card.IsGold)
            {
                var badge = new NormalizedRegion(region.Left + .182 * factor, Math.Max(region.Top, center - .019),
                    region.Left + .206 * factor, Math.Min(region.Bottom, center + .019));
                var score = _quantity.Score(frame, badge);
                var doubled = score >= DeckBuilderQuantityReader.MinimumScore;
                quantities.Add(new(match.Card.Id, badge, score, doubled));
                if (doubled) match = match with { Count = 2 };
            }
            cards.Add(match);
        }
        var headerBand = new NormalizedRegion(region.Left + .03 * factor, region.Top,
            region.Left + .182 * factor, Math.Min(region.Bottom, region.Top + .165));
        var header = await _text.ReadLinesAsync(frame, headerBand, 3, enhance: false, smooth: true).ConfigureAwait(false);
        // The wider header crop can merge an emblem into the title ("N PRECISION STRIKE").
        // The narrower name-column read excludes it; only complete titles from either crop count.
        var headerNames = header.Concat(lines.Where(line => line.Region.Top >= headerBand.Top && line.Region.Bottom <= headerBand.Bottom)).ToArray();
        var leader = MatchHeader(headerNames, _catalog, CardKind.Leader);
        var stratagem = MatchHeader(headerNames, _catalog, CardKind.Stratagem);
        foreach (var matched in new[] { leader, stratagem }.Where(card => card is not null).Cast<CardDefinition>())
        {
            if (MatchHeader(headerNames, _catalog, matched.Kind, allowGlyphCorrection: false) is not null) continue;
            var raw = headerNames.First(line => MatchHeader([line], _catalog, matched.Kind)?.Id == matched.Id);
            corrections.Add(new(matched.Id, raw.Text, matched.Name));
        }
        var headerBottom = headerNames.Where(line => MatchHeader([line], _catalog, CardKind.Leader) is not null ||
            MatchHeader([line], _catalog, CardKind.Stratagem) is not null).Select(line => line.Region.Bottom).DefaultIfEmpty(region.Top).Max();
        var unreadable = unresolved.Where(line => line.Region.Top > headerBottom && line.Text.Count(char.IsLetter) >= 4 &&
            line.Region.Bottom - line.Region.Top <= .035).ToArray();
        return new(cards, lines.Concat(header).ToArray(), quantities, corrections, leader, stratagem, unreadable);
    }

    public static CardDefinition? MatchHeader(IEnumerable<VisibleTextLine> lines, IEnumerable<CardDefinition> catalog,
        CardKind kind, bool allowGlyphCorrection = true)
    {
        if (kind is not (CardKind.Leader or CardKind.Stratagem)) throw new ArgumentException("Expected header metadata.");
        var candidates = kind == CardKind.Leader ? GwentOneCardCatalog.StartingLeaders(catalog) : catalog.Where(card => card.Kind == kind).ToArray();
        var text = lines.Select(line => (Compact: DeckSearchCatalog.Normalize(line.Text).Replace(" ", ""),
            Capitals: line.Text.Any(char.IsLetter) && line.Text.Where(char.IsLetter).All(char.IsUpper))).ToArray();
        var matches = candidates.Where(card =>
        {
            var name = DeckSearchCatalog.Normalize(card.Name).Replace(" ", "");
            return text.Any(read => read.Compact == name || (allowGlyphCorrection && kind == CardKind.Leader && read.Capitals &&
                OneConfusedGlyph(read.Compact, name, "uv")));
        }).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static IReadOnlyList<DeckCard> MatchLines(IEnumerable<VisibleTextLine> lines, IEnumerable<CardDefinition> catalog,
        bool allowGlyphCorrection = false)
    {
        var names = catalog.Where(card => card.CanBeInStartingDeck && card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact)
            .Select(card => (Card: card, Name: DeckSearchCatalog.Normalize(card.Name).Replace(" ", ""))).ToArray();
        var result = new List<DeckCard>();
        foreach (var line in lines)
        {
            if (line.Region.Bottom - line.Region.Top > .07) continue;
            var text = DeckSearchCatalog.Normalize(line.Text);
            // Legacy page OCR can confuse the stat column (10→IO, 11→II, 5→S).
            // Strip only separate short numeric-looking prefixes, never fuzzily replace card-name letters.
            text = Regex.Replace(text, @"^(?:(?:\d+|[ios]{1,2})\s+){0,2}", "");
            text = Regex.Replace(text, @"(?:\s+(?:x\s*\d+|\d+\s*x|\d+))+$", "");
            var matches = names.Where(item => text.Replace(" ", "") == item.Name).ToArray();
            if (matches.Length == 0 && allowGlyphCorrection)
            {
                var compact = text.Replace(" ", "");
                matches = names.Where(item => OneConfusedGlyph(compact, item.Name) || OneMisreadApostrophe(compact, item.Card.Name)).ToArray();
            }
            if (matches.Length != 1) continue;
            var best = matches[0];
            // Only quantities explicitly marked x2/2x/×2 are trusted. Provision/power numbers are not copy counts.
            var count = Regex.IsMatch(line.Text, @"(?:[x×]\s*2\b|\b2\s*[x×])", RegexOptions.IgnoreCase) ? 2 : 1;
            result.Add(new(best.Card, Math.Min(count, best.Card.IsGold ? 1 : 2)));
        }
        return result;
    }

    private static bool OneConfusedGlyph(string read, string candidate, string confusionGroup = "lti1")
    {
        // One same-length, known glyph confusion in a whole name of at least six letters.
        // No nearest-name matching, prefix completion, insertions or arbitrary substitutions.
        if (read.Length < 6 || read.Length != candidate.Length) return false;
        var differences = 0;
        for (var i = 0; i < read.Length; i++)
        {
            if (read[i] == candidate[i]) continue;
            if (++differences > 1 || !(confusionGroup.Contains(read[i]) && confusionGroup.Contains(candidate[i]))) return false;
        }
        return differences == 1;
    }

    private static bool OneMisreadApostrophe(string read, string candidate)
    {
        // A printed internal apostrophe can become a thin i/l stroke: Qu'an -> Quian.
        // Only that punctuation position may differ; never allow an arbitrary inserted letter.
        if (read.Length < 6) return false;
        for (var i = 1; i < candidate.Length - 1; i++)
        {
            if (candidate[i] is not ('\'' or '\u2019' or '\u2018') || !char.IsLetter(candidate[i - 1]) || !char.IsLetter(candidate[i + 1])) continue;
            foreach (var glyph in "il")
            {
                var variant = candidate[..i] + glyph + candidate[(i + 1)..];
                if (read == DeckSearchCatalog.Normalize(variant).Replace(" ", "")) return true;
            }
        }
        return false;
    }

    public void Dispose() { _text.Dispose(); _quantity.Dispose(); }
}
