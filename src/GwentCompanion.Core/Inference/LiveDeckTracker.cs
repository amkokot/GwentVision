using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Core.Inference;

public sealed class LiveDeckTracker
{
    private static readonly string[] KnownFactions =
        ["Monsters", "Nilfgaard", "Northern Realms", "Scoia'tael", "Skellige", "Syndicate"];
    private readonly Dictionary<string, MatchVote> _votes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ObservedCard> _observations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _factionScores = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _factionPriors = new(StringComparer.OrdinalIgnoreCase);
    private string? _faction;

    public LiveDeckTracker(PlayerSide side)
    {
        Side = side;
    }

    public PlayerSide Side { get; }
    public string? Faction => _faction;
    public double FactionConfidence
    {
        get
        {
            if (_faction is null || !_factionScores.TryGetValue(_faction, out var score))
            {
                return 0;
            }

            var total = _factionScores.Values.Sum();
            return total <= 0 ? 0 : score / total;
        }
    }

    public IReadOnlyCollection<ObservedCard> Observations => _observations.Values;
    public bool HasStableFaction => _faction is not null && FactionConfidence >= 0.60;
    public IReadOnlyCollection<ObservedCard> DeckBuildingObservations => _observations.Values
        .Where(item => StartingDeckRules.CountsAgainstStartingDeck(item.Provenance))
        .Where(item => HasStableFaction
            ? !IsFactionException(item.Card)
            : string.Equals(item.Card.Faction, "Neutral", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    public int FactionExceptionCount => _observations.Values.Count(item => IsFactionException(item.Card));

    public bool IsFactionException(CardDefinition card) =>
        HasStableFaction && !FactionCompatibility.IsPlayableBy(card, _faction!);

    public bool Consider(
        MoveHistoryCardCandidate candidate,
        IReadOnlyList<CardArtMatch> matches,
        DateTimeOffset observedAt,
        bool visuallyHovered = false)
    {
        ArgumentNullException.ThrowIfNull(matches);
        var factionPenalty = _faction is null
            ? 0
            : 0.07 * Math.Clamp(FactionConfidence, 0.45, 1);
        var eligible = matches
            .Select(match => new SoftMatch(
                match,
                match.Distance + (_faction is not null && !FactionCompatibility.IsPlayableBy(match.Card, _faction)
                    ? factionPenalty
                    : 0)))
            .OrderBy(match => match.AdjustedDistance)
            .ToArray();
        var minimumCardness = visuallyHovered ? 0.32 : 0.35;
        if (eligible.Length < 2 || candidate.Score < minimumCardness)
        {
            return false;
        }

        var best = eligible[0].Match;
        var gap = eligible[1].AdjustedDistance - eligible[0].AdjustedDistance;
        var normalMatch = best.Distance <= 0.48 && gap >= 0.08;
        var hoveredMatch = visuallyHovered && best.Distance <= 0.62 && gap >= 0.12;
        var animationTolerantMatch = candidate.Score >= 0.35 && best.Distance <= 0.62 && gap >= 0.13;
        if (!normalMatch && !hoveredMatch && !animationTolerantMatch)
        {
            return false;
        }

        if (!_votes.TryGetValue(best.Card.Id, out var vote))
        {
            vote = new MatchVote(best.Card);
            _votes.Add(best.Card.Id, vote);
        }

        if (!vote.Add(best.Distance, gap, observedAt))
        {
            return false;
        }
        var exceptionallyClear = (best.Distance <= 0.32 && gap >= 0.15) ||
                                 hoveredMatch ||
                                 animationTolerantMatch;
        if (!exceptionallyClear && (vote.Count < 2 || vote.AverageDistance > 0.45))
        {
            return false;
        }

        var confidence = Math.Clamp(
            0.72 + (0.48 - vote.AverageDistance) + Math.Min(0.16, vote.AverageGap),
            0.72,
            0.98);
        var observation = new ObservedCard(
            best.Card,
            CardProvenance.ProbableStartingDeck,
            confidence,
            observedAt,
            $"Move History confirmed across {vote.Count} sample(s); " +
            $"mean distance {vote.AverageDistance:F3}, mean gap {vote.AverageGap:F3}",
            _observations.TryGetValue(best.Card.Id, out var current) ? current.ObservedCopies : 1);
        var changed = !_observations.TryGetValue(best.Card.Id, out var existing) ||
                      observation.Confidence > existing.Confidence + 0.005;
        _observations[best.Card.Id] = observation;
        var previousFaction = _faction;
        UpdateFactionHypothesis();
        return changed || !string.Equals(previousFaction, _faction, StringComparison.OrdinalIgnoreCase);
    }

    public bool SetObservedCopyLowerBound(string cardId, int copies, string source = "simultaneous Move History copies")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        if (!_observations.TryGetValue(cardId, out var observation))
        {
            return false;
        }

        var legalMaximum = observation.Card.IsGold ? 1 : 2;
        // Daerlan doubles itself at setup. Two visible copies only establish one
        // original; even this remains a lower bound because later copying exists.
        if (observation.Card.Id == "162301") copies = (int)Math.Ceiling(copies / 2d);
        var bounded = Math.Clamp(copies, 1, legalMaximum);
        if (bounded <= observation.ObservedCopies)
        {
            return false;
        }

        _observations[cardId] = observation with
        {
            ObservedCopies = bounded,
            Evidence = observation.Evidence + $"; at least {bounded} {source} visible",
        };
        return true;
    }

    public bool SetProvenance(string cardId, CardProvenance provenance, string evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        if (!_observations.TryGetValue(cardId, out var observation) ||
            observation.Provenance == provenance)
        {
            return false;
        }

        _observations[cardId] = observation with
        {
            Provenance = provenance,
            Evidence = observation.Evidence + "; " + evidence,
        };
        UpdateFactionHypothesis();
        return true;
    }

    public bool ConsiderDirectPlay(
        CardDefinition card,
        double confidence,
        DateTimeOffset observedAt,
        string evidence,
        CardProvenance provenance = CardProvenance.ProbableStartingDeck)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        _observations.TryGetValue(card.Id, out var existing);
        // A later board sighting cannot erase stronger play/provenance evidence.
        if (existing is not null && provenance == CardProvenance.Unknown && existing.Provenance != CardProvenance.Unknown)
            return false;
        var observation = new ObservedCard(
            card,
            provenance,
            Math.Clamp(confidence, 0, 1),
            observedAt,
            evidence + " Created, copied, or stolen cards remain possible until corroborated.",
            existing?.ObservedCopies ?? 1);
        var changed = existing is null ||
                      (existing.Provenance == CardProvenance.Unknown && provenance != CardProvenance.Unknown) ||
                      observation.Confidence > existing.Confidence + 0.005;
        if (changed)
        {
            _observations[card.Id] = observation;
        }

        var previousFaction = _faction;
        UpdateFactionHypothesis();
        return changed || !string.Equals(previousFaction, _faction, StringComparison.OrdinalIgnoreCase);
    }

    public void Reset()
    {
        _votes.Clear();
        _observations.Clear();
        _factionScores.Clear();
        _factionPriors.Clear();
        _faction = null;
    }

    public void SetFactionPrior(string faction, double weight = 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(faction);
        if (!KnownFactions.Contains(faction, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unknown GWENT faction: {faction}", nameof(faction));
        }

        if (weight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(weight));
        }

        _factionPriors.Clear();
        _factionPriors[faction] = weight;
        _faction = faction;
        UpdateFactionHypothesis();
    }

    public void ClearFactionPrior()
    {
        _factionPriors.Clear();
        _faction = null;
        UpdateFactionHypothesis();
    }

    private void UpdateFactionHypothesis()
    {
        _factionScores.Clear();
        foreach (var faction in KnownFactions)
        {
            _factionScores[faction] = _factionPriors.GetValueOrDefault(faction);
        }

        // Generated, copied, stolen and unresolved cross-faction cards describe
        // what appeared during the match, not the starting faction. Letting them
        // vote can make a leader-established faction unstable and temporarily drop
        // every faction card from the live provision numerator.
        foreach (var observation in _observations.Values.Where(item=>StartingDeckRules.CountsAgainstStartingDeck(item.Provenance)))
        {
            var playable = FactionCompatibility.PlayableFactions(observation.Card);
            if (playable.Count == 0)
            {
                continue;
            }

            var share = Math.Clamp(observation.Confidence, 0.5, 1) / playable.Count;
            foreach (var faction in playable)
            {
                if (_factionScores.ContainsKey(faction))
                {
                    _factionScores[faction] += share;
                }
            }
        }

        var ranked = _factionScores.OrderByDescending(item => item.Value).ToArray();
        if (ranked.Length == 0 || ranked[0].Value <= 0)
        {
            return;
        }

        if (_faction is null)
        {
            _faction = ranked[0].Key;
            return;
        }

        var challenger = ranked[0];
        var currentScore = _factionScores.GetValueOrDefault(_faction);
        if (string.Equals(challenger.Key, _faction, StringComparison.OrdinalIgnoreCase) ||
            challenger.Value < currentScore + 0.35)
        {
            return;
        }

        var challengerCards = _observations.Values.Count(item =>
            FactionCompatibility.PlayableFactions(item.Card).Contains(challenger.Key));
        if (challengerCards >= 2)
        {
            _faction = challenger.Key;
        }
    }

    private sealed record SoftMatch(CardArtMatch Match, double AdjustedDistance);

    private sealed class MatchVote(CardDefinition card)
    {
        private double _distanceTotal;
        private double _gapTotal;
        private DateTimeOffset? _lastSample;

        public CardDefinition Card { get; } = card;
        public int Count { get; private set; }
        public double AverageDistance => _distanceTotal / Count;
        public double AverageGap => _gapTotal / Count;

        public bool Add(double distance, double gap, DateTimeOffset sampledAt)
        {
            if (_lastSample == sampledAt)
            {
                return false;
            }

            _lastSample = sampledAt;
            Count++;
            _distanceTotal += distance;
            _gapTotal += gap;
            return true;
        }
    }
}
