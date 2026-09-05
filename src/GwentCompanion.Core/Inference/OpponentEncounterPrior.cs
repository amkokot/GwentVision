using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

/// <summary>Local personal encounter prevalence, not a ladder-wide usage estimate.</summary>
public sealed class OpponentEncounterPrior
{
    private readonly IReadOnlyList<(DeckDefinition Deck, LearnedOpponentEncounter Encounter, double Strength)> _examples;
    private readonly int? _currentMmr;
    public IReadOnlyList<DeckDefinition> CompleteDecks { get; }
    public OpponentEncounterPrior(IEnumerable<LearnedOpponentDeck> memories, int? currentMmr = null, string? excludedSessionId = null,
        IEnumerable<DeckDefinition>? library = null)
    {
        _currentMmr = currentMmr;
        var records = memories.ToArray();
        var complete = records.Where(r => r.Complete && r.Cards.Sum(c => c.ObservedCopies) >= 25 && !string.IsNullOrWhiteSpace(r.Faction)).ToArray();
        var definitions = complete.ToDictionary(r => r.Id, r => new DeckDefinition("learned-" + r.Id, r.Name, r.Faction!, r.Leader ?? "", r.LeaderBonus ?? 0,
            r.Cards.Select(c => new DeckCard(c.Card, c.ObservedCopies)).ToArray(),
            Stratagem: r.StratagemId is { } id ? new(id, id, "Neutral", CardKind.Stratagem, 0) : null,
            SourceUpdatedAt: r.UpdatedAt,
            Occurrences: r.Encounters.Where(e => e.Cards.Sum(c => c.ObservedCopies) == r.Cards.Sum(c => c.ObservedCopies))
                .Select(e => new DeckOccurrence("match-" + e.SessionId, e.Patch ?? DeckPatchMetadata.Current(e.At).Label,
                    "OpponentMatch", "Reviewed opponent match", e.At)).ToArray()));
        CompleteDecks = definitions.Values.ToArray();
        var lookup = (library ?? []).GroupBy(DeckLibrary.Fingerprint).ToDictionary(g => g.Key, g => g.First());
        var inferred = records.Where(r => !r.Complete && !r.NeedsReview && r.LibraryFingerprint is not null && lookup.ContainsKey(r.LibraryFingerprint))
            .SelectMany(r => r.Encounters.Select(e => (Deck: lookup[r.LibraryFingerprint!], Encounter: e, Strength: .65)));
        _examples = complete.SelectMany(r => r.Encounters.Select(e => (Deck: definitions[r.Id], Encounter: e, Strength: 1d))).Concat(inferred)
            .Where(x => !x.Encounter.SessionId.Equals(excludedSessionId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Encounter.SessionId, StringComparer.OrdinalIgnoreCase)
            // A session saved under conflicting complete identities is ambiguous, not two wins
            // for prevalence. The same encounter under aliases contributes at most once.
            .Where(g => g.Select(x => DeckMetaAnalyzer.CompositionKey(x.Deck)).Distinct().Count() == 1)
            .Select(g => g.OrderByDescending(x => x.Encounter.At).First()).ToArray();
    }
    public double Weight(DeckDefinition deck, DateTimeOffset now, PatchPredictionContext? context = null)
    {
        context ??= PatchRecency.Current(now);
        double AgeWeight(LearnedOpponentEncounter encounter) => PatchRecency.Resolve(deck with {
            Patches = encounter.Patch is { } patch ? [new(patch, true, "Match patch")] : [],
            Occurrences = [], LastEdited = null, CachedAt = null, SourceUpdatedAt = encounter.At }, context).Weight;
        var key = DeckMetaAnalyzer.CompositionKey(deck);
        var effective = _examples.Where(x => DeckMetaAnalyzer.CompositionKey(x.Deck) == key &&
            (string.IsNullOrWhiteSpace(deck.Leader) || string.IsNullOrWhiteSpace(x.Encounter.StartingLeader) || deck.Leader.Equals(x.Encounter.StartingLeader, StringComparison.OrdinalIgnoreCase)) &&
            (deck.Stratagem is null || x.Encounter.StratagemId is null || deck.Stratagem.Id == x.Encounter.StratagemId))
            .Sum(x => AgeWeight(x.Encounter) * RatingWeight(x.Encounter.MatchMmr) * x.Strength);
        return Math.Min(2.5, 1 + .35 * Math.Log(1 + effective));
    }
    private double RatingWeight(int? recordedMmr) => _currentMmr is null ? 1 : recordedMmr is null ? .5 :
        Math.Exp(-.5 * Math.Pow((recordedMmr.Value - _currentMmr.Value) / 300d, 2));
}
