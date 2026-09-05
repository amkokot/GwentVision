using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public enum CandidateEvaluationQuality
{
    Simulated,
    ConditionalSimulation,
    BoardAwareEstimate,
    DirectEstimate,
    Unsupported,
}

public sealed record CandidatePointEvaluation(CardDefinition Card, PlayerSide Side, int? MinimumPoints, int? MaximumPoints,
    CandidateEvaluationQuality Quality, GamePosition Position, GamePosition? After, PlaySearchResult Simulation,
    ApproximatePointEstimate? Estimate, IReadOnlyList<PointContribution> BoardContributions,
    IReadOnlyList<string> Assumptions, IReadOnlyList<string> Unresolved)
{
    public bool HasValue => MaximumPoints is not null;
    public string RangeText => MinimumPoints is null || MaximumPoints is null ? "unresolved" :
        MinimumPoints == MaximumPoints ? $"{MaximumPoints:+0;-0;0}" : $"{MinimumPoints:+0;-0;0}–{MaximumPoints:+0;-0;0}";
}

/// <summary>
/// Public board-snapshot-to-candidate boundary. It stages each candidate as a conditional hand card, attempts the
/// executable state transition first, and falls back to a board-aware bounded estimate without changing the input.
/// </summary>
public sealed class CandidatePointEvaluator
{
    private readonly CardDefinition[] _catalog;
    private readonly IReadOnlyDictionary<string, CardDefinition> _cards;
    private readonly PlayRuleBook _book;
    private readonly ApproximatePointModel _approximate;
    private readonly BoardInteractionPointModel _interactions;
    private readonly Lazy<CardPointHorizonEvaluator> _horizons;
    private readonly ReachCache<CandidatePointEvaluation> _cache;
    private readonly bool _cacheEnabled;
    public long CacheHits => _cache.Hits;
    public int CachedEvaluations => _cache.Count;
    internal CardPointHorizonEvaluator Horizons => _horizons.Value;

    public CandidatePointEvaluator(IEnumerable<CardDefinition> catalog, int cacheCapacity = 0)
    {
        _cache = new(cacheCapacity); _cacheEnabled = cacheCapacity > 0;
        _catalog = catalog.DistinctBy(card => card.Id).ToArray();
        _cards = _catalog.ToDictionary(card => card.Id);
        _book = new(_catalog);
        _approximate = new(_catalog);
        _interactions = new(_catalog);
        _horizons = new(() => new(_catalog, _book));
    }

    public CandidatePointEvaluation Evaluate(GamePosition position, CardDefinition candidate, PlayerSide side,
        string? instanceId = null, bool? original = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GwentCompanion.Core.Vision.ReachWorkScope.Checkpoint();
        var key = _cacheEnabled ? ReachCacheKey.For(position, candidate, side, instanceId, original) : null;
        if (key is not null && _cache.TryGet(key, out var cached)) return cached;
        var value = Calculate(position, candidate, side, instanceId, original, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return key is null ? value : _cache.Add(key, value);
    }

    private CandidatePointEvaluation Calculate(GamePosition position, CardDefinition candidate, PlayerSide side,
        string? instanceId, bool? original, CancellationToken cancellationToken)
    {
        var prepared = StageCandidate(position, candidate, side, instanceId, original);
        var stagedId = instanceId is not null && prepared.Zone(side, CardZone.Hand).Cards.Any(card => card.InstanceId == instanceId)
            ? instanceId
            : prepared.Zone(side, CardZone.Hand).Cards.Last(card => card.CardId == candidate.Id).InstanceId;
        var simulation = new TacticalPlayEngine(_book).ImmediateMaximum(prepared, stagedId, side, cancellationToken: cancellationToken);
        var board = _interactions.Estimate(candidate, prepared, side);
        var placementSide = _book.Rules[candidate.Id].Disloyal ? side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User : side;
        if (candidate.Kind is CardKind.Unit or CardKind.Artifact && Enum.GetValues<BoardRow>().All(row =>
            prepared.Zone(placementSide, CardZone.Board, row).Cards.Length >= 9))
        {
            var unavailable = new ApproximatePointEstimate(0, 0, PointEstimateQuality.Bounded,
                ["No legal placement: both destination rows are full; the candidate cannot currently be played."]);
            return new(candidate, side, 0, 0, CandidateEvaluationQuality.DirectEstimate, prepared, null,
                simulation with { Approximation = unavailable }, unavailable, [], unavailable.Notes, simulation.Missing);
        }
        if (simulation.MaximumPoints is { } exact)
        {
            var conditional = simulation.Assumptions.Any(assumption => !assumption.StartsWith("One-card estimate:", StringComparison.Ordinal)) ||
                simulation.Line.Any(entry => entry.StartsWith("[approx]", StringComparison.Ordinal));
            var candidateRule = _book.Rules[candidate.Id];
            var rangedOutcome = candidateRule.Effect is PlayEffect.CreatePlay or PlayEffect.SpawnPlayChoice or
                PlayEffect.AdjacentTransform or PlayEffect.Fucusya or PlayEffect.Artaud or PlayEffect.TorresFounder or
                PlayEffect.TorresPriest or PlayEffect.Emhyr or PlayEffect.BattleStations or PlayEffect.Abordage or
                PlayEffect.Birna or PlayEffect.GeraltProfessional or PlayEffect.ChampionCharge or PlayEffect.CoupDeGrace or
                PlayEffect.Sihil or PlayEffect.NovigradianJustice or PlayEffect.PhilippaBlindFury or PlayEffect.GeraltAard ||
                candidateRule.Effect == PlayEffect.GreedyAgent && (simulation.FavorableRandomness ||
                    candidateRule.Argument is "braathens" or "lydia" or "crow-messenger") ||
                candidateRule.Effect == PlayEffect.Tutor && !prepared.Zone(side, CardZone.Deck).Complete ||
                candidateRule.Effect == PlayEffect.GravePlay && !prepared.Zone(side, CardZone.Graveyard).Complete;
            var minimum = rangedOutcome ? simulation.MinimumPoints ?? exact : exact;
            return new(candidate, side, minimum, exact,
                conditional ? CandidateEvaluationQuality.ConditionalSimulation : CandidateEvaluationQuality.Simulated,
                prepared, simulation.After, simulation, null, board.Contributions,
                simulation.Assumptions, simulation.Missing);
        }

        var projectedRule = _book.ForProjection.Rules[candidate.Id];
        // A body-only extraction must not replace a better board-aware fallback. Otherwise try
        // the same state machine with explicitly approximate, cached action plans for missing clauses.
        if (projectedRule.Projection?.HasActions == true || projectedRule.Projection is null)
        {
            var projected = new TacticalPlayEngine(_book.ForProjection).ImmediateMaximum(prepared, stagedId, side,
                branchLimit: 768, cancellationToken: cancellationToken);
            if (projected.BestModeledPoints is { } maximum && projected.After is not null)
            {
                var minimum = projected.FavorableRandomness ? projected.MinimumModeledPoints ?? maximum : maximum;
                var notes = projected.Assumptions.Concat(projected.Missing).Append(
                    "* Greedy extracted-action projection, not an exact total or a guaranteed upper bound; omitted abilities remain listed.").Distinct().ToArray();
                var projectionEstimate = new ApproximatePointEstimate(minimum, maximum, PointEstimateQuality.Bounded, notes,
                    projected.FavorableRandomness);
                return new(candidate, side, minimum, maximum,
                    board.HasValue ? CandidateEvaluationQuality.BoardAwareEstimate : CandidateEvaluationQuality.DirectEstimate,
                    prepared, projected.After, projected with { MaximumPoints = null, MinimumPoints = null, Approximation = projectionEstimate },
                    projectionEstimate, board.Contributions, notes,
                    simulation.Missing.Concat(projected.Missing).Distinct().ToArray());
            }
        }
        var estimate = _approximate.Estimate(candidate, prepared, side);
        if (estimate is null)
            estimate = new(candidate.Kind == CardKind.Unit ? candidate.Power : 0,
                candidate.Kind == CardKind.Unit ? candidate.Power : 0, PointEstimateQuality.BaselineOnly,
                ["* Known body/no immediate contribution only. Unresolved ability value is NOT zero and no upper bound is established."]);
        else if (estimate.Quality == PointEstimateQuality.BaselineOnly)
            estimate = estimate with { Notes = estimate.Notes.Append(
                "* Body-only subtotal: unresolved ability value is NOT zero and no upper bound is established.").ToArray() };

        var quality = board.HasValue ? CandidateEvaluationQuality.BoardAwareEstimate : CandidateEvaluationQuality.DirectEstimate;
        return new(candidate, side, estimate.Minimum, estimate.Maximum, quality, prepared, null,
            simulation with { Approximation = estimate }, estimate, board.Contributions,
            simulation.Assumptions.Concat(board.Assumptions).Distinct().ToArray(), simulation.Missing);
    }

    public IReadOnlyList<CandidatePointEvaluation> Evaluate(GamePosition position, IEnumerable<CardDefinition> candidates,
        PlayerSide side, CancellationToken cancellationToken = default) => candidates.DistinctBy(card => card.Id)
        .Select(card => Evaluate(position, card, side, original: true, cancellationToken: cancellationToken)).ToArray();

    public CandidatePointEvaluation Evaluate(GameStateSnapshot snapshot, CardDefinition candidate, PlayerSide side,
        bool? original = true, CancellationToken cancellationToken = default)
    {
        var (position, assumptions) = PositionFromSnapshot(snapshot);
        var result = Evaluate(position, candidate, side, original: original, cancellationToken: cancellationToken);
        return result with { Assumptions = assumptions.Concat(result.Assumptions).Distinct().ToArray() };
    }

    public IReadOnlyList<CandidatePointEvaluation> Evaluate(GameStateSnapshot snapshot, IEnumerable<CardDefinition> candidates,
        PlayerSide side, CancellationToken cancellationToken = default)
    {
        var (position, assumptions) = PositionFromSnapshot(snapshot);
        return candidates.DistinctBy(card => card.Id).Select(card =>
        {
            var result = Evaluate(position, card, side, original: true, cancellationToken: cancellationToken);
            return result with { Assumptions = assumptions.Concat(result.Assumptions).Distinct().ToArray() };
        }).ToArray();
    }

    private GamePosition StageCandidate(GamePosition position, CardDefinition candidate, PlayerSide side,
        string? requestedId, bool? original)
    {
        var hand = position.Zone(side, CardZone.Hand);
        var existing = requestedId is not null ? hand.Cards.FirstOrDefault(card => card.InstanceId == requestedId) :
            hand.Cards.FirstOrDefault(card => card.CardId == candidate.Id);
        if (existing is not null) return position;

        var deck = position.Zone(side, CardZone.Deck);
        var knownDeck = deck.Cards.FirstOrDefault(card => card.CardId == candidate.Id);
        var used = position.Zones.SelectMany(zone => zone.Cards).Select(card => card.InstanceId).ToHashSet(StringComparer.Ordinal);
        var baseId = requestedId ?? $"candidate-{side}-{candidate.Id}";
        var uniqueId = baseId; var suffix = 2;
        while (used.Contains(uniqueId)) uniqueId = baseId + "-" + suffix++;
        var compiled = _book.Rules[candidate.Id];
        var veteran = compiled.Veteran && position.Round is >= 2 and <= 3 ? position.Round.Value - 1 : 0;
        var armor = candidate.PrintedArmor ?? 0;
        var currentPower = compiled.PowerInvariant == "armor" ? armor : candidate.Power + veteran;
        var card = (knownDeck ?? new PositionCard(uniqueId, candidate.Id, currentPower, currentPower,
            armor, compiled.PrintedStatuses, null, null, original)) with
        { InstanceId = uniqueId, Original = knownDeck?.Original ?? original };
        return position with { Zones = position.Zones.Select(zone =>
        {
            if (zone.Side != side) return zone;
            if (zone.Zone == CardZone.Deck && knownDeck is not null)
                return zone with { Cards = zone.Cards.Remove(knownDeck), TotalCount = zone.TotalCount is { } total ? Math.Max(0, total - 1) : null };
            if (zone.Zone == CardZone.Hand)
                return zone with { Cards = zone.Cards.Add(card), Complete = false };
            return zone;
        }).ToImmutableArray() };
    }

    private (GamePosition Position, IReadOnlyList<string> Assumptions) PositionFromSnapshot(GameStateSnapshot snapshot)
    {
        var assumptions = new HashSet<string>();
        var position = CalculationPositionAdapter.FromObserved(snapshot);
        var zones = position.Zones.Select(zone => zone.Zone != CardZone.Board ? zone : zone with
        {
            Cards = zone.Cards.Select(card =>
            {
                if (!_cards.TryGetValue(card.CardId, out var definition)) return card;
                if (card.Power is null || card.BasePower is null || card.Armor is null || card.Statuses is null)
                    assumptions.Add("Unread board stats/statuses use printed defaults; boosts, damage, Armor and Locks can change the result.");
                return card with
                {
                    Power = card.Power ?? definition.Power,
                    BasePower = card.BasePower ?? definition.Power,
                    Armor = card.Armor ?? definition.PrintedArmor ?? 0,
                    Statuses = card.Statuses ?? PlayRules.Compile(definition).PrintedStatuses
                };
            }).ToImmutableArray()
        }).ToImmutableArray();
        string? Leader(PlayerGameState player)
        {
            var value = player.CurrentLeader?.Value ?? player.StartingLeader?.Value;
            return value is null ? null : _cards.Values.FirstOrDefault(card => card.Kind == CardKind.Leader &&
                (card.Id == value || card.Name.Equals(value, StringComparison.OrdinalIgnoreCase)))?.Id;
        }
        var userLeader = Leader(snapshot.User); var opponentLeader = Leader(snapshot.Opponent);
        if (userLeader is null || opponentLeader is null) assumptions.Add("An unread leader passive is not invented by the snapshot evaluator.");
        position = position with
        {
            Zones = zones,
            User = position.User with { CurrentLeaderId = userLeader },
            Opponent = position.Opponent with { CurrentLeaderId = opponentLeader }
        };
        if (position.Zones.Any(zone => zone.Zone == CardZone.Board && !zone.Complete))
            assumptions.Add("Partial board rows make population, category, target and row-capacity results conditional.");
        return (position, assumptions.Order().ToArray());
    }
}
