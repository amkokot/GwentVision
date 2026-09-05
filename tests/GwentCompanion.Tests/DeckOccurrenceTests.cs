using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

internal static class DeckOccurrenceTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run(string root)
    {
        var at = DateTimeOffset.Parse("2026-08-28T00:00:00Z");
        var cache = Path.Combine(root, "GwentCompanion/cache/decks");
        var file = Directory.GetFiles(cache, "*.json").First(p => !Path.GetFileName(p).StartsWith("guide-"));
        var hash = Path.GetFileNameWithoutExtension(file);
        var entry = DeckLinkFileReader.FromText("https://www.playgwent.com/en/decks/" + hash).Single() with {
            SourceId = "row-1", Sheet = "14.2", Row = 1, Patches = [new("14.2", false, "Worksheet 14.2")] };
        var current = entry with { SourceId = "row-2", Sheet = "14.8", Row = 2, Patches = [new("14.8", false, "Worksheet 14.8")] };
        var third = current with { SourceId = "row-3", Row = 3 };
        var service = new PlayGwentDeckCacheService();
        var imported = service.LoadCached([entry, current, third, third], cache, int.MaxValue).Single();
        Check(imported.Occurrences?.Count == 3 && imported.Occurrences.Count(o => o.Patch == "14.8") == 2,
            "Importer lost repeated same-patch rows or double-counted the same source.");
        var library = new DeckLibrary(); library.Merge([imported, imported]);
        var saved = DeckOccurrences.Record(imported with { Id = "manual" }, "scan-1", "LibrarySave", at, "Test scan");
        library.Merge([saved, saved]);
        library.Merge([DeckOccurrences.Record(saved with { Id = "manual-again" }, "scan-2", "LibrarySave", at.AddHours(1), "Test scan")]);
        Check(library.Decks.Length == 1 && library.Decks[0].Occurrences?.Count == 5 &&
            library.Decks[0].Patches!.Any(p => p.Label == "14.2"), "Exact duplicate merge lost old patches or source records.");
        Check(DeckOccurrences.Evidence(library.Decks[0].Occurrences).Length == 2 &&
            DeckOccurrences.Describe(library.Decks[0].Occurrences).Contains("14.8 ×1"), "Same-patch rows/saves inflated observation counts.");
        var resaved = DeckOccurrences.Record(saved, "scan-1", "LibrarySave", at.AddMonths(1), "Re-save");
        Check(resaved.Occurrences!.Single(o => o.Id == "scan-1").Patch == "14.8" &&
            resaved.Patches!.All(p => p.Label != "14.9"), "Same-event save rejuvenated the observation.");
        var path = Path.Combine(root, "GwentCompanion/diagnostics/occurrence-tests", Guid.NewGuid().ToString("N"), "library.json");
        library.Save(path); var restored = DeckLibrary.Load(path);
        Check(restored.Decks[0].Occurrences!.SequenceEqual(library.Decks[0].Occurrences!), "Occurrence history failed library round trip.");
        restored.Save(path); Check(File.Exists(path + ".bak"), "Occurrence save did not preserve a backup.");
        var analyzer = new DeckMetaAnalyzer();
        var variant = imported with { Id = "other", Occurrences = [], Cards = imported.Cards.Skip(1)
            .Append(new DeckCard(imported.Cards[0].Card with { Id = "other-card" }, imported.Cards[0].Count)).ToArray() };
        var baseList = imported with { Occurrences = imported.Occurrences!.Take(1).ToArray() };
        var before = analyzer.AnalyzeAt([baseList, variant], [], imported.Faction, null, null, at);
        var after = analyzer.AnalyzeAt([library.Decks[0], variant], [], imported.Faction, null, null, at);
        Check(after.CorpusDecks == 2 && after.RankedDecks.First(r => r.Deck.Id != "other").Score > before.RankedDecks.First(r => r.Deck.Id != "other").Score,
            "Repeat observations failed to affect a bounded prevalence prior without multiplying samples.");
        Console.WriteLine("  Occurrences: source history retained; library evidence deduplicated per patch; historical retention, idempotent resave, persistence and bounded cross-patch prior passed.");
    }
}
