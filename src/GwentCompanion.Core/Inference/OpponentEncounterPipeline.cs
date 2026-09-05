using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

public sealed record EncounterCaptureResult(LearnedOpponentDeck Record, DeckDefinition? LibraryObservation, bool NewEncounter);

/// <summary>Conservative association, not a claim to know unobserved cards. No match means a durable review draft.</summary>
public static class OpponentEncounterPipeline
{
    public static EncounterCaptureResult Capture(OpponentDeckMemoryStore memory, LearnedOpponentEncounter encounter,
        IReadOnlyList<DeckDefinition> library)
    {
        if (encounter.PostMatchMmr?.RatingAfter is null && encounter.PostMatchRank is null)
            throw new InvalidOperationException("Wait for a confirmed post-match rating result.");
        var previous = memory.Records.FirstOrDefault(r => r.Encounters.Any(e => e.SessionId == encounter.SessionId));
        var corpus = library.Where(d => encounter.Faction is null || d.Faction == encounter.Faction)
            .GroupBy(DeckLibrary.Fingerprint).Select(g => g.First()).ToArray();
        var eligible = corpus.Where(d => d.CardCount >= 25 && HeaderMatches(d, encounter) && CompositionMatches(d, encounter) &&
            encounter.Cards.All(c => d.CountOf(c.Card.Id) >= c.ObservedCopies) && ConditionsMatch(d, encounter)).ToArray();
        // Unseen substitutions are genuinely ambiguous; no recency/pin tie-break can make them observations.
        DeckDefinition? match = previous?.LibraryFingerprint is { } fingerprint
            ? library.FirstOrDefault(d => DeckLibrary.Fingerprint(d) == fingerprint)
            : previous is null && eligible.Length == 1 && StrongEvidence(encounter, corpus) ? eligible[0] : null;
        // A prior close-analogue association remains a variant association when the same
        // session is reloaded; the saved fingerprint alone does not turn it into an exact list.
        var exact = match is not null && encounter.Cards.All(c => match.CountOf(c.Card.Id) >= c.ObservedCopies) &&
            ConditionsMatch(match, encounter) && CompositionMatches(match, encounter);
        if (previous is null && match is null && StrongEvidence(encounter, corpus))
            match = DominantCloseAnalogue(encounter, corpus);
        string? mergeId = previous?.Id;
        if (previous is null && match is not null)
        {
            var linked = memory.Records.Where(r => (r.LibraryFingerprint == DeckLibrary.Fingerprint(match) ||
                r.Complete && r.Faction == match.Faction && r.Leader == match.Leader && r.StratagemId == match.Stratagem?.Id &&
                r.Cards.Sum(c => c.ObservedCopies) == match.CardCount && r.Cards.All(c => match.CountOf(c.Card.Id) == c.ObservedCopies)) &&
                OpponentDeckMemoryStore.Match(r, encounter).Possible).ToArray();
            if (linked.Length == 1) mergeId = linked[0].Id;
        }
        if (previous is null && mergeId is null && StrongEvidence(encounter, corpus))
        {
            var partials = memory.FindMatches(encounter).Where(m => m.Possible && m.Deck.LibraryFingerprint is null &&
                !m.Deck.Complete && m.SharedCopies >= 10 &&
                PartialSpendCoverage(encounter, m.Deck) >= DeckVariationSimilarity.Threshold &&
                (match is null || m.Deck.Cards.All(c => match.CountOf(c.Card.Id) >= c.ObservedCopies))).ToArray();
            if (partials.Length == 1) mergeId = partials[0].Deck.Id;
        }
        var observedCopies = encounter.Cards.Sum(c => c.ObservedCopies);
        var sharedCopies = match is null ? 0 : encounter.Cards.Sum(c => Math.Min(c.ObservedCopies, match.CountOf(c.Card.Id)));
        var overlap = match is null ? null : new DeckVariationSimilarity(corpus, encounter.Cards.Select(c => c.Card))
            .Compare(encounter.Cards.Select(c => new DeckCard(c.Card, c.ObservedCopies)), match.Cards);
        var reason = match is null ? "Incomplete/ambiguous composition; no full library identity assumed." : exact
            ? $"Unique compatible library list after {observedCopies} observed copies; inferred association, unseen cards not verified."
            : $"Dominant close library analogue: {overlap!.SharedProvisions}/{overlap.LeftProvisions}p of observed spend overlaps ({overlap.SharedProvisions / (double)overlap.LeftProvisions:P1}); {sharedCopies}/{observedCopies} observed copies; {observedCopies - sharedCopies} observed deviation(s). Partial analogue, not a verified full variation; unseen cards unverified.";
        var record = memory.Capture(encounter, match?.Name ?? $"{encounter.Faction ?? "Opponent"} · {encounter.At:yyyy-MM-dd HH:mm}",
            mergeId, match is null ? null : DeckLibrary.Fingerprint(match), reason);
        var savedEncounter = record.Encounters.First(e => e.SessionId == encounter.SessionId);
        DeckDefinition? observation = null;
        if (match is not null)
        {
            var occurrence = new DeckOccurrence("match-" + encounter.SessionId, savedEncounter.Patch!, exact ? "OpponentMatchInferred" : "OpponentMatchAnalogueInferred",
                reason, savedEncounter.At, Inferred: true);
            observation = match with { Occurrences = DeckOccurrences.Merge(match.Occurrences, [occurrence]),
                Patches = DeckPatchMetadata.Merge(match.Patches, DeckOccurrences.Tags([occurrence])) };
        }
        return new(record, observation, previous is null);
    }
    private static bool StrongEvidence(LearnedOpponentEncounter encounter, IReadOnlyList<DeckDefinition> corpus)
    {
        var cards = encounter.Cards;
        if (cards.Sum(c => c.ObservedCopies) < 12 || cards.Count < 8 || encounter.Faction is null) return false;
        // At least two reasonably distinctive identities; generic faction staples alone are weak evidence.
        return cards.Count(c => corpus.Count == 0 || corpus.Count(d => d.CountOf(c.Card.Id) > 0) / (double)corpus.Count <= .35) >= 2;
    }
    private static double PartialSpendCoverage(LearnedOpponentEncounter encounter, LearnedOpponentDeck previous)
    {
        var metric = new DeckVariationSimilarity([], previous.Cards.Select(c => c.Card).Concat(encounter.Cards.Select(c => c.Card)));
        var overlap = metric.Compare(encounter.Cards.Select(c => new DeckCard(c.Card, c.ObservedCopies)),
            previous.Cards.Select(c => new DeckCard(c.Card, c.ObservedCopies)));
        // Partial views may have different coverage. This only joins compatible evidence drafts,
        // never establishes a complete-list identity or a Library variation membership.
        return overlap.SharedProvisions / (double)Math.Max(1, Math.Min(overlap.LeftProvisions, overlap.RightProvisions));
    }
    private static DeckDefinition? DominantCloseAnalogue(LearnedOpponentEncounter encounter, IReadOnlyList<DeckDefinition> corpus)
    {
        var observed = encounter.Cards.Sum(card => card.ObservedCopies);
        var maximumMissing = Math.Max(2, (int)Math.Floor(observed * .15));
        var metric = new DeckVariationSimilarity(corpus, encounter.Cards.Select(c => c.Card));
        var observedCards = encounter.Cards.Select(c => new DeckCard(c.Card, c.ObservedCopies)).ToArray();
        var ranked = corpus.Where(deck => deck.CardCount >= 25 && HeaderMatches(deck, encounter) && ConstructionCompatible(deck, encounter) && CompositionMatches(deck, encounter))
            .Select(deck =>
            {
                var shared = encounter.Cards.Sum(card => Math.Min(card.ObservedCopies, deck.CountOf(card.Card.Id)));
                var overlap = metric.Compare(observedCards, deck.Cards);
                return (Deck: deck, Shared: shared, Missing: observed - shared,
                    Coverage: overlap.SharedProvisions / (double)Math.Max(1, overlap.LeftProvisions));
            })
            .Where(item => item.Missing <= maximumMissing && item.Shared >= 12 && item.Coverage >= DeckVariationSimilarity.Threshold)
            .OrderByDescending(item => item.Coverage).ThenBy(item => item.Missing)
            .ThenByDescending(item => DeckMetaAnalyzer.SourceDate(item.Deck)).ToArray();
        if (ranked.Length == 0) return null;
        if (ranked.Length > 1 && ranked[0].Coverage - ranked[1].Coverage <= .03)
            return null; // Two near-ties are still an ambiguous family, so keep review available.
        return ranked[0].Deck;
    }
    private static bool ConstructionCompatible(DeckDefinition deck, LearnedOpponentEncounter encounter)
    {
        var knowledge = new OpponentKnowledge();
        foreach (var condition in encounter.Conditions)
            if (condition.Suggested) knowledge.Suggest(condition.Condition, condition.At, condition.Evidence);
            else knowledge.Resolve(condition.Condition, condition.At, condition.Evidence);
        return DeckMetaAnalyzer.Allowed(deck, knowledge.Assess(encounter.Cards));
    }
    private static bool CompositionMatches(DeckDefinition deck, LearnedOpponentEncounter encounter) =>
        (encounter.CompositionClues ?? []).All(clue => Math.Abs(clue.CountIn(deck) - clue.Count) <= (clue.Confidence >= .9 ? 0 : 1));
    private static bool HeaderMatches(DeckDefinition deck, LearnedOpponentEncounter e) =>
        (e.StartingLeader is null || deck.Leader.Equals(e.StartingLeader, StringComparison.OrdinalIgnoreCase)) &&
        (e.StratagemId is null || deck.Stratagem?.Id == e.StratagemId) &&
        (e.StartingSize is null || e.StartingSize == deck.CardCount) && deck.CardCount >= e.MinimumSize;
    private static bool ConditionsMatch(DeckDefinition deck, LearnedOpponentEncounter e)
    {
        // Reuse validated memory constraints for headers, resolved archetypes and union provision feasibility.
        var reference = new LearnedOpponentDeck("candidate", deck.Name, e.At,
            [new("reference", e.At, deck.Faction, deck.Leader, deck.LeaderProvisionBonus, deck.Stratagem?.Id,
                deck.CardCount, deck.CardCount, deck.Cards.Select(c => new ObservedCard(c.Card, CardProvenance.ProbableStartingDeck, 1, e.At, ObservedCopies: c.Count)).ToArray(),
                [], OpponentProvisionCalculator.Calculate([], 150 + deck.LeaderProvisionBonus, deck.CardCount))], Complete: true);
        return OpponentDeckMemoryStore.Match(reference, e).Possible;
    }
}
