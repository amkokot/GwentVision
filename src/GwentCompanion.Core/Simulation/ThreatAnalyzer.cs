using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.Core.Simulation;

public sealed record ThreatCandidate(CardDefinition Card, double? ModelShare, string Availability,
    int CopiesRemaining = 1, double? ConditionalHandChance = null);
public sealed record CatchUpThreat(ThreatCandidate Candidate, int Points, bool UsesLeader, int LeaderCharges,
    bool Conditional, IReadOnlyList<string> Line, string Note, CardPointHorizons? Horizons = null);
public sealed record ThreatReport(PlaySearchResult PlayerPlay, int? GapAfterPlay, IReadOnlyList<CatchUpThreat> Replies,
    LeaderSearchResult? Leader, IReadOnlyList<string> Unresolved, IReadOnlyList<string> Assumptions, ThreatSummary? Summary = null,
    CardStatEstimate? PlayerStatistics = null, IReadOnlyList<CardStatEstimate>? ReplyStatistics = null, int? EstimatedGapAfterPlay = null,
    OpponentReachAssessment? Reach = null, CardPointHorizons? PlayerHorizons = null, bool Complete = true)
{
    public int? ReplyGap => GapAfterPlay ?? EstimatedGapAfterPlay;
}
public sealed record ThreatSummary(int? MinimumAheadProvisions, int? MaximumCardSwing, int EvaluatedCards, int CandidateCards, int EstimatedCards = 0);
public sealed record ThreatProgress(int Completed, int Total, ThreatReport Report);
public sealed record ThreatPosition(GamePosition Position, string HoverInstanceId, IReadOnlyList<string> Assumptions,
    ThreatStatContext? Statistics = null, ReachHorizonContext? UserHorizon = null, ReachHorizonContext? OpponentHorizon = null);

/// <summary>Constructs an explicitly conditional position without changing observed evidence or the inferred starting deck.</summary>
public static class ThreatPositionBuilder
{
    public static ThreatPosition Build(GamePosition observed, IEnumerable<CardDefinition> catalog, CardDefinition hover,
        DeckDefinition? ownReference, IEnumerable<DeckCard> opponentHypothesis, IEnumerable<ZoneHypothesis> inventory,
        IEnumerable<ObservedCard>? ownPlayed = null, IEnumerable<ObservedCard>? opponentPlayed = null)
    {
        var definitions = catalog.DistinctBy(card => card.Id).ToDictionary(card => card.Id);
        var opponentList = opponentHypothesis.ToArray();
        var assumptions = new HashSet<string>();
        var zones = observed.Zones.ToDictionary(zone => (zone.Side, zone.Zone, zone.Row));
        PositionCard Base(CardDefinition card, string id, bool? original = null) =>
            new(id, card.Id, card.Power, card.Power, card.PrintedArmor ?? 0, PlayRules.Compile(card).PrintedStatuses, null, null, original);
        foreach (var zone in observed.Zones)
        {
            zones[(zone.Side, zone.Zone, zone.Row)] = zone with { Cards = zone.Cards.Select(card =>
            {
                if (!definitions.TryGetValue(card.CardId, out var definition)) return card;
                if (card.Power is null || card.Armor is null || card.Statuses is null)
                    assumptions.Add("Unread board stats/statuses use printed values; boosts, damage and status changes may alter results.");
                return card with { Power = card.Power ?? definition.Power, BasePower = card.BasePower ?? definition.Power,
                    Armor = card.Armor ?? definition.PrintedArmor ?? 0, Statuses = card.Statuses ?? PlayRules.Compile(definition).PrintedStatuses };
            }).ToImmutableArray() };
        }
        foreach (var entry in inventory.Where(entry => entry.Copies > 0 && entry.Zone is CardZone.Deck or CardZone.Graveyard or CardZone.Banished))
        {
            if (!definitions.TryGetValue(entry.CardId, out var definition)) continue;
            var key = (entry.Side, entry.Zone, (BoardRow?)null); var zone = zones[key];
            var already = zone.Cards.Count(card => card.CardId == entry.CardId);
            for (var copy = already; copy < entry.Copies; copy++)
                zone = zone with { Cards = zone.Cards.Add(Base(definition, $"inventory-{entry.Side}-{entry.Zone}-{entry.CardId}-{copy}")) };
            zones[key] = zone with { Complete = false };
            assumptions.Add(entry.Reviewed ? "Reviewed zone identities are partial inventories." : "Graveyard membership inferred from play/rounds; unseen returns and banishes remain possible.");
        }
        // Reserve the hovered copy before allocating the reference remainder. Never count it both in hand and deck.
        var handKey = (PlayerSide.User, CardZone.Hand, (BoardRow?)null);
        var hand = zones[handKey]; var existing = hand.Cards.FirstOrDefault(card => card.CardId == hover.Id);
        var hoverCard = existing ?? Base(hover, "hover-player", ownReference?.CountOf(hover.Id) > 0 ? true : null);
        if (existing is null) zones[handKey] = hand with { Cards = hand.Cards.Add(hoverCard), Complete = false };
        foreach (var side in Enum.GetValues<PlayerSide>())
        {
            var listed = side == PlayerSide.User ? ownReference?.Cards ?? [] : opponentList;
            var seen = (side == PlayerSide.User ? ownPlayed : opponentPlayed)?.ToDictionary(item => item.Card.Id, item => item.ObservedCopies) ?? [];
            var deckKey = (side, CardZone.Deck, (BoardRow?)null); var deck = zones[deckKey];
            foreach (var group in listed.GroupBy(item => item.Card.Id))
            {
                var card = group.First().Card; var count = group.Sum(item => item.Count);
                var located = zones.Values.Where(zone => zone.Side == side).SelectMany(zone => zone.Cards).Count(item => item.CardId == card.Id);
                var alreadyPlayed = seen.GetValueOrDefault(card.Id);
                // Lost observed copies remain unavailable, not quietly returned to the hypothetical draw pile.
                var onBoardOrOut = zones.Values.Where(zone => zone.Side == side && zone.Zone is CardZone.Board or CardZone.Graveyard or CardZone.Banished)
                    .SelectMany(zone => zone.Cards).Count(item => item.CardId == card.Id);
                var unavailable = located + Math.Max(0, alreadyPlayed - onBoardOrOut);
                for (var copy = unavailable; copy < count; copy++)
                    deck = deck with { Cards = deck.Cards.Add(Base(card, $"hypothesis-{side}-{card.Id}-{copy}", true)) };
            }
            zones[deckKey] = deck with { Complete = false };
            if (listed.Any()) assumptions.Add(side + ": remaining reference/hypothesis cards assumed in draw pile; some may actually be in hand.");
        }
        var own = observed.User; var opponent = observed.Opponent;
        var cardValues = (observed.CardValues ?? []).Where(value => !value.Kind.StartsWith("starting-", StringComparison.Ordinal)).ToList();
        void StartingValues(PlayerSide side, IEnumerable<DeckCard> cards)
        {
            var list = cards.ToArray();
            foreach (var category in new[] { "Tactic", "Nature" })
            {
                var count = list.Where(item => item.Card.HasCategory(category)).Sum(item => item.Count);
                cardValues.Add(new(side, "starting-deck", "starting-" + category.ToLowerInvariant() + "-count", count, count));
            }
            foreach (var item in list)
                cardValues.Add(new(side, item.Card.Id, "starting-copy-count", item.Count, item.Count));
        }
        if (ownReference is { CardCount: >= 25 })
        {
            own = own with { StartingDeckIds = ownReference.Cards.Select(item => item.Card.Id).ToImmutableHashSet(),
                Devotion = ownReference.Cards.All(item => item.Card.Faction != "Neutral") };
            StartingValues(PlayerSide.User, ownReference.Cards);
            assumptions.Add("Starting-deck exclusions and Devotion use the selected player reference; generated cards do not change these constraints.");
        }
        if (opponentList.Sum(item => item.Count) >= 25)
        {
            opponent = opponent with { StartingDeckIds = opponentList.Select(item => item.Card.Id).ToImmutableHashSet(),
                Devotion = opponentList.All(item => item.Card.Faction != "Neutral") };
            StartingValues(PlayerSide.Opponent, opponentList);
            assumptions.Add("Opponent starting-deck conditions use the current 25-card hypothesis and remain conditional on that hypothesis.");
        }
        return new(observed with { User = own, Opponent = opponent, Zones = observed.Zones.Select(zone => zones[(zone.Side, zone.Zone, zone.Row)]).ToImmutableArray(),
                CardValues = cardValues.ToImmutableArray() },
            hoverCard.InstanceId, assumptions.Order().ToArray());
    }
}

public sealed class ThreatAnalyzer(IEnumerable<CardDefinition> catalog, Func<CreatePointProfiles>? profiles = null,
    ReachValidationProfile? validation = null)
{
    /// <summary>The smallest deck-supported swing that reaches the current gap.</summary>
    public static CatchUpThreat? CompactLikelyAnswer(ThreatReport report)
    {
        if (report.Replies.Count == 0) return null;
        var maximumShare = report.Replies.Max(reply => reply.Candidate.ModelShare ?? 0);
        var floor = Math.Max(.05, maximumShare * .2);
        var plausible = report.Replies.Where(reply => reply.Candidate.ModelShare is null || reply.Candidate.ModelShare >= floor).ToArray();
        if (plausible.Length == 0) plausible = report.Replies.ToArray();
        return plausible.Where(reply => report.ReplyGap is null || reply.Points >= report.ReplyGap)
            .OrderBy(reply => reply.Points).ThenBy(reply => reply.Candidate.Card.Provision)
            .ThenByDescending(reply => reply.Candidate.ModelShare ?? 0).FirstOrDefault();
    }
    /// <summary>Keep the cheapest way ahead (otherwise cheapest tie), then the strongest deck-supported alternatives.</summary>
    public static IReadOnlyList<CatchUpThreat> LikelyOptions(ThreatReport report, int limit = 3)
    {
        if (limit < 1 || report.Replies.Count == 0) return [];
        var cheapest = report.Replies.Where(reply => reply.Points > report.ReplyGap).OrderBy(reply => reply.Candidate.Card.Provision).FirstOrDefault()
            ?? report.Replies.OrderBy(reply => reply.Candidate.Card.Provision).First();
        var likely = report.Replies.OrderByDescending(reply => reply.Candidate.ModelShare ?? 0).ThenBy(reply => reply.Candidate.Card.Provision).First();
        var biggest = report.Replies.OrderByDescending(reply => reply.Points).ThenByDescending(reply => reply.Candidate.ModelShare ?? 0).First();
        return new[] { cheapest, likely, biggest }.Distinct().Concat(report.Replies.Where(reply => reply != cheapest && reply != likely && reply != biggest)
            .OrderByDescending(reply => reply.Points > report.GapAfterPlay)
            .ThenByDescending(reply => reply.Candidate.ModelShare ?? 0).ThenBy(reply => reply.Candidate.Card.Provision))
            .Take(limit).OrderBy(reply => reply.Candidate.Card.Provision).ToArray();
    }
    private readonly CardDefinition[] _catalog = catalog.DistinctBy(card => card.Id).ToArray();
    private readonly PlayRuleBook _book = new(catalog);
    private readonly ApproximatePointModel _approximate = new(catalog);
    private readonly BoardInteractionPointModel _interactions = new(catalog);
    private readonly CandidatePointEvaluator _candidatePoints = new(catalog, cacheCapacity: 96);
    private readonly ReachCache<LeaderSearchResult> _leaders = new(48);
    private readonly ReachCache<CardStatEstimate?> _statistics = new(96);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<CandidatePointEvaluation, ReachCache<CardPointHorizons?>> _horizonCache = new();
    public long CandidateCacheHits => _candidatePoints.CacheHits;
    public int CachedCandidates => _candidatePoints.CachedEvaluations;
    private CardPointHorizonEvaluator _horizons => _candidatePoints.Horizons;
    // An interrupted cold profile build must be retryable, not poison all future Create evaluations.
    private readonly Lazy<CreatePointProfiles>? _profiles = profiles is null ? null : new(profiles, LazyThreadSafetyMode.PublicationOnly);
    private readonly ReachValidationProfile? _validation = validation;
    private readonly Dictionary<string, ThreatReport> _cache = [];
    private readonly Queue<string> _cacheOrder = new();
    private readonly object _cacheLock = new();
    /// <summary>Keep cheap reality checks and tall punish even when they fall outside the highest-share candidates.</summary>
    public IReadOnlyList<ThreatCandidate> LiveCandidates(IEnumerable<ThreatCandidate> candidates)
    {
        var offered = candidates.DistinctBy(item => item.Card.Id).OrderByDescending(item => item.ModelShare ?? 0)
            .ThenBy(item => item.Card.Id, StringComparer.Ordinal).ToArray();
        if (offered.Length <= 30) return offered;
        var cheap = offered.OrderBy(item => item.Card.Provision).ThenByDescending(item => item.ModelShare ?? 0).Take(6);
        var punish = offered.Where(item => _book.Rules.GetValueOrDefault(item.Card.Id)?.Effect is
            PlayEffect.Destroy or PlayEffect.Banish or PlayEffect.Reset or PlayEffect.RowReset).Take(6);
        return cheap.Concat(punish).Concat(offered).DistinctBy(item => item.Card.Id).Take(30).ToArray();
    }
    public static int? UserLead(GameStateSnapshot state)
    {
        var debt = GwentRules.ScoreGap(state, PlayerSide.User);
        return debt.Known ? -debt.Value : null;
    }
    public ThreatReport Analyze(ThreatPosition input, int? observedUserLead, IEnumerable<ThreatCandidate> candidates,
        CancellationToken cancellationToken = default, Action<ThreatProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GwentCompanion.Core.Vision.ReachWorkScope.Checkpoint();
        var candidateArray = candidates.Select(candidate => CurrentRound(candidate, input.Position.Round))
            .DistinctBy(candidate => candidate.Card.Id).ToArray();
        var key = RequestKey(input, observedUserLead, candidateArray);
        lock (_cacheLock) if (_cache.TryGetValue(key, out var cached)) return cached;
        var report = Calculate(input, observedUserLead, candidateArray, cancellationToken, progress);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_cacheLock)
        {
            if (!_cache.ContainsKey(key)) { _cache[key] = report; _cacheOrder.Enqueue(key); }
            while (_cache.Count > 16) _cache.Remove(_cacheOrder.Dequeue());
        }
        return report;
    }
    public static string RequestKey(ThreatPosition input, int? lead, IEnumerable<ThreatCandidate> candidates) =>
        ReachCacheKey.For(input.Position, input.HoverInstanceId, lead, candidates.ToArray(), input.Assumptions,
            input.Statistics, input.UserHorizon?.CacheKey, input.OpponentHorizon?.CacheKey);

    private CardPointHorizons? Horizons(CandidatePointEvaluation value, ReachHorizonContext? context,
        ApproximatePointEstimate? estimate = null)
    {
        GwentCompanion.Core.Vision.ReachWorkScope.Checkpoint();
        var cache = _horizonCache.GetValue(value, _ => new(8));
        var key = System.Text.Json.JsonSerializer.Serialize(new { Context = context?.CacheKey, Estimate = estimate });
        if (cache.TryGet(key, out var result)) return result;
        result = _horizons.Evaluate(value, context, estimate);
        GwentCompanion.Core.Vision.ReachWorkScope.Checkpoint();
        return cache.Add(key, result);
    }

    private LeaderSearchResult Leader(TacticalPlayEngine engine, GamePosition position, bool maximum, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        GwentCompanion.Core.Vision.ReachWorkScope.Checkpoint();
        var key = ReachCacheKey.For(position, maximum);
        if (_leaders.TryGet(key, out var value)) return value;
        value = maximum ? engine.MaximumLeader(position, PlayerSide.Opponent, cancellationToken: cancellation) :
            engine.LeaderContribution(position, PlayerSide.Opponent, cancellationToken: cancellation);
        cancellation.ThrowIfCancellationRequested();
        return _leaders.Add(key, value);
    }
    private ThreatCandidate CurrentRound(ThreatCandidate candidate, int? round)
    {
        var startingId = EvolvingCardCatalog.StartingId(candidate.Card.Id);
        var family = EvolvingCardCatalog.Families.FirstOrDefault(item => item.StartingId == startingId);
        if (family is null || round is not (>= 1 and <= 3)) return candidate;
        var id = round switch { 1 => family.StartingId, 2 => family.SecondId, _ => family.FinalId };
        var form = _catalog.FirstOrDefault(card => card.Id == id);
        return form is null || form.Id == candidate.Card.Id ? candidate : candidate with
        {
            Card = form,
            Availability = candidate.Availability + $"; round {round} evolving form ({form.Name}) assumed"
        };
    }
    private ThreatReport Calculate(ThreatPosition input, int? observedUserLead, IEnumerable<ThreatCandidate> candidates,
        CancellationToken cancellationToken, Action<ThreatProgress>? progress)
    {
        var engine = new TacticalPlayEngine(_book);
        var hoverId = input.Position.Zones.SelectMany(zone => zone.Cards).FirstOrDefault(card => card.InstanceId == input.HoverInstanceId)?.CardId;
        var hover = _catalog.FirstOrDefault(card => card.Id == hoverId);
        var ownValue = hover is null ? null : _candidatePoints.Evaluate(input.Position, hover, PlayerSide.User, input.HoverInstanceId,
            input.Position.Zone(PlayerSide.User, CardZone.Hand).Cards.FirstOrDefault(card => card.InstanceId == input.HoverInstanceId)?.Original,
            cancellationToken);
        var own = ownValue?.Simulation ?? engine.ImmediateMaximum(input.Position, input.HoverInstanceId, PlayerSide.User, cancellationToken: cancellationToken);
        CardStatEstimate? Statistics(CardDefinition card, GamePosition p, PlayerSide side)
        {
            GwentCompanion.Core.Vision.ReachWorkScope.Checkpoint();
            var key = ReachCacheKey.For(p, card.Id, side, input.Statistics);
            if (_statistics.TryGet(key, out var value)) return value;
            value = RandomDamageStatistics.For(card, p, side, _book) is { } random ? new(card, random.Distribution, random.Label, false) :
                card.AbilityText?.Contains("Create", StringComparison.Ordinal) == true ? _profiles?.Value.Estimate(card, p, side, _catalog, input.Statistics) : null;
            cancellationToken.ThrowIfCancellationRequested();
            return _statistics.Add(key, value);
        }
        ApproximatePointEstimate? Approximation(CardDefinition card, GamePosition p, PlayerSide side, CardStatEstimate? statistics = null)
        {
            var estimate = _approximate.Estimate(card, p, side);
            if (statistics?.Distribution is not { } distribution || distribution.Quantile(0) is not { } low || distribution.Quantile(1) is not { } high)
                return estimate;
            var interactions = _interactions.Estimate(card, p, side);
            var notes = new List<string> { statistics.Label + ". " + distribution.Basis };
            notes.AddRange(interactions.Contributions.Select(value => $"{value.Source} {value.RangeText}: {value.Reason}"));
            notes.AddRange(interactions.Assumptions);
            return new(low + interactions.Minimum, high + interactions.Maximum, PointEstimateQuality.Bounded,
                notes.Distinct().ToArray(), distribution.Approximate);
        }
        var playerStats = hover is null ? null : Statistics(hover, input.Position, PlayerSide.User);
        if (own.MaximumPoints is null && hover is not null)
        {
            var approximation = ownValue?.Simulation.UsedProjection == true ? ownValue.Estimate :
                Approximation(hover, input.Position, PlayerSide.User, playerStats) ?? ownValue?.Estimate;
            own = own with { Approximation = approximation };
        }
        var playerHorizons = ownValue is null ? null : Horizons(ownValue, input.UserHorizon, own.Approximation);
        var unresolved = new List<string>();
        var assumptions = input.Assumptions.Concat(own.Assumptions).Distinct().ToList();
        var scheduled = LiveCandidates(candidates);
        void Publish(int completed, ThreatReport partial)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(new(completed, scheduled.Count + 1, partial with { Complete = false, Reach = null }));
            GwentCompanion.Core.Vision.ReachWorkScope.Checkpoint();
        }
        Publish(1, new(own, own.MaximumPoints is { } ownTotal && observedUserLead is { } score ? score + ownTotal : null,
            [], null, [], assumptions.ToArray(), PlayerStatistics: playerStats, PlayerHorizons: playerHorizons));
        // A partial modeled subtotal is never substituted for a supported score swing.
        if (own.MaximumPoints is not { } ownPoints || own.After is null)
        {
            int? estimatedGap = observedUserLead is { } currentLead && own.Approximation is { } approximate ? currentLead + approximate.Maximum : null;
            var fallbackPosition = (own.UsedProjection ? own.After ?? input.Position : input.Position) with { ActivePlayer = PlayerSide.Opponent };
            var fallbackLeader = Leader(engine, fallbackPosition, false, cancellationToken);
            var fallbackOffered = candidates.DistinctBy(item => item.Card.Id).OrderByDescending(item => item.ModelShare ?? 0).ToArray();
            var fallbackReplies = new List<CatchUpThreat>(); var replyStats = new List<CardStatEstimate>();
            int? fallbackCheapestAhead = null, fallbackMaximumSwing = null; var fallbackEvaluated = 0;
            var completed = 1;
            foreach (var candidate in scheduled)
            {
                Publish(completed++, new(own, null, fallbackReplies.ToArray(), fallbackLeader, own.Missing, assumptions.ToArray(),
                    new(fallbackCheapestAhead, fallbackMaximumSwing, fallbackEvaluated, fallbackOffered.Length, fallbackEvaluated),
                    playerStats, replyStats.ToArray(), estimatedGap, PlayerHorizons: playerHorizons));
                cancellationToken.ThrowIfCancellationRequested();
                var p = PrepareReply(fallbackPosition, candidate.Card);
                var stats = Statistics(candidate.Card, p, PlayerSide.Opponent);
                if (stats is not null) replyStats.Add(stats);
                var candidateValue = _candidatePoints.Evaluate(p, candidate.Card, PlayerSide.Opponent, "reply-card", true, cancellationToken);
                var estimate = candidateValue.Simulation.UsedProjection ? candidateValue.Estimate :
                    candidateValue.Simulation.MaximumPoints is { } resolved ?
                        new ApproximatePointEstimate(candidateValue.MinimumPoints ?? resolved, resolved, PointEstimateQuality.StateAware,
                            candidateValue.Assumptions, candidateValue.Simulation.FavorableRandomness) :
                    Approximation(candidate.Card, p, PlayerSide.Opponent, stats) ?? candidateValue.Estimate;
                if (estimate is null) continue;
                var horizons = Horizons(candidateValue, input.OpponentHorizon, estimate);
                fallbackEvaluated++; var horizonPoints = horizons?.RelevantMaximum ?? estimate.Maximum;
                fallbackMaximumSwing = Math.Max(fallbackMaximumSwing ?? int.MinValue, horizonPoints);
                if (estimatedGap is >= 0)
                {
                    var points = horizonPoints; var usesLeader = false; var charges = 0;
                    var note = $"Estimated board-aware range {estimate.RangeText}; unresolved card text and hidden interactions remain excluded.";
                    if (points <= estimatedGap && fallbackLeader.Estimate.MaximumPoints is { } leaderPoints && leaderPoints > 0 && points + leaderPoints > estimatedGap)
                    {
                        points += leaderPoints; usesLeader = true; charges = fallbackLeader.ChargesUsed;
                        horizons = horizons?.AddFromOneTurn(leaderPoints, "Leader contribution persists in Y and Z.");
                        note += $" Includes an estimated {charges}-charge leader contribution.";
                    }
                    if (points > estimatedGap) fallbackCheapestAhead = Math.Min(fallbackCheapestAhead ?? int.MaxValue, candidate.Card.Provision);
                    if (points >= estimatedGap) fallbackReplies.Add(new(candidate, points, usesLeader, charges, true, [], note, horizons));
                }
            }
            assumptions.Add("Fresh exact board reconstruction was unavailable; the hovered play and opponent replies use bounded one-card estimates with readable board reactions.");
            assumptions.Add("Opponent candidates are possible hand cards, not known hand contents.");
            var fallbackUnresolved = own.Missing.Concat(estimatedGap is null ? ["Hovered-card range or current score gap unavailable; catch-up threshold cannot be computed."] : [])
                .Distinct().ToArray();
            var orderedFallback = fallbackReplies.OrderBy(reply => reply.Candidate.Card.Provision).ThenByDescending(reply => reply.Candidate.ModelShare).ToArray();
            var fallbackSummary = new ThreatSummary(fallbackCheapestAhead, fallbackMaximumSwing, fallbackEvaluated, fallbackOffered.Length, fallbackEvaluated);
            var fallbackReach = OpponentReachModel.Assess(estimatedGap, orderedFallback, fallbackOffered, fallbackSummary, assumptions, _validation);
            return new(own, null, orderedFallback, fallbackLeader, fallbackUnresolved, assumptions, fallbackSummary,
                playerStats, replyStats, estimatedGap, fallbackReach, playerHorizons);
        }
        var gap = observedUserLead is { } lead ? lead + ownPoints : (int?)null;
        if (gap is null) unresolved.Add("Score gap unavailable/stale; replies cannot be classified as catch-up plays.");
        var replyPosition = own.After with { ActivePlayer = PlayerSide.Opponent };
        var leader = Leader(engine, replyPosition, false, cancellationToken);
        var replies = new List<CatchUpThreat>();
        var statistics = new List<CardStatEstimate>();
        var leaderCard = _catalog.FirstOrDefault(card => card.Id == replyPosition.Opponent.CurrentLeaderId);
        if (replyPosition.Opponent.LeaderCharges != 0 && leaderCard is not null && Statistics(leaderCard, replyPosition, PlayerSide.Opponent) is { } leaderStats)
            statistics.Add(leaderStats);
        int? cheapestAhead = null, maximumSwing = null; var evaluated = 0; var estimated = 0;
        var offered = candidates.DistinctBy(item => item.Card.Id).OrderByDescending(item => item.ModelShare ?? 0).ToArray();
        if (offered.Length > 30) unresolved.Add("Evaluated 30 candidate identities, retaining cheap replies and tall punish alongside likely cards.");
        var completedReplies = 1;
        foreach (var candidate in scheduled)
        {
            Publish(completedReplies++, new(own, gap, replies.ToArray(), leader, unresolved.ToArray(), assumptions.ToArray(),
                new(cheapestAhead, maximumSwing, evaluated, offered.Length, estimated), playerStats, statistics.ToArray(),
                PlayerHorizons: playerHorizons));
            cancellationToken.ThrowIfCancellationRequested();
            var p = PrepareReply(replyPosition, candidate.Card);
            if (Statistics(candidate.Card, p, PlayerSide.Opponent) is { } stats) statistics.Add(stats);
            var candidateValue = _candidatePoints.Evaluate(p, candidate.Card, PlayerSide.Opponent, "reply-card", true, cancellationToken);
            var play = candidateValue.Simulation;
            if (play.MaximumPoints is null)
            {
                var statisticalEstimate = Statistics(candidate.Card, p, PlayerSide.Opponent);
                var approximation = candidateValue.Simulation.UsedProjection ? candidateValue.Estimate :
                    Approximation(candidate.Card, p, PlayerSide.Opponent, statisticalEstimate) ?? candidateValue.Estimate;
                if (approximation is null)
                { unresolved.Add(candidate.Card.Name + ": " + string.Join("; ", play.Missing.Take(2))); continue; }
                var horizons = Horizons(candidateValue, input.OpponentHorizon, approximation);
                evaluated++; estimated++; var horizonPoints = horizons?.RelevantMaximum ?? approximation.Maximum;
                maximumSwing = Math.Max(maximumSwing ?? int.MinValue, horizonPoints);
                if (gap is >= 0)
                {
                    var points = horizonPoints; var usesLeader = false; var charges = 0;
                    var note = $"Estimated board-aware range {approximation.RangeText}; unresolved card text and hidden interactions remain excluded.";
                    if (points <= gap && leader.Estimate.MaximumPoints is { } leaderPoints && leaderPoints > 0 && points + leaderPoints > gap)
                    {
                        points += leaderPoints; usesLeader = true; charges = leader.ChargesUsed;
                        horizons = horizons?.AddFromOneTurn(leaderPoints, "Leader contribution persists in Y and Z.");
                        note += $" Includes an estimated {charges}-charge leader contribution.";
                    }
                    if (points > gap) cheapestAhead = Math.Min(cheapestAhead ?? int.MaxValue, candidate.Card.Provision);
                    if (points >= gap) replies.Add(new(candidate, points, usesLeader, charges, true, [], note, horizons));
                }
                continue;
            }
            evaluated++;
            var candidateHorizons = Horizons(candidateValue, input.OpponentHorizon);
            maximumSwing = Math.Max(maximumSwing ?? int.MinValue, candidateHorizons?.RelevantMaximum ?? play.MaximumPoints.Value);
            if (gap is >= 0)
            {
                var points = candidateHorizons?.RelevantMaximum ?? play.MaximumPoints.Value;
                var usesLeader = false; var charges = 0; var line = play.Line; var note = play.FavorableRandomness ? "Favorable RNG; not guaranteed." : candidate.Availability;
                if (points <= gap && play.After is not null)
                {
                    var assisted = Leader(engine, play.After, true, cancellationToken);
                    if (assisted.Estimate.MaximumPoints is { } leaderPoints && leaderPoints > 0 && points + leaderPoints > gap)
                    {
                        points += leaderPoints; usesLeader = true; charges = assisted.ChargesUsed;
                        candidateHorizons = candidateHorizons?.AddFromOneTurn(leaderPoints, "Leader contribution persists in Y and Z.");
                        line = line.Concat(assisted.Estimate.Line).ToArray();
                        note += $" Includes {charges} leader charge{(charges == 1 ? "" : "s")}.";
                    }
                }
                if (points > gap) cheapestAhead = Math.Min(cheapestAhead ?? int.MaxValue, candidate.Card.Provision);
                if (points >= gap)
                    replies.Add(new(candidate, points, usesLeader, charges,
                        assumptions.Count > 0 || play.Assumptions.Count > 0 || candidate.Availability != "Observed hand",
                        line, note, candidateHorizons));
            }
        }
        assumptions.Add("Opponent candidates are possible hand cards, not known hand contents. Provision order is a resource-cost proxy, not an optimal-play ranking.");
        assumptions.Add("Replies use the displayed best player line; a different target/placement or RNG outcome can change the threats.");
        var orderedReplies = replies.OrderBy(reply => reply.Candidate.Card.Provision).ThenBy(reply => reply.UsesLeader)
            .ThenByDescending(reply => reply.Candidate.ModelShare).ToArray();
        var summary = new ThreatSummary(cheapestAhead, maximumSwing, evaluated, offered.Length, estimated);
        var reach = OpponentReachModel.Assess(gap, orderedReplies, offered, summary, assumptions, _validation);
        return new(own, gap, orderedReplies, leader, unresolved.Distinct().ToArray(), assumptions,
            summary, playerStats, statistics, Reach: reach, PlayerHorizons: playerHorizons);
    }
    private static GamePosition PrepareReply(GamePosition p, CardDefinition definition)
    {
        var deck = p.Zone(PlayerSide.Opponent, CardZone.Deck);
        var startingId = EvolvingCardCatalog.StartingId(definition.Id);
        bool SameFamily(PositionCard card) => EvolvingCardCatalog.StartingId(card.CardId) == startingId;
        var knownHand = p.Zone(PlayerSide.Opponent, CardZone.Hand).Cards.FirstOrDefault(SameFamily);
        var knownDeck = deck.Cards.FirstOrDefault(SameFamily);
        var source = knownHand ?? knownDeck;
        var card = new PositionCard("reply-card", definition.Id, definition.Power, definition.Power,
            definition.PrintedArmor ?? 0, PlayRules.Compile(definition).PrintedStatuses,
            source?.Charges, source?.Cooldown, source?.Original ?? true);
        var devotion = EvolvingCardCatalog.IsFinal(definition.Id) ? true : p.Opponent.Devotion;
        return p with { Opponent = p.Opponent with { Devotion = devotion }, Zones = p.Zones.Select(zone =>
        {
            if (zone.Side != PlayerSide.Opponent) return zone;
            if (zone.Zone == CardZone.Deck && knownHand is null && knownDeck is not null) return zone with { Cards = zone.Cards.Remove(knownDeck), TotalCount = zone.TotalCount - 1 };
            if (zone.Zone == CardZone.Hand) return zone with { Cards = zone.Cards.Where(item => item.InstanceId != knownHand?.InstanceId).Append(card).ToImmutableArray(), Complete = false };
            return zone;
        }).ToImmutableArray() };
    }
}
