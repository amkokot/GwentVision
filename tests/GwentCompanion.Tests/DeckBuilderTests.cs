using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using OpenCvSharp;

internal static class DeckBuilderTests
{
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    public static void LeaderChoices(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json"));
        var leaders = GwentOneCardCatalog.StartingLeaders(catalog);
        foreach (var faction in new[] { "Monsters", "Nilfgaard", "Northern Realms", "Scoia'tael", "Skellige", "Syndicate" })
            Check(leaders.Count(card => card.Faction == faction) == 7, "Cached catalog must offer seven starting abilities for " + faction);
        Check(leaders.Length == 42 && leaders.All(card => card.Provision > 0), "Replacement abilities must not appear as starting leaders.");
        var expected = new[] { "Call of Harmony", "Deadeye Ambush", "Guerilla Tactics", "Invigorate", "Mahakam Forge", "Nature's Gift", "Precision Strike" };
        Check(leaders.Where(card => card.Faction == "Scoia'tael").Select(card => card.Name).SequenceEqual(expected), "Every Scoia'tael starting ability must be selectable.");
        var row = new NormalizedRegion(.06, .22, .16, .24);
        foreach (var leader in leaders)
            Check(DeckBuilderScanner.MatchHeader([new(leader.Name.ToUpperInvariant(), row)], catalog, CardKind.Leader)?.Id == leader.Id,
                "Exact printed leader must be recognized: " + leader.Name);
        Check(DeckBuilderScanner.MatchHeader([new("INUIGORATE", row)], catalog, CardKind.Leader)?.Name == "Invigorate", "Uppercase U/V font confusion must recover Invigorate.");
        Check(DeckBuilderScanner.MatchHeader([new("INUIGORATE", row)], catalog, CardKind.Leader, false) is null, "The old exact-only matcher should reproduce this miss.");
        foreach (var text in new[] { "INVIGORAT", "INXIGORATE", "INUGORATE", "Invigorate your units", "Inuigorate", "CURSE OF LUST" })
            Check(DeckBuilderScanner.MatchHeader([new(text, row)], catalog, CardKind.Leader) is null, "Incomplete, non-header or replacement-ability text must not infer a starting leader: " + text);
        Check(DeckBuilderScanner.MatchHeader([new("INUIGORATE", row), new("BLOOD SCENT", row)], catalog, CardKind.Leader) is null, "Conflicting printed names must abstain.");
        var ambiguity = new[] { new CardDefinition("a", "Invigorate", "Scoia'tael", CardKind.Leader, 18),
            new CardDefinition("b", "Inuigorate", "Scoia'tael", CardKind.Leader, 18) };
        Check(DeckBuilderScanner.MatchHeader([new("INUIGORATE", row)], ambiguity, CardKind.Leader) is null, "Exact/corrected header ambiguity must abstain.");
        var draft = new DeckScanDraft(); var old = leaders.First(card => card.Faction == "Monsters"); var next = leaders.First(card => card.Name == "Invigorate");
        draft.SetLeader(old); draft.ClearLeader(); draft.ObserveHeader(next, null);
        Check(draft.Leader is null, "Changing picker faction must not manufacture leader evidence.");
        draft.ObserveHeader(next, null);
        Check(draft.Leader == next, "Changing picker faction must allow two-read automatic recognition again.");
    }

    public static async Task ScoiataelHeaderAsync(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json"));
        using var scanner = new DeckBuilderScanner(catalog);
        var directory = Path.Combine(root, "GwentCompanion", "deck-scans", "20260827-144112-759d80");
        var draft = new DeckScanDraft(); var pages = new List<object>();
        foreach (var path in Directory.GetFiles(directory, "page-*.jpg").Order())
        {
            var page = await scanner.ReadAsync(DeckBuilderReplay.Restore(path), DeckBuilderScanner.LeftPanel);
            draft.ObserveHeader(page.Leader, page.Stratagem);
            if (Path.GetFileName(path) is "page-001.jpg" or "page-002.jpg")
                Check(page.Leader?.Name == "Invigorate" && page.Stratagem?.Name == "Tactical Advantage", "Verified top-frame headers must be recovered independently.");
            pages.Add(new { Page = Path.GetFileName(path), Leader = page.Leader?.Name, Stratagem = page.Stratagem?.Name, page.NameCorrections });
        }
        Check(draft.Leader?.Name == "Invigorate" && draft.Stratagem?.Name == "Tactical Advantage", "Scoia'tael header must reach ordinary draft consensus.");
        Check(pages.Count == 10, "Replay must exercise all saved scan pages.");
        var report = new { Source = directory, Scope = "Header recognition only; no user-confirmed full deck composition is available for this scan",
            Leader = draft.Leader!.Name, Stratagem = draft.Stratagem!.Name, Pages = pages };
        File.WriteAllText(Path.Combine(root, "GwentCompanion", "diagnostics", "v0.1.7-header-replay.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("  Scoia'tael saved scan: Invigorate / Tactical Advantage; 10 pages replayed.");
    }

    public static void NameRules(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json"));
        var row = new NormalizedRegion(.062, .5, .18, .517);
        var expected = new[] { "Portal", "Aerondight", "Lady of the Lake", "Nekker Warrior", "Siren" };
        var lines = new[] { "IO Portal", "II Aerondight", "7 LadyoftheLake", "7 4 Nekker Warrior x2", "4 S Siren" }.Select(text => new VisibleTextLine(text, row));
        Check(DeckBuilderScanner.MatchLines(lines, catalog).Select(item => item.Card.Name).SequenceEqual(expected), "Stat glyphs / merged spaces must not reject exact card names.");
        Check(DeckBuilderScanner.MatchLines([new("Gotyat", row)], catalog).Count == 0, "Default text matcher remains exact.");
        Check(DeckBuilderScanner.MatchLines([new("Gotyat", row)], catalog, true).Single().Card.Name == "Golyat", "A unique whole-name one-glyph correction can recover Golyat.");
        foreach (var text in new[] { "Nekker Warrio", "Nekker", "Play Portal from your deck", "Goxyat", "Gottat" })
        {
            var matches = DeckBuilderScanner.MatchLines([new(text, row)], catalog, true);
            Check(matches.All(item => item.Card.Name != (text == "Nekker" ? "Nekker Warrior" : text == "Nekker Warrio" ? "Nekker Warrior" : text.Contains("Portal") ? "Portal" : "Golyat")),
                "No prefix completion, arbitrary typo guessing or ability-prose matching: " + text);
        }
        var ambiguous = new[] { new CardDefinition("a", "Golyat", "Monsters", CardKind.Unit, 7), new CardDefinition("b", "Goiyat", "Monsters", CardKind.Unit, 7) };
        Check(DeckBuilderScanner.MatchLines([new("Gotyat", row)], ambiguous, true).Count == 0, "Two possible confusable names must abstain.");
    }

    public static async Task QuantitiesAsync(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json"));
        var source = DeckBuilderReplay.Restore(Path.Combine(root, "GwentCompanion", "deck-scans", "20260827-140327-db4212", "page-011.jpg"));
        // This bottom page and all its cards were not used to extract the glyph reference (page 001 Griffin).
        using var scanner = new DeckBuilderScanner(catalog);
        var top = await scanner.ReadAsync(DeckBuilderReplay.Restore(Path.Combine(root, "GwentCompanion", "deck-scans", "20260827-140327-db4212", "page-001.jpg")), DeckBuilderScanner.LeftPanel);
        Check(top.Leader?.Name == "Fruits of Ysgith" && top.Stratagem?.Name == "Urn of Shadows", "Builder header must expose the printed leader and stratagem.");
        using var glyphReader = new DeckBuilderQuantityReader();
        var original = await scanner.ReadAsync(source, DeckBuilderScanner.LeftPanel);
        Check(original.Leader is null && original.Stratagem is null, "Scrolled-away headers must not be guessed from deck cards.");
        foreach (var scale in new[] { 1d, .75, 1.5 })
        {
            var frame = Resize(source, scale);
            var page = await scanner.ReadAsync(frame, DeckBuilderScanner.LeftPanel);
            var quantity = page.Cards.GroupBy(item => item.Card.Name).ToDictionary(group => group.Key, group => group.Sum(item => item.Count));
            Console.WriteLine($"  Builder {frame.Width}×{frame.Height}: " + string.Join(", ", page.Quantities!.Select(item => catalog.First(card => card.Id == item.CardId).Name + "=" + item.Score.ToString("F3"))));
            foreach (var badge in original.Quantities!)
                Check((glyphReader.Score(frame, badge.Region) >= DeckBuilderQuantityReader.MinimumScore) == badge.HasDoubleMarker,
                    $"Quantity-marker classification changed at scale {scale} for {badge.CardId}.");
            // Synthetic downsizing can erase name strokes; only marker recognition is asserted at those scales.
            if (scale != 1) continue;
            foreach (var name in new[] { "Griffin", "Rotfiend", "Siren", "Ghoul", "Fiend", "Nekker Warrior" })
                Check(quantity.GetValueOrDefault(name) == 2, $"Visible ×2 {name} missing at scale {scale}.");
            Check(quantity.GetValueOrDefault("Harpy Egg") == 1, "Single unmarked bronze must not become two copies.");
            Check(page.Cards.Count(item => item.Card.Name == "Barbegazi" && item.Count == 1) == 2, "Two physical single-copy rows must stay separate, not acquire phantom ×2 badges.");
            Check(quantity.GetValueOrDefault("Portal") == 1 && quantity.GetValueOrDefault("Golyat") == 1, "Stat-free names and whole-name glyph correction must survive resizing.");
        }
    }

    public static void HeaderPersistence(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json"));
        var leader = catalog.Single(card => card.Name == "Fruits of Ysgith");
        var urn = catalog.Single(card => card.Name == "Urn of Shadows");
        var scroll = catalog.Single(card => card.Name == "Cursed Scroll");
        var draft = new DeckScanDraft();
        draft.ObserveHeader(leader, urn); Check(draft.Leader is null && draft.Stratagem is null, "Header requires two independent consecutive reads.");
        draft.ObserveHeader(leader, urn); draft.ObserveHeader(null, null);
        Check(draft.Leader == leader && draft.Stratagem == urn, "Headers must persist after scrolling out of view.");
        draft.SetStratagem(scroll); draft.ObserveHeader(leader, urn); draft.ObserveHeader(leader, urn);
        Check(draft.Stratagem == scroll, "Manual header correction wins over repeated OCR.");
        draft.Reset(); draft.ObserveHeader(leader, urn); draft.BreakSequence(); draft.ObserveHeader(leader, urn);
        Check(draft.Leader is null && draft.Stratagem is null, "Foreground breaks header consensus.");
        var cachePath = Directory.GetFiles(Path.Combine(root, "GwentCompanion", "cache", "decks"), "*.json").First();
        var deck = new PlayGwentDeckPageParser().ParseStateJson(File.ReadAllText(cachePath), new Uri("https://www.playgwent.com/en/decks/" + Path.GetFileNameWithoutExtension(cachePath)));
        Check(deck.Stratagem is not null && deck.Stratagem.Kind == CardKind.Stratagem && !deck.Cards.Any(item => item.Card.Kind == CardKind.Stratagem), "Website stratagem must be parsed outside the deck's card slots.");
        draft.Load(deck.Cards); draft.SetStratagem(urn);
        var complete = draft.Build("Metadata fixture", leader);
        Check(complete.CardCount == deck.CardCount && complete.ProvisionTotal == deck.ProvisionTotal && complete.Stratagem == urn,
            "Stratagem must not alter card quantities or provisions.");
        var library = new DeckLibrary(); var unknown = complete with { Stratagem = null };
        library.Merge([unknown]); library.Rename(unknown.Id, "Keep my name");
        Check(library.Merge([complete]).Added == 0 && library.Find(unknown.Id)!.Deck.Stratagem == urn, "Known stratagem must enrich the existing unknown list, preserving ID.");
        Check(library.Find(unknown.Id)!.Deck.Name == "Keep my name", "Enrichment must not reset custom names.");
        library.Merge([complete with { Id = "other-known", Stratagem = scroll }]);
        Check(library.Decks.Length == 2, "Different known stratagems must remain separate variants.");
        var directory = Path.Combine(Path.GetTempPath(), "gv-stratagem-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "library.json"); library.Save(path);
            var restored = DeckLibrary.Load(path);
            Check(restored.Decks.Length == 2 && restored.Find(unknown.Id)!.Deck.Stratagem?.Id == urn.Id, "Stratagem and selection alias must survive restart.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static PixelFrame Resize(PixelFrame frame, double scale)
    {
        if (scale == 1) return frame;
        using var pixels = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.BgraPixels);
        using var resized = new Mat();
        Cv2.Resize(pixels, resized, new Size((int)Math.Round(frame.Width * scale), (int)Math.Round(frame.Height * scale)), 0, 0, InterpolationFlags.Cubic);
        var bytes = new byte[resized.Width * resized.Height * 4]; Marshal.Copy(resized.Data, bytes, 0, bytes.Length);
        return new PixelFrame(resized.Width, resized.Height, bytes);
    }
}
