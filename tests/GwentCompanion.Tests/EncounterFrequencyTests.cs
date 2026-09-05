using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

internal static class EncounterFrequencyTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run(string root)
    {
        var cards = Enumerable.Range(0, 25).Select(i => new ObservedCard(
            new CardDefinition("freq" + i, "Card " + i, "Monsters", CardKind.Unit, 4), CardProvenance.ConfirmedStartingDeck, 1, At)).ToArray();
        LearnedOpponentEncounter Encounter(string id, int? mmr = null) => new(id, At, "Monsters", "White Frost", 15, null, 25, 25, cards, [],
            OpponentProvisionCalculator.Calculate(cards, 165), MatchMmr: mmr);
        var store = new OpponentDeckMemoryStore();
        var first = store.Record(Encounter("match-1", 2400), "Deck", reviewedComplete: true);
        var again = store.Record(Encounter("match-1", 2400) with { At = At.AddDays(30) }, "Renamed", reviewedComplete: true);
        Check(store.Records.Count == 1 && again.EncounterCount == 1 && again.LastSeen == At, "Re-save inflated/rejuvenated an encounter.");
        Check(again.Encounters.Single().Patch == "14.8", "Re-saving a match changed its original patch.");
        var twice = store.Record(Encounter("match-2", 2500) with { At = At.AddDays(1) }, "Deck", reviewedComplete: true);
        Check(twice.Id == first.Id && twice.EncounterCount == 2 && store.Records.Count == 1 && twice.FirstSeen == At && twice.LastSeen == At.AddDays(1), "Complete duplicate did not merge distinct match dates.");
        var partialInput = Encounter("partial") with { Cards = cards.Take(3).ToArray(), StartingSize = null };
        var partial = store.Record(partialInput, "Partial");
        Check(store.Records.Count == 2 && partial.EncounterCount == 1, "Matching partial observation was assumed to identify a whole deck.");
        var repeatPartial = store.Record(partialInput, "Partial updated");
        Check(repeatPartial.Id == partial.Id && repeatPartial.EncounterCount == 1 && store.Records.Count == 2, "Partial same-session save was not idempotent.");
        var variantCards = cards.Take(24).Append(cards[24] with { Card = cards[24].Card with { Id = "alternative", Name = "Different" } }).ToArray();
        var variant = store.Record(Encounter("variant") with { Cards = variantCards }, "Variant", reviewedComplete: true);
        Check(variant.Id != first.Id && store.Records.Count == 3, "A one-card variation counted as an exact repeat.");
        var prior = new OpponentEncounterPrior([twice]);
        var definition = prior.CompleteDecks.Single();
        var empty = new OpponentEncounterPrior([]);
        Check(empty.Weight(definition, At) == 1 && prior.Weight(definition, At.AddDays(1)) > 1, "Encounter count failed to affect prior.");
        Check(prior.Weight(definition, At.AddDays(60)) < prior.Weight(definition, At.AddDays(1)), "Old popularity did not decay.");
        var single = first with { Encounters = [Encounter("single", 2400)] };
        var one = new OpponentEncounterPrior([single], 2400);
        Check(one.Weight(definition, At) > new OpponentEncounterPrior([single], 3300).Weight(definition, At), "Different MMR was weighted as locally representative.");
        Check(new OpponentEncounterPrior([single with { Encounters = [Encounter("single")] }], 2400).Weight(definition, At) > 1,
            "Unknown MMR became impossible rather than lower-trust evidence.");
        Check(new OpponentEncounterPrior([single], excludedSessionId: "single").Weight(definition, At) == 1, "Current match fed back into its own frequency prior.");
        Check(one.Weight(definition, At, new("14.7", StrictHistory: true)) == 1, "Future match frequency leaked into historical prediction.");
        Check(one.Weight(definition, At, new("14.8")) == one.Weight(definition, At.AddYears(1), new("14.8")),
            "Fixed patch encounter weighting changed with wall clock.");
        Check(new OpponentEncounterPrior([partial]).CompleteDecks.Count == 0 && new OpponentEncounterPrior([partial]).Weight(definition, At) == 1,
            "Incomplete observations contaminated complete-list prevalence.");
        var many = single with { Encounters = Enumerable.Range(0, 1000).Select(i => Encounter("many-" + i)).ToArray() };
        Check(new OpponentEncounterPrior([many]).Weight(definition, At) <= 2.5, "Local repeated-deck boost was unbounded.");
        var analyzer = new DeckMetaAnalyzer();
        var other = new OpponentEncounterPrior([variant]).CompleteDecks.Single();
        var baseline = analyzer.AnalyzeAt([definition, other], [], "Monsters", null, "White Frost", At);
        var weighted = analyzer.AnalyzeAt([definition, other], [], "Monsters", null, "White Frost", At, encounters: prior);
        Check(weighted.CorpusDecks == baseline.CorpusDecks && weighted.Cards.Single(c => c.Card.Id == "freq24").ConditionalPresence > baseline.Cards.Single(c => c.Card.Id == "freq24").ConditionalPresence,
            "Encounter prior either inflated unique samples or failed to change candidates.");
        Check(analyzer.AnalyzeAt([], [], "Monsters", null, null, At, encounters: prior).CorpusDecks == 1, "Reviewed complete new opponent identity is unavailable for inference.");
        var folder = Path.Combine(root, "GwentCompanion/diagnostics/encounter-frequency-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "memory.json"); store.Save(path);
        var restored = OpponentDeckMemoryStore.Load(path);
        Check(restored.Records.Single(r => r.Id == first.Id).Encounters.Select(e => e.MatchMmr).SequenceEqual(new int?[] { 2400, 2500 }), "Per-match MMR/time records did not round-trip.");
        restored.Save(path); Check(File.Exists(path + ".bak"), "Frequency saves did not retain recovery backup.");
        var invalid = false;
        try { store.Record(Encounter("bad", -1), "Bad", reviewedComplete: true); } catch (InvalidOperationException) { invalid = true; }
        Check(invalid, "Invalid rating was silently recorded.");
        Console.WriteLine("  Exact opponent duplicates count distinct matches; partials/variants remain separate; timestamps, MMR, recency decay, current-match exclusion and bounded weighting passed.");
    }
}
