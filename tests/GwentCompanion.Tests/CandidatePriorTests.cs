using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

internal static class CandidatePriorTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-28T12:00:00Z");
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static ObservedCard Seen(CardDefinition c) => new(c, CardProvenance.ProbableStartingDeck, .99, At, "Fixture");
    private static DeckDefinition Full(string id, string faction, params CardDefinition[] cards) => new(id, id, faction, "", 15,
        cards.Concat(Enumerable.Range(0, Math.Max(0, 25 - cards.Length)).Select(i =>
            new CardDefinition(id + i, id + i, faction, CardKind.Unit, 4)))
            .GroupBy(c => c.Id).Select(g => new DeckCard(g.First(), g.Count())).ToArray(), SourceUpdatedAt: At);

    public static void Run(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        foreach (var fixture in new[] { "crones", "sigvald" })
            Check(SavedVisionEvents.Read(Path.Combine(root, $"GwentCompanion/diagnostics/v0.1.21-{fixture}-ui-fixture.json"), catalog).Count == 1,
                "Offline package UI fixture failed to load.");
        var cards = catalog.ToDictionary(c => c.Id);
        var sigvald = cards["202282"];
        var partners = new[] { "202277", "202456", "113320", "203246" }.Select(id => cards[id]).ToArray();
        var other = new CardDefinition("contradict", "Other strategy", "Skellige", CardKind.Unit, 4, IsGold: true);
        var decks = Enumerable.Range(0, 12).Select(i => Full("fixture" + i, "Skellige",
            i < 3 ? [sigvald, .. partners] : [other])).ToArray();
        var analyzer = new DeckMetaAnalyzer();
        DeckMetaReport Analyze(ObservedCard[]? seen = null, DeckCard[]? picks = null, IEnumerable<DeckDefinition>? source = null) =>
            analyzer.AnalyzeAt(source ?? decks, seen ?? [], "Skellige", null, null, At, assumptions: picks);
        var before = Analyze(); var after = Analyze(picks: [new(sigvald)]);
        foreach (var partner in partners)
        {
            var baseline = before.Cards.Single(c => c.Card.Id == partner.Id);
            var boosted = after.Cards.Single(c => c.Card.Id == partner.Id);
            Check(boosted.ConditionalPresence > baseline.ConditionalPresence + .15, "Explicit Sigvald pick failed to raise " + partner.Name);
            Check(boosted.SupportingDecks == baseline.SupportingDecks && boosted.CopyRecommendations![0].MatchingDecks == baseline.CopyRecommendations![0].MatchingDecks,
                "A click fabricated empirical sample support.");
            Check(boosted.Associations.Any(a => a.ObservedCard == "Your pick: Sigvald" && a.JointDecks == 3), "Missing assumed-source explanation.");
            Check(!boosted.CopyRecommendations![0].StronglySupported, "A click alone became strong observed support.");
        }
        Check(after.ObservedIdentities == 0 && after.CorpusDecks == before.CorpusDecks, "Assumption became evidence or a cached sample.");
        var seenOnly = Analyze([Seen(sigvald)]);
        var seenAndPick = Analyze([Seen(sigvald)], [new(sigvald)]);
        Check(seenOnly.Cards.Zip(seenAndPick.Cards).All(p => p.First == p.Second ||
            p.First.Card.Id == p.Second.Card.Id && Math.Abs(p.First.ConditionalPresence - p.Second.ConditionalPresence) < 1e-12), "Observed pick double counted.");
        Check(seenOnly.Cards.Single(c => c.Card.Id == partners[0].Id).ConditionalPresence > after.Cards.Single(c => c.Card.Id == partners[0].Id).ConditionalPresence,
            "An explicit assumption is stronger than an actual sighting.");
        Check(Analyze([Seen(other)], [new(sigvald)]).RankedDecks.First().Deck.CountOf(other.Id) > 0, "Conflicting observation failed to outweigh one speculative pick.");
        var duplicate = Analyze(picks: [new(sigvald), new(sigvald)]);
        Check(after.Cards.Zip(duplicate.Cards).All(p => Math.Abs(p.First.ConditionalPresence - p.Second.ConditionalPresence) < 1e-12), "Duplicate prior input multiplied weight.");
        var partial = Full("partial", "Skellige", sigvald) with { Cards = [new(sigvald)] };
        Check(Analyze(picks: [new(sigvald)], source: decks.Append(partial)).CorpusDecks == 12, "Partial source entered frequency denominator.");
        var projector = new OpponentDeckProjector(); var edits = new DeckProjectionEdits(); edits.Include(sigvald, 1);
        var projected = projector.Build(decks, [], "Skellige", edits: edits, catalog: catalog);
        Check(projected.Slots.Any(s => s.Card?.Id == sigvald.Id && s.State == DeckSlotState.Selected) && projected.ObservedCopies == 0,
            "Projection lost explicit pick or counted it as observed.");
        Check(projected.Meta.Cards.Single(c => c.Card.Id == partners[0].Id).ConditionalPresence > before.Cards.Single(c => c.Card.Id == partners[0].Id).ConditionalPresence,
            "UI projection did not pass picks to recommender.");
        edits.Exclude(sigvald, 1);
        var removed = projector.Build(decks, [], "Skellige", edits: edits, catalog: catalog);
        var clean = projector.Build(decks, [], "Skellige", catalog: catalog);
        Check(removed.Meta.Cards.Zip(clean.Meta.Cards).All(p => p.First.Card.Id == p.Second.Card.Id && Math.Abs(p.First.ConditionalPresence - p.Second.ConditionalPresence) < 1e-12),
            "Removing a pick left behind positive influence.");
        Check(projector.Build(decks, [], "Skellige", catalog: catalog).Meta.Cards.Zip(clean.Meta.Cards)
            .All(p => Math.Abs(p.First.ConditionalPresence - p.Second.ConditionalPresence) < 1e-12), "Rebuild reinforced automatic guesses.");
        PackageFallbacks(projector, catalog, cards);
        AuditCache(root, catalog);
        Console.WriteLine("  Explicit candidate priors, reversible reweighting, evidence precedence, Crones/Sigvald package fallbacks and safeguards passed.");
    }

    private static void PackageFallbacks(OpponentDeckProjector projector, IReadOnlyList<CardDefinition> catalog, Dictionary<string, CardDefinition> cards)
    {
        var crones = new[] { "132206", "132207", "132208" }.Select(id => cards[id]).ToArray();
        foreach (var source in crones)
        {
            var p = projector.Build([], [Seen(source)], "Monsters", catalog: catalog);
            Check(p.ObservedCopies == 1 && p.Slots.Count(s => s.PackageHint is not null && s.State == DeckSlotState.Predicted) == 2 &&
                p.Slots.Where(s => s.PackageHint is not null).All(s => s.ModelShare is null), "Crones no-data fallback missing or pretending to be measured.");
            Check(p.Meta.CorpusDecks == 0 && p.Meta.Cards.Count == 0, "Curated package polluted empirical model.");
        }
        foreach (var alternate in new[] { "200220", "200221", "200222" })
            Check(projector.Build([], [Seen(cards[alternate])], "Monsters", catalog: catalog).PackageHints!.Count == 0,
                "Alternate Crone identity triggered base-Crone package.");
        var edits = new DeckProjectionEdits(); edits.Include(crones[0], 1);
        var picked = projector.Build([], [], "Monsters", edits: edits, catalog: catalog);
        Check(picked.ObservedCopies == 0 && picked.PackageHints!.Count == 2 && picked.PackageHints.All(h => h.FromUserPick), "Picked Crone source lost provenance.");
        edits.Exclude(crones[1], 1);
        Check(projector.Build([], [], "Monsters", edits: edits, catalog: catalog).Slots.All(s => s.Card?.Id != crones[1].Id), "Package auto-fill ignored dismissal.");
        Check(projector.Build([], [Seen(crones[1])], "Monsters", edits: edits, catalog: catalog).Slots.Any(s => s.Card?.Id == crones[1].Id && s.State == DeckSlotState.Observed),
            "Observed partner did not replace dismissal.");
        edits.Clear();
        Check(projector.Build([], [], "Monsters", edits: edits, catalog: catalog).PackageHints!.Count == 0, "Fallback fed back its own guesses.");
        foreach (var origin in new[] { CardProvenance.Created, CardProvenance.Unknown })
            Check(projector.Build([], [Seen(crones[0]) with { Provenance = origin }], "Monsters", catalog: catalog).PackageHints!.Count == 0,
                "Created/unknown Crone inferred original companions.");
        Check(projector.Build([], [Seen(crones[0]) with { Confidence = .4 }], "Monsters", catalog: catalog).PackageHints!.Count == 0, "Weak sighting forced package.");
        Check(projector.Build([], [Seen(crones[0])], "Skellige", catalog: catalog).PackageHints!.Count == 0, "Faction restriction bypassed.");
        var counterexample = Full("counterexample", "Monsters", crones[0]);
        Check(projector.Build([counterexample], [Seen(crones[0])], "Monsters", catalog: catalog).PackageHints!.All(h => !h.AutoFill), "Cached counterexample ignored by curated auto-fill.");
        var gn = StartingDeckRules.EvaluateObservedDeck([]) with { GoldenNekker = new("GN", ConstraintState.Confirmed, "Fixture") };
        edits.Include(cards["202282"], 1);
        var sigvald = projector.Build([], [], "Skellige", edits: edits, catalog: catalog);
        Check(sigvald.PackageHints!.Count == 4 && sigvald.PackageHints.All(h => !h.AutoFill) && sigvald.ObservedCopies == 0,
            "Sparse Sigvald fallback should offer four candidates, not force all four.");
        Check(projector.Build([], [], "Skellige", edits: edits, catalog: catalog, constraints: gn).PackageHints!.All(h => h.Card.Provision <= 9),
            "Golden Nekker restriction bypassed by package suggestion.");
        var renfri = gn with { GoldenNekker = new("GN", ConstraintState.Unknown, ""), Renfri = new("Renfri", ConstraintState.Confirmed, "Fixture") };
        Check(projector.Build([], [], "Skellige", edits: edits, catalog: catalog, constraints: renfri).PackageHints!.All(h => h.Card.Kind == CardKind.Unit),
            "Renfri restriction bypassed by package suggestion.");
        var pin = Full("pin", "Skellige", cards["202282"]);
        Check(projector.Build([], [], "Skellige", pin, edits, catalog: catalog).PackageHints!.Count == 0, "Full-deck pin overridden by curated package.");
    }

    private static void AuditCache(string root, IReadOnlyList<CardDefinition> catalog)
    {
        var library = DeckLibrary.Load(Path.Combine(root, "GwentCompanion/cache/deck-library.json"));
        var complete = library.Decks.Where(DeckMetaAnalyzer.IsComplete).GroupBy(DeckMetaAnalyzer.CompositionKey).Select(g => g.First()).ToArray();
        var names = catalog.ToDictionary(c => c.Id, c => c.Name);
        var pairs = new[] { ("202282", "202277"), ("202282", "202456"), ("202282", "113320"), ("202282", "203246"),
            ("132206", "132207"), ("132206", "132208"), ("132207", "132206"), ("132207", "132208"), ("132208", "132206"), ("132208", "132207") };
        var rows = pairs.Select(p => new { Source = names[p.Item1], Target = names[p.Item2],
            SourceLists = complete.Count(d => d.CountOf(p.Item1) > 0),
            JointLists = complete.Count(d => d.CountOf(p.Item1) > 0 && d.CountOf(p.Item2) > 0) }).ToArray();
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.21-package-audit.json"), JsonSerializer.Serialize(new {
            UniqueCompleteLists = complete.Length, Rows = rows,
            Note = "Complete compositions deduplicated; zero source lists means unknown, not zero likelihood. Curated hints are separate from these counts." },
            new JsonSerializerOptions { WriteIndented = true }));
        foreach (var row in rows) Console.WriteLine($"  Cache: {row.Source} -> {row.Target}: {row.JointLists}/{row.SourceLists} complete source lists.");
    }
}
