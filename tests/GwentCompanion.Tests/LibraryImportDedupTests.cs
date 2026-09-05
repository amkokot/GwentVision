using System.IO;
using System.Security.Cryptography;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

internal static class LibraryImportDedupTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run(string root)
    {
        var folder = Path.Combine(root, "GwentCompanion/diagnostics/library-import-dedup-tests"); Directory.CreateDirectory(folder);
        var livePath = Path.Combine(root, "GwentCompanion/cache/deck-library.json");
        var liveHash = SHA256.HashData(File.ReadAllBytes(livePath));
        var at = DateTimeOffset.Parse("2026-08-28T00:00:00Z");
        var cards = Enumerable.Range(0, 25).Select(i => new DeckCard(new("dedup" + i, "Card " + i, "Skellige", CardKind.Unit, 4))).ToArray();
        var a = new DeckDefinition("a", "Original", "Skellige", "Onslaught", 16, cards,
            Patches: [new("14.8", false, "Original source")], Occurrences: [new("event-a", "14.8", "Import", "Original source", at)]);
        var b = a with { Id = "b", Name = "Other variant", Occurrences = [],
            Cards = cards.Select((c, i) => i == 24 ? c with { Card = c.Card with { Id = "other-card" } } : c).ToArray() };
        var library = new DeckLibrary(); library.Merge([a, b]); library.EnsureVariationGroups();
        var analyzer = new DeckMetaAnalyzer();
        DeckMetaReport Predict(IEnumerable<DeckDefinition> decks) => analyzer.AnalyzeAt(decks, [], "Skellige", null, "Onslaught", at, patchContext: new("14.8"));
        var baseline = Predict(library.Decks);
        void SamePrediction(DeckMetaReport report, string context)
        {
            Check(report.CorpusDecks == baseline.CorpusDecks && report.Cards.Count == baseline.Cards.Count, context + ": sample/card counts changed.");
            var scores = report.RankedDecks.ToDictionary(r => DeckMetaAnalyzer.CompositionKey(r.Deck), r => r.Score);
            Check(baseline.RankedDecks.All(r => Math.Abs(scores[DeckMetaAnalyzer.CompositionKey(r.Deck)] - r.Score) < 1e-12), context + ": ranking weights inflated.");
            foreach (var expected in baseline.Cards)
            {
                var actual = report.Cards.Single(c => c.Card.Id == expected.Card.Id);
                Check(Math.Abs(expected.ConditionalPresence - actual.ConditionalPresence) < 1e-12 &&
                    Math.Abs(expected.RecentPrevalence - actual.RecentPrevalence) < 1e-12 &&
                    expected.SupportingDecks == actual.SupportingDecks && expected.CopyPresence.SequenceEqual(actual.CopyPresence),
                    context + ": card prevalence/copy recommendations changed.");
            }
        }
        // Multiple apps: new deck IDs, URLs, names, occurrence IDs, order and save times, same composition/patch.
        for (var i = 1; i <= 6; i++)
        {
            var incoming = a with { Id = "remote-" + i, Name = "Renamed " + i, Cards = cards.Reverse().ToArray(),
                SourceUri = new Uri("https://www.playgwent.com/en/decks/" + i.ToString("x32")),
                LastEdited = at.AddHours(i), Patches = [new("14.8", i % 2 == 0, "Remote " + i)],
                Occurrences = [new("remote-event-" + i, i == 6 ? "14.8.1" : "14.8", i % 2 == 0 ? "LibrarySave" : "Import", "Remote " + i, at.AddHours(i), i % 2 == 0)] };
            var sender = new DeckLibrary(); sender.Merge([incoming]); var path = Path.Combine(folder, "source-" + i + ".gwent-library.json"); sender.ExportTransfer(path);
            var preview = library.PreviewTransfer(path);
            Check(preview.Added == 0 && preview.Merged == 1 && library.Find(a.Id)!.Deck.Occurrences!.Count == i, "Preview added a duplicate deck or mutated the receiver.");
            library = preview.Library;
            Check(DeckOccurrences.Evidence(library.Find(a.Id)!.Deck.Occurrences).Length == 1, "Same-patch import became a new observation.");
            SamePrediction(Predict(library.Decks), "Cross-library import " + i);
            var repeated = library.PreviewTransfer(path);
            Check(repeated.Library.Find(a.Id)!.Deck.Occurrences!.Count == i + 1, "Identical transfer grew the source journal.");
            SamePrediction(Predict(repeated.Library.Decks), "Repeated transfer " + i);
        }
        Check(library.Find(a.Id)!.Sources.Length == 6 && library.Find(a.Id)!.Aliases.Length == 7 &&
            library.Find(a.Id)!.Deck.Occurrences!.Count == 7, "Deduplication discarded provenance.");
        Check(DeckOccurrences.Describe(library.Find(a.Id)!.Deck.Occurrences) == "Observations: 14.8 ×1", "Displayed observation count inflated.");
        var roundtripPath = Path.Combine(folder, "roundtrip.gwent-library.json"); library.ExportTransfer(roundtripPath);
        var secondApp = new DeckLibrary().PreviewTransfer(roundtripPath).Library;
        SamePrediction(Predict(secondApp.Decks), "Transfer into an empty app");
        secondApp.ExportTransfer(roundtripPath); library = library.PreviewTransfer(roundtripPath).Library;
        SamePrediction(Predict(library.Decks), "App-to-app round trip");
        // Existing caches are corrected in prediction, without destructive journal migration.
        var oldDuplicates = a with { Occurrences = Enumerable.Range(0, 40).Select(i => new DeckOccurrence("old-" + i, "14.8", "Import", "Old copy " + i, at)).ToArray() };
        SamePrediction(Predict([oldDuplicates, b]), "Existing duplicate history");
        SamePrediction(Predict([a, oldDuplicates with { Id = "alias" }, b]), "Unmerged duplicate compositions");
        var persisted = new DeckLibrary(); persisted.Merge([oldDuplicates, b]); var cachePath = Path.Combine(folder, "old-library.json"); persisted.Save(cachePath);
        var reloaded = DeckLibrary.Load(cachePath); SamePrediction(Predict(reloaded.Decks), "Reload existing cache");
        Check(reloaded.Find(a.Id)!.Deck.Occurrences!.Count == 40, "Existing source journal was destructively rewritten.");
        // Different patches and truly different variants are still independent evidence.
        var historical = new DeckLibrary(); historical.Merge([a with { Id = "historical", Patches = [new("14.7", false, "Earlier")],
            Occurrences = [new("past-event", "14.7", "Import", "Earlier", at.AddMonths(-1))] }]);
        var historyPath = Path.Combine(folder, "history.gwent-library.json"); historical.ExportTransfer(historyPath);
        library = library.PreviewTransfer(historyPath).Library;
        Check(DeckOccurrences.Evidence(library.Find(a.Id)!.Deck.Occurrences).Length == 2 && library.Decks.Length == 2,
            "Different patches were collapsed or another variation was removed.");
        Check(Predict(library.Decks).RankedDecks.Single(r => r.Deck.Id == a.Id).Score > baseline.RankedDecks.Single(r => r.Deck.Id == a.Id).Score,
            "Genuine cross-patch presence lost its bounded prevalence effect.");
        var matches = new[] { new DeckOccurrence("match-one", "14.8", "OpponentMatchInferred", "Match", at),
            new DeckOccurrence("match-two", "14.8", "OpponentMatchInferred", "Match", at.AddMinutes(10)),
            new DeckOccurrence("match-three", "14.8", "OpponentMatch", "Reviewed match", at.AddMinutes(20)) };
        var withMatches = DeckOccurrences.Merge(a.Occurrences, matches, matches);
        Check(DeckOccurrences.Evidence(withMatches).Length == 4 && DeckOccurrences.Describe(withMatches) == "Observations: 14.8 ×4",
            "Distinct match sessions were collapsed or copied sessions were counted twice.");
        Check(liveHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(livePath))), "Live library changed during tests.");
        var summary = "PASS: six cross-library imports with different IDs/names/URLs/rows/save kinds; repeat and app round trips; unchanged ranking, prevalence and copy predictions; old-cache duplicates suppressed without losing provenance; different patches/variants and genuine matches preserved; live library unchanged.";
        File.WriteAllText(Path.Combine(folder, "result.txt"), summary); Console.WriteLine(summary);
    }
}
