using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

internal static class AutoPopulationTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
    private static CardDefinition Card(string id, int provision = 4) => new(id, id, "Monsters", CardKind.Unit, provision);
    private static ObservedCard Seen(CardDefinition card, int copies = 1) => new(card, CardProvenance.ProbableStartingDeck, .99, At, "Fixture", copies);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static DeckDefinition Deck(string id, params CardDefinition[] cards) => new(id, id, "Monsters", "White Frost", 15,
        cards.Concat(Enumerable.Range(0, Math.Max(0, 25 - cards.Length)).Select(i => Card(id + " filler " + i)))
            .GroupBy(c => c.Id).Select(g => new DeckCard(g.First(), g.Count())).ToArray(), SourceUpdatedAt: At);

    public static void Run(string root)
    {
        var a = Card("anchor"); var old = Card("first guess"); var replacement = Card("new guess");
        var trigger = Card("new strategy"); var corroborator = Card("new strategy 2");
        var decks = Enumerable.Range(0, 6).Select(i => Deck("source" + i, i < 3 ? [a, old] : [a, replacement, trigger, corroborator])).ToArray();
        var projector = new OpponentDeckProjector();
        bool Has(OpponentDeckProjection p, CardDefinition c) => p.Slots.Any(s => s.Card?.Id == c.Id && s.State == DeckSlotState.Predicted);
        var early = projector.Build(decks, [Seen(a)], "Monsters");
        Check(Has(early, old) && early.Slots.Where(s => s.State == DeckSlotState.Predicted).All(s => s.Reason.Contains("Tentative") && s.ModelShare >= .35),
            "First observation failed to produce explicitly uncertain early guesses.");
        var late = projector.Build(decks, [Seen(a), Seen(trigger), Seen(corroborator)], "Monsters");
        Check(!Has(late, old) && Has(late, replacement), "New observed strategy did not replace a falling guess.");
        Check(late.ObservedCopies == 3 && late.Slots.Where(s => s.State == DeckSlotState.Observed).Sum(s => s.Card!.Provision) == 12, "Guesses charged observed provisions.");
        Check(projector.Build(decks, [Seen(a)], null).UnknownSlots == 24, "Unknown faction filled a mixed deck.");
        Check(projector.Build([], [Seen(a)], "Monsters").UnknownSlots == 24, "No-data catalog rule invented an automatic card.");
        Check(projector.Build([decks[0]], [Seen(a)], "Monsters").UnknownSlots == 0, "A matching sole cached example should be usable as a tentative list immediately.");
        Check(projector.Build([decks[0]], [], "Monsters", startingLeader: "White Frost").UnknownSlots == 0, "Known leader did not permit immediate tentative fill.");
        var edits = new DeckProjectionEdits(); edits.Exclude(old, 1);
        Check(!Has(projector.Build(decks, [Seen(a)], "Monsters", edits: edits), old), "Dismissed early guess bounced back.");
        edits.Include(old, 1);
        Check(projector.Build(decks, [Seen(a), Seen(trigger), Seen(corroborator)], "Monsters", edits: edits).Slots.Any(s => s.Card?.Id == old.Id && s.State == DeckSlotState.Selected),
            "A statistical re-ranking silently erased an explicit user choice.");
        var neutral = Card("neutral") with { Faction = "Neutral" };
        var big = Card("expensive", 12); var special = Card("special") with { Kind = CardKind.Special };
        var validCards = Enumerable.Range(0, 25).Select(i => Card("valid " + i, 5)).ToArray();
        var valid = Deck("valid", validCards);
        var invalid = Deck("invalid", validCards.Take(18).Concat(new[] { neutral, big, special, a, a }).ToArray());
        var rules = StartingDeckRules.EvaluateObservedDeck([]);
        foreach (var condition in new[] { "Devotion", "GN", "Singleton", "Renfri", "Musicians" })
        {
            var required = new ConstraintAssessment(condition, ConstraintState.Confirmed, "Resolved fixture effect");
            var constraints = condition switch
            {
                "Devotion" => rules with { Devotion = required }, "GN" => rules with { GoldenNekker = required },
                "Singleton" => rules with { Shupe = required }, "Renfri" => rules with { Renfri = required },
                _ => rules with { Musicians = required }
            };
            var result = projector.Build([invalid, valid], [], "Monsters", constraints: constraints, startingLeader: "White Frost");
            Check(result.Slots.All(s => s.Card?.Id != neutral.Id && s.Card?.Id != big.Id && s.Card?.Id != special.Id), "Hard constraints failed to remove invalid list guesses: " + condition);
        }
        var beforeRule = projector.Build([invalid], [], "Monsters", startingLeader: "White Frost");
        Check(Has(beforeRule, neutral), "Constraint transition fixture did not initially fill a neutral.");
        var afterRule = projector.Build([invalid, valid], [], "Monsters", startingLeader: "White Frost", constraints: rules with { Devotion = new("Devotion", ConstraintState.Confirmed, "Effect resolved") });
        Check(!Has(afterRule, neutral), "A formerly guessed neutral survived confirmed Devotion.");
        var leader = Card("leader", 15) with { Name = "White Frost", Kind = CardKind.Leader };
        var costly = Deck("costly", Enumerable.Range(0, 25).Select(i => Card("costly" + i, 10)).ToArray());
        var budget = projector.Build([costly], [], "Monsters", startingLeader: "White Frost", catalog: [leader]);
        Check(budget.Slots.Where(s => s.Card is not null).Sum(s => s.Card!.Provision) + 4 * budget.UnknownSlots <= 165, "Early fill exceeded known leader budget.");
        ReferenceVariants(projector, validCards);
        MeasureCache(root);
        Console.WriteLine("  Early tentative fill; evidence-driven replacement; hard-rule removal; dismissals, budgets and reference variations passed.");
    }

    private static void ReferenceVariants(OpponentDeckProjector projector, CardDefinition[] common)
    {
        var pin = Deck("pin", common);
        var wrong = Enumerable.Range(0, 3).Select(i => Card("mismatch" + i)).ToArray();
        var little = projector.Build([], wrong.Select(c => Seen(c)), "Monsters", pin);
        var much = projector.Build([], common.Take(15).Select(c => Seen(c)).Concat(wrong.Select(c => Seen(c))), "Monsters", pin);
        Check(little.PinDeviations == 3 && much.PinDeviations == 3 && much.PinInfluence > .5 && little.PinInfluence < .2,
            "Positive reference overlap failed to protect a plausible three-card variant.");
        var generated = wrong.Select(c => Seen(c) with { Provenance = CardProvenance.Created });
        Check(projector.Build([], generated, "Monsters", pin).PinInfluence == .85, "Generated cards weakened the original reference.");
        var near = pin with { Id = "near", Cards = pin.Cards.Take(24).Append(new DeckCard(wrong[0])).ToArray() };
        var far = Deck("far", wrong[0]);
        var prior = new DeckReferencePrior(pin, .6);
        Check(DeckMetaAnalyzer.ReferenceWeight(near, prior) > DeckMetaAnalyzer.ReferenceWeight(far, prior) && DeckMetaAnalyzer.ReferenceWeight(far, null) == 1,
            "Near variants were not preferred by the reference prior.");
        var analyzer = new DeckMetaAnalyzer();
        var baseline = analyzer.AnalyzeAt([near, far], [Seen(wrong[0])], "Monsters", null, "White Frost", At);
        var conditioned = analyzer.AnalyzeAt([near, far], [Seen(wrong[0])], "Monsters", null, "White Frost", At, reference: prior);
        Check(conditioned.RankedDecks.Single(d => d.Deck.Id == near.Id).Score > baseline.RankedDecks.Single(d => d.Deck.Id == near.Id).Score,
            "Reference proximity was not applied to actual model weights.");
        Check(conditioned.CorpusDecks == baseline.CorpusDecks && conditioned.ObservedIdentities == baseline.ObservedIdentities,
            "A reference was inserted as a fake sample or observed card.");
        var incompatible = projector.Build([near, far], [Seen(wrong[0])], "Monsters", pin, startingLeader: "Blood Scent");
        Check(incompatible.PinInfluence == 0, "Wrong original leader retained reference influence.");
    }

    private static void MeasureCache(string root)
    {
        var library = DeckLibrary.Load(Path.Combine(root, "GwentCompanion/cache/deck-library.json"));
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        var decks = library.Decks.Where(DeckMetaAnalyzer.IsComplete).GroupBy(DeckMetaAnalyzer.CompositionKey).Select(g => g.First()).OrderBy(d => d.Id).ToArray();
        var cases = new List<FillCase>(); var projector = new OpponentDeckProjector(); var timer = Stopwatch.StartNew();
        // Bounded, deterministic recent hold-outs per faction as the archive grows.
        // Training still uses the entire deduplicated corpus, never the held list.
        var heldDecks = decks.GroupBy(d => d.Faction).SelectMany(g => g.OrderByDescending(DeckMetaAnalyzer.SourceDate).ThenBy(d => d.Id).Take(8)).ToArray();
        foreach (var held in heldDecks)
        {
            var random = new Random(173);
            var revealed = held.Cards.OrderBy(c => c.Card.Id).Select(c => (Card: c.Card, Order: random.Next())).OrderBy(c => c.Order).Select(c => c.Card).ToArray();
            var training = decks.Where(d => d.Id != held.Id).ToArray();
            foreach (var n in new[] { 0, 1, 3, 6 })
            foreach (var visible in new[] { false, true })
            {
                var p = projector.Build(training, revealed.Take(n).Select(c => Seen(c)), held.Faction,
                    startingLeader: held.Leader, catalog: catalog, startingStratagemId: visible ? held.Stratagem?.Id : null);
                var guesses = p.Slots.Where(s => s.State == DeckSlotState.Predicted).ToArray();
                var hits = guesses.Count(s => held.CountOf(s.Card!.Id) >= s.Copy);
                Check(p.Slots.Count >= 25 && p.ObservedCopies == n, "Held-out projection lost observed identities.");
                cases.Add(new(held.Id, n, visible, guesses.Length, hits, p.UnknownSlots));
            }
        }
        var summary = cases.GroupBy(c => c.Seen).Select(g => new { Seen = g.Key, Cases = g.Count(),
            MeanGuesses = g.Average(c => c.Guesses), MeanUnknown = g.Average(c => c.Unknown),
            FilledPrecision = g.Sum(c => c.Guesses) == 0 ? 0 : g.Sum(c => c.Hits) / (double)g.Sum(c => c.Guesses) }).ToArray();
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.23-auto-fill-evaluation.json"), JsonSerializer.Serialize(new {
            Method = "Eight newest complete compositions per faction held out individually; entire remaining corpus for training; known leader, paired stratagem visible/hidden branches (50/50); 0/1/3/6 deterministic revealed original identities. No early recordings. Fill quantity and error, not pixel recognition accuracy. Near variants may remain in training.",
            Decks = decks.Length, HeldOutDecks = heldDecks.Length, ElapsedSeconds = timer.Elapsed.TotalSeconds, Summary = summary, Cases = cases }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var s in summary) Console.WriteLine($"  {s.Seen} seen: {s.MeanGuesses:F1} tentative unseen slots, {s.MeanUnknown:F1} unknown, {s.FilledPrecision:P1} of auto-filled copies correct (held-out cache).");
    }
    private sealed record FillCase(string Deck, int Seen, bool StratagemVisible, int Guesses, int Hits, int Unknown);
}
