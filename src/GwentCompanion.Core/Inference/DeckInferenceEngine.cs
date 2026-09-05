using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

public sealed record RankedDeck(
    DeckDefinition Deck,
    double Score,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Contradictions);

public sealed record LikelyUnseenCard(
    CardDefinition Card,
    double PosteriorPresence,
    int SupportingDecks);

public sealed record LikelyLeader(
    string Leader,
    double Posterior,
    int SupportingDecks);

public sealed record ProvisionForecast(
    int ObservedProvisionFloor,
    double ExpectedListedProvisionsUnseen,
    double ExpectedUnseenCardsAtLeastTen,
    int SupportingDecks,
    int? HighestProvisionCapacity);

public sealed class DeckInferenceEngine
{
    public IReadOnlyList<DeckDefinition> CompatibleDecks(
        IEnumerable<DeckDefinition> decks,
        IEnumerable<ObservedCard> observations,
        string? faction,
        ObservedStartingDeckAssessment? constraints = null)
    {
        ArgumentNullException.ThrowIfNull(decks);
        ArgumentNullException.ThrowIfNull(observations);
        var observed = observations
            .Where(item => item.Provenance is CardProvenance.ConfirmedStartingDeck or CardProvenance.ProbableStartingDeck)
            .ToArray();
        return decks
            .Where(deck => string.IsNullOrWhiteSpace(faction) || EqualsIgnoreCase(deck.Faction, faction))
            .Where(deck => SatisfiesLikelyConstraints(deck, constraints))
            .Where(deck => observed.All(item => deck.CountOf(item.Card.Id) >= item.ObservedCopies))
            .OrderBy(deck => deck.RecencyRank)
            .ToArray();
    }

    public IReadOnlyList<RankedDeck> Rank(
        IEnumerable<DeckDefinition> decks,
        IEnumerable<ObservedCard> observations,
        string? faction,
        string? leader,
        int limit = 10)
    {
        ArgumentNullException.ThrowIfNull(decks);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var observed = observations.ToArray();
        var candidates = new List<RankedDeck>();

        foreach (var deck in decks)
        {
            var score = -0.12 * deck.RecencyRank;
            var evidence = new List<string>();
            var contradictions = new List<string>();

            if (!string.IsNullOrWhiteSpace(faction))
            {
                if (EqualsIgnoreCase(deck.Faction, faction))
                {
                    score += 3;
                    evidence.Add($"Faction: {deck.Faction}");
                }
                else
                {
                    score -= 20;
                    contradictions.Add($"Faction mismatch: {deck.Faction}");
                }
            }

            if (!string.IsNullOrWhiteSpace(leader) && !string.IsNullOrWhiteSpace(deck.Leader))
            {
                if (EqualsIgnoreCase(deck.Leader, leader))
                {
                    score += 4;
                    evidence.Add($"Leader: {deck.Leader}");
                }
                else
                {
                    score -= 7;
                    contradictions.Add($"Leader mismatch: {deck.Leader}");
                }
            }

            foreach (var observation in observed)
            {
                var copies = deck.CountOf(observation.Card.Id);
                if (copies >= observation.ObservedCopies)
                {
                    var weight = observation.Provenance switch
                    {
                        CardProvenance.ConfirmedStartingDeck => 3.2,
                        CardProvenance.ProbableStartingDeck => 2.5,
                        CardProvenance.Unknown => 1.2,
                        _ => 0.35,
                    };
                    score += weight * Math.Clamp(observation.Confidence, 0, 1);
                    evidence.Add($"Contains {observation.Card.Name}");
                    continue;
                }

                if (observation.Provenance == CardProvenance.ConfirmedStartingDeck)
                {
                    score -= 8 * Math.Clamp(observation.Confidence, 0, 1);
                    contradictions.Add($"Missing confirmed card: {observation.Card.Name}");
                }
                else if (observation.Provenance == CardProvenance.ProbableStartingDeck)
                {
                    score -= 2.5 * Math.Clamp(observation.Confidence, 0, 1);
                    contradictions.Add($"Does not list probable card: {observation.Card.Name}");
                }
            }

            candidates.Add(new RankedDeck(deck, score, evidence, contradictions));
        }

        return candidates
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Deck.RecencyRank)
            .Take(limit)
            .ToArray();
    }

    public IReadOnlyList<LikelyUnseenCard> RankLikelyUnseenCards(
        IEnumerable<DeckDefinition> decks,
        IEnumerable<ObservedCard> observations,
        string? faction,
        int limit = 10,
        ObservedStartingDeckAssessment? constraints = null)
    {
        ArgumentNullException.ThrowIfNull(decks);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var observed = observations.ToArray();
        var observedCopies = observed
            .GroupBy(item => item.Card.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Max(item => item.ObservedCopies),
                StringComparer.OrdinalIgnoreCase);
        var corpus = decks
            .Where(deck => string.IsNullOrWhiteSpace(faction) || EqualsIgnoreCase(deck.Faction, faction))
            .Where(deck => SatisfiesLikelyConstraints(deck, constraints))
            .ToArray();
        if (corpus.Length == 0)
        {
            return Array.Empty<LikelyUnseenCard>();
        }

        var weightedDecks = corpus.Select(deck =>
        {
            return (Deck: deck, Weight: PosteriorWeight(deck, observed));
        }).ToArray();
        var totalWeight = weightedDecks.Sum(item => item.Weight);
        if (totalWeight <= 0)
        {
            return Array.Empty<LikelyUnseenCard>();
        }

        return weightedDecks
            .SelectMany(item => item.Deck.Cards
                .Where(card => !observedCopies.ContainsKey(card.Card.Id))
                .Select(card => (card.Card, item.Weight, DeckId: item.Deck.Id)))
            .GroupBy(item => item.Card.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => new LikelyUnseenCard(
                group.First().Card,
                Math.Clamp(group.Sum(item => item.Weight) / totalWeight, 0, 1),
                group.Select(item => item.DeckId).Distinct(StringComparer.OrdinalIgnoreCase).Count()))
            .OrderByDescending(item => item.PosteriorPresence)
            .ThenByDescending(item => item.SupportingDecks)
            .ThenByDescending(item => item.Card.Provision)
            .Take(limit)
            .ToArray();
    }

    public IReadOnlyList<LikelyLeader> RankLikelyLeaders(
        IEnumerable<DeckDefinition> decks,
        IEnumerable<ObservedCard> observations,
        string? faction,
        int limit = 3)
    {
        ArgumentNullException.ThrowIfNull(decks);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var observed = observations.ToArray();
        var corpus = decks
            .Where(deck => !string.IsNullOrWhiteSpace(deck.Leader))
            .Where(deck => string.IsNullOrWhiteSpace(faction) || EqualsIgnoreCase(deck.Faction, faction))
            .Select(deck => (Deck: deck, Weight: PosteriorWeight(deck, observed)))
            .ToArray();
        var total = corpus.Sum(item => item.Weight);
        if (total <= 0)
        {
            return Array.Empty<LikelyLeader>();
        }

        return corpus
            .GroupBy(item => item.Deck.Leader, StringComparer.OrdinalIgnoreCase)
            .Select(group => new LikelyLeader(
                group.Key,
                Math.Clamp(group.Sum(item => item.Weight) / total, 0, 1),
                group.Select(item => item.Deck.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()))
            .OrderByDescending(item => item.Posterior)
            .ThenByDescending(item => item.SupportingDecks)
            .Take(limit)
            .ToArray();
    }

    public ProvisionForecast ForecastProvisions(
        IEnumerable<DeckDefinition> decks,
        IEnumerable<ObservedCard> observations,
        string? faction,
        ObservedStartingDeckAssessment? constraints = null)
    {
        ArgumentNullException.ThrowIfNull(decks);
        ArgumentNullException.ThrowIfNull(observations);
        var observed = observations.ToArray();
        var observedCopies = observed
            .GroupBy(item => item.Card.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Max(item => item.ObservedCopies),
                StringComparer.OrdinalIgnoreCase);
        var corpus = decks
            .Where(deck => string.IsNullOrWhiteSpace(faction) || EqualsIgnoreCase(deck.Faction, faction))
            .Where(deck => SatisfiesLikelyConstraints(deck, constraints))
            .Select(deck => (Deck: deck, Weight: PosteriorWeight(deck, observed)))
            .Where(item => item.Weight > 0)
            .ToArray();
        var floor = StartingDeckRules.ProbableProvisionLowerBound(observed);
        if (corpus.Length == 0)
        {
            return new ProvisionForecast(floor, 0, 0, 0, null);
        }

        var totalWeight = corpus.Sum(item => item.Weight);
        var expectedUnseen = corpus.Sum(item => item.Weight * item.Deck.Cards
            .Sum(card => card.Card.Provision * Math.Max(0, card.Count - observedCopies.GetValueOrDefault(card.Card.Id)))) / totalWeight;
        var expectedBigCards = corpus.Sum(item => item.Weight * item.Deck.Cards
            .Where(card => card.Card.Provision >= 10)
            .Sum(card => Math.Max(0, card.Count - observedCopies.GetValueOrDefault(card.Card.Id)))) / totalWeight;
        var highestCapacity = corpus.Max(item => 150 + item.Deck.LeaderProvisionBonus);
        return new ProvisionForecast(
            floor,
            expectedUnseen,
            expectedBigCards,
            corpus.Length,
            highestCapacity);
    }

    private static double PosteriorWeight(DeckDefinition deck, IReadOnlyCollection<ObservedCard> observed)
    {
        var weight = Math.Exp(-Math.Min(600, Math.Max(0, deck.RecencyRank)) / 180d);
        foreach (var observation in observed)
        {
            var confidence = Math.Clamp(observation.Confidence, 0.35, 1);
            weight *= deck.CountOf(observation.Card.Id) >= observation.ObservedCopies
                ? 1.8 + confidence * 1.8
                : 0.30 + (1 - confidence) * 0.35;
        }

        return weight;
    }

    private static bool SatisfiesLikelyConstraints(
        DeckDefinition deck,
        ObservedStartingDeckAssessment? constraints)
    {
        if (constraints is null)
        {
            return true;
        }

        return DeckMetaAnalyzer.Allowed(deck, constraints);
    }

    private static bool RequiresConstraint(ConstraintAssessment assessment) =>
        assessment.State is ConstraintState.Likely or ConstraintState.Confirmed;

    private static bool EqualsIgnoreCase(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
