using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

internal static class PatchPredictionTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run(string root)
    {
        var at = DateTimeOffset.Parse("2026-08-28T00:00:00Z");
        var context = new PatchPredictionContext("14.8");
        var c = new CardDefinition("anchor", "Anchor", "Skellige", CardKind.Unit, 4);
        var d = new DeckDefinition("d", "Deck", "Skellige", "", 15, Enumerable.Range(0, 25).Select(i => new DeckCard(c with { Id = "c" + i })).ToArray());
        DeckDefinition Patch(string label) => d with { Patches = [new(label, false, "Fixture")], SourceUpdatedAt = at.AddYears(-2) };
        Check(PatchRecency.Resolve(Patch("14.8"), context).Weight == 1, "Current patch ignored due to old timestamp.");
        Check(PatchRecency.Resolve(Patch("14.6"), context) is { Distance: 2, Weight: .5 }, "Two-patch half-life is wrong.");
        Check(PatchRecency.Resolve(Patch("14.6") with { SourceUpdatedAt = at, LastEdited = at }, context).Weight == .5,
            "Recaching or server edits changed a patch-labeled weight.");
        Check(PatchRecency.Resolve(Patch("13.12"), new("14.1")).Distance == 1, "Patch distance fails at year rollover.");
        Check(PatchRecency.Resolve(Patch("14.10"), new("14.12")).Distance == 2, "Patch ordering is lexicographic instead of numeric.");
        Check(!PatchRecency.Resolve(Patch("14.9"), context).Available, "Future-only deck entered a historical prediction.");
        Check(PatchRecency.Resolve(d with { Patches = [new("14.6", false, "Earlier"), new("14.9", false, "Later")] }, context).Distance == 2,
            "A real earlier occurrence was discarded or a future reappearance leaked in.");
        Check(!PatchRecency.Resolve(d with { Patches = [new("14.7", true, "Worksheet month JUL AUG"), new("14.8", true, "Worksheet month JUL AUG")] }, new("14.7", StrictHistory: true)).Available,
            "Ambiguous multi-month sheet leaked its later possible patch into a backtest.");
        Check(PatchRecency.Resolve(d with { SourceUpdatedAt = at.AddMonths(-2) }, context) is { Distance: 2, DateFallback: true, Weight: .5 },
            "Missing-patch date fallback must become a patch bucket, not day-by-day decay.");
        Check(!PatchRecency.Resolve(d, context with { StrictHistory = true }).Available, "Undated/untagged decks entered strict backtesting.");
        Check(PatchRecency.Resolve(Patch("7.37.4"), context) is { Legacy: true, Weight: PatchRecency.BackgroundFloor },
            "An old compound worksheet label became current through a misleading timestamp.");
        var old = Patch("14.6") with { Id = "old", Cards = d.Cards.Select((e, i) => i == 0 ? new DeckCard(c with { Id = "old-card" }) : e).ToArray() };
        var fresh = Patch("14.8") with { Id = "fresh", Cards = d.Cards.Select((e, i) => i == 0 ? new DeckCard(c with { Id = "new-card" }) : e).ToArray() };
        var future = Patch("14.9") with { Id = "future", Leader = "Future leader", Cards = d.Cards.Select((e, i) => i == 0 ? new DeckCard(c with { Id = "future-card" }) : e).ToArray() };
        var analyzer = new DeckMetaAnalyzer();
        var result = analyzer.AnalyzeAt([old, fresh, future], [], "Skellige", null, null, at, patchContext: context);
        Check(result.CorpusDecks == 2 && result.Cards.All(s => s.Card.Id != "future-card"), "Future-patch cards leaked through analyzer deduplication.");
        Check(result.Cards.Single(s => s.Card.Id == "new-card").ConditionalPresence > result.Cards.Single(s => s.Card.Id == "old-card").ConditionalPresence,
            "Patch weighting failed to favor the newer package.");
        var frozen = analyzer.AnalyzeAt([old, fresh], [], "Skellige", null, null, at.AddYears(1), patchContext: context);
        Check(result.Cards.Zip(frozen.Cards).All(p => p.First.Card.Id == p.Second.Card.Id && Math.Abs(p.First.ConditionalPresence - p.Second.ConditionalPresence) < 1e-12),
            "Fixed-patch labeled predictions changed with wall-clock time.");
        // A deck can return exactly, or with one substitution, after a sparse interval.
        var returning = fresh with { Patches = [new("14.8", false, "Current"), new("14.2", false, "Earlier")] };
        var revival = ReturningDeckPatterns.Find([returning], [0], context).Single();
        Check(revival is { PreviousPatch: "14.2", LatestPatch: "14.8", GapPatches: 5, InterveningLists: 0 },
            "Repeated exact composition lost its historical reappearance.");
        var oldVariant = old with { Patches = [new("14.2", false, "Earlier")] };
        var patterns = ReturningDeckPatterns.Find([fresh, oldVariant], [0, 0], context);
        Check(patterns.All(p => p is not null), "A returning one-card variant was not recognized.");
        var continuous = returning with { Patches = Enumerable.Range(2, 7).Select(i => new DeckPatch("14." + i, false, "Monthly")).ToArray() };
        Check(ReturningDeckPatterns.Find([continuous], [0], context).Single() is null, "Continuous presence was mislabeled as a return.");
        var differentLeader = oldVariant with { Leader = "Other leader" };
        Check(ReturningDeckPatterns.Find([fresh with { Leader = "Known leader" }, differentLeader], [0, 0], context).All(p => p is null),
            "Conflicting leaders rejuvenated an unrelated strategy.");
        var remote = oldVariant with { Cards = oldVariant.Cards.Select((e, i) => i < 3 ? new DeckCard(c with { Id = "remote" + i }) : e).ToArray() };
        Check(ReturningDeckPatterns.Find([fresh, remote], [0, 0], context).All(p => p is null),
            "Transitive variant-family links became direct returning evidence.");
        var sparse = oldVariant with { Id = "one-between", Patches = [new("14.5", false, "Middle")] };
        Check(ReturningDeckPatterns.Find([fresh, oldVariant, sparse], [0, 0, 0], context)[0] is { InterveningLists: 1 },
            "One intervening observation prevented sparse-gap recognition.");
        Check(ReturningDeckPatterns.Find([returning], [0], new("14.6", StrictHistory: true)).Single() is null,
            "A future return leaked into historical predictions.");
        var returningReport = analyzer.AnalyzeAt([fresh, oldVariant], [new(fresh.Cards[1].Card,
            CardProvenance.ConfirmedStartingDeck, 1, at)], "Skellige", null, null, at, patchContext: context);
        Check(returningReport.Cards.Any(s => s.ReturningPattern is not null), "Relevant returning history was unavailable for explanation.");
        DeckOccurrenceTests.Run(root);
        var library = DeckLibrary.Load(Path.Combine(root, "GwentCompanion/cache/deck-library.json"));
        var rows = library.Decks.Select(deck => new { deck.Id, Evidence = PatchRecency.Resolve(deck, context) }).ToArray();
        var report = new { Target = context, LibraryRecords = rows.Length, PatchLabeled = rows.Count(r => r.Evidence.Label is not null && !r.Evidence.DateFallback),
            DateFallback = rows.Count(r => r.Evidence.DateFallback), Unknown = rows.Count(r => r.Evidence.Label is null),
            Distances = rows.GroupBy(r => r.Evidence.Distance).Select(g => new { Distance = g.Key, Decks = g.Count(), Weight = g.First().Evidence.Weight }) };
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.23-patch-coverage.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Patch prediction passed: {report.PatchLabeled}/{report.LibraryRecords} patch-labeled, {report.DateFallback} date fallbacks, {report.Unknown} unknown; numeric distance, aliases, ambiguity and future exclusion verified.");
    }
}
