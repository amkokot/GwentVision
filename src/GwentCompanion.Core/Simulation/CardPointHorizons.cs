using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed record PointRange(int Minimum, int Maximum, double? Mean = null, double? Sigma = null,
    bool Approximate = false)
{
    public bool Exact => Minimum == Maximum;
    public double Midpoint => Mean ?? (Minimum + Maximum) / 2d;
    public double Radius => Sigma ?? (Maximum - Minimum) / 2d;
    public string Compact => (Exact && Mean is null ? Maximum.ToString() : $"{Midpoint:0.#}±{Radius:0.#}") + (Approximate ? "*" : "");
    public string SignedCompact => (Exact && Mean is null ? $"{Maximum:+0;-0;0}" : $"{Midpoint:+0.#;-0.#;0}±{Radius:0.#}") + (Approximate ? "*" : "");
    public PointRange Add(PointRange other)
    {
        var probabilistic = Mean is not null || other.Mean is not null;
        double? mean = probabilistic ? Midpoint + other.Midpoint : null;
        double? sigma = probabilistic ? Math.Sqrt(Radius * Radius + other.Radius * other.Radius) : null;
        return new(Minimum + other.Minimum, Maximum + other.Maximum, mean, sigma, Approximate || other.Approximate);
    }
    public PointRange Add(int value) => new(Minimum + value, Maximum + value, Mean is null ? null : Mean + value, Sigma, Approximate);
    public PointRange MarkApproximate() => this with { Approximate = true };
}

public sealed record ReachWeatherEffect(PlayerSide AffectedSide, BoardRow Row, string Name, int? RemainingTurns);

public sealed record ReachHorizonContext(int? CardsInHandBeforePlay, IReadOnlyList<ReachWeatherEffect> Weather,
    bool WeatherKnown = true, int? OpposingCardsInHand = null, bool? OpposingPassed = null)
{
    public string CacheKey => $"hand={CardsInHandBeforePlay?.ToString() ?? "?"};other-hand={OpposingCardsInHand?.ToString() ?? "?"};other-passed={OpposingPassed?.ToString() ?? "?"};weather-known={WeatherKnown};" +
        string.Join(',', Weather.OrderBy(effect => effect.AffectedSide).ThenBy(effect => effect.Row)
            .ThenBy(effect => effect.Name, StringComparer.OrdinalIgnoreCase)
            .Select(effect => $"{effect.AffectedSide}:{effect.Row}:{effect.Name}:{effect.RemainingTurns?.ToString() ?? "?"}"));

    public static ReachHorizonContext FromSnapshot(GameStateSnapshot? state, PlayerSide side)
    {
        if (state?.At is not { } at) return new(null, [], false, null, null);
        static bool Fresh<T>(StateFact<T>? fact, DateTimeOffset now) => fact is not null &&
            fact.Kind is EvidenceKind.Visual or EvidenceKind.Reviewed && fact.Confidence >= .8 &&
            fact.IsFresh(now, GwentRules.DynamicFactLifetime);
        var handFact = state.Player(side).HandCount;
        var hand = Fresh(handFact, at) ? handFact!.Value : (int?)null;
        var other = side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User;
        var otherHandFact = state.Player(other).HandCount;
        var otherHand = Fresh(otherHandFact, at) ? otherHandFact!.Value : (int?)null;
        var otherPassedFact = state.Player(other).Passed;
        var otherPassed = Fresh(otherPassedFact, at) ? otherPassedFact!.Value : (bool?)null;
        var currentRows = state.Rows.Where(row => !state.BoardObscured && state.Phase == GamePhase.Playing &&
            row.ScannedAt is { } scanned && scanned <= at && at - scanned <= GwentRules.DynamicFactLifetime).ToArray();
        var weatherKnown = currentRows.Length == 4 && currentRows.All(row => row.EffectsKnown);
        var weather = currentRows.Where(row => row.EffectsKnown).SelectMany(row => row.Effects.Select(effect =>
            new ReachWeatherEffect(row.Side, row.Row, effect.Name,
                Fresh(effect.RemainingTurns, at) ? effect.RemainingTurns!.Value : null))).ToArray();
        return new(hand, weather, weatherKnown, otherHand, otherPassed);
    }
}

public sealed record CardPointHorizons(PointRange CardOnly, PointRange OneTurn, PointRange TwoTurns,
    bool? TwoTurnRelevant, IReadOnlyList<string> Notes)
{
    public int RelevantMaximum => TwoTurnRelevant == false ? OneTurn.Maximum : Math.Max(OneTurn.Maximum, TwoTurns.Maximum);
    public string Compact => $"{CardOnly.Compact} | {OneTurn.Compact} | {TwoTurns.Compact}";

    public CardPointHorizons AddFromOneTurn(int points, string note) => this with
    {
        OneTurn = OneTurn.Add(points), TwoTurns = TwoTurns.Add(points), Notes = Notes.Append(note).ToArray()
    };
}

/// <summary>
/// Builds X | Y | Z from one candidate search. X reuses the direct-action subtotal (parser only on fallback), Y reuses the one-turn
/// result, and Z advances only automatic effects. Existing Orders, Fees and charge conversion are never clicked in Z.
/// </summary>
public sealed class CardPointHorizonEvaluator
{
    private static readonly Regex SpawnWeather = new(
        @"Spawn ?(?<name>Frost|Fog|Rain|Storm|Cataclysm|Blood Moon) on (?:an enemy|the opposite) row for (?<turns>\d+) turns?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly IReadOnlyDictionary<string, CardDefinition> _cards;
    private readonly ApproximatePointModel _direct;
    private readonly PlayRuleBook _book;

    public CardPointHorizonEvaluator(IEnumerable<CardDefinition> catalog, PlayRuleBook? sharedRules = null)
    {
        var cards = catalog.DistinctBy(card => card.Id).ToArray();
        _cards = cards.ToDictionary(card => card.Id);
        _direct = new(cards); _book = sharedRules ?? new(cards);
    }

    public CardPointHorizons? Evaluate(CandidatePointEvaluation value, ReachHorizonContext? context,
        ApproximatePointEstimate? oneTurnOverride = null)
    {
        if (!value.HasValue && oneTurnOverride is null) return null;
        var notes = new List<string>();
        var direct = value.After is not null && value.Simulation.CardOnlyMaximum is not null ? null :
            _direct.EstimateDirect(value.Card, value.Position, value.Side);
        var x = direct is null
            ? value.Card.Kind == CardKind.Unit ? new PointRange(value.Card.Power, value.Card.Power) : new PointRange(0, 0)
            : new(direct.Minimum, direct.Maximum);
        if (value.After is not null && value.Simulation.CardOnlyMaximum is { } cardOnly)
            x = new(value.Simulation.CardOnlyMinimum ?? cardOnly, cardOnly);
        var y = oneTurnOverride is not null ? new PointRange(oneTurnOverride.Minimum, oneTurnOverride.Maximum) :
            new PointRange(value.MinimumPoints!.Value, value.MaximumPoints!.Value);
        var z = y;
        var greedy = value.Simulation.Line.Any(item => item.StartsWith("[agent]", StringComparison.Ordinal));
        var approximate = greedy || value.Estimate is not null || value.Simulation.Line.Any(item => item.StartsWith("[approx]", StringComparison.Ordinal));
        if (value.Estimate is not null) notes.AddRange(value.Estimate.Notes);
        if (value.Simulation.UsedProjection) notes.Add("Y/Z use the approximate simulated board; omitted clauses are not treated as implemented rules.");
        if (greedy) notes.Add("Greedy internal player simulated legal card-controlled choices; this is a locally optimized line, not a full-turn proof.");
        var fucusyaAssumption = value.Card.Id == "203041" && approximate;
        if (fucusyaAssumption && value.Simulation.CardOnlyMaximum is null) x = y;

        // Each horizon is a pair of alternating player turns. A side with no card still receives its
        // automatic status/engine turn; only an already-passed side is skipped. The second pair exists
        // only while either player has another card action after this candidate.
        var secondPair = context is null || context.CardsInHandBeforePlay is null ||
            context.OpposingPassed != true && context.OpposingCardsInHand is null ||
            context.CardsInHandBeforePlay > 1 || context.OpposingPassed != true && context.OpposingCardsInHand > 0;
        var futureEnemyWindows = context?.OpposingPassed == true ? 0 : secondPair ? 2 : 1;

        // ImmediateMaximum already ended the acting turn. Finish the first opposing automatic turn for Y,
        // then advance the second acting/opposing pair for Z. No Orders or Fees are clicked here.
        if (value.After is not null)
        {
            var horizonBook = value.Simulation.UsedProjection ? _book.ForProjection : _book;
            var engine = new TacticalPlayEngine(horizonBook);
            ResolvedAction Advance(GamePosition p, PlayerSide owner) => value.Simulation.UsedProjection ?
                engine.AutomaticReach(p, owner) : engine.ResolveEndTurn(p, owner);
            static PointRange ActionRange(ResolvedAction action, int points) => new(
                action.FavorableRandomness ? action.MinimumPoints ?? points : points, points,
                Approximate: action.FavorableRandomness);
            var other = value.Side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User;
            var cursor = value.After;
            if (futureEnemyWindows >= 1)
            {
                var opposing = Advance(cursor, other);
                if (opposing.Points is { } points)
                {
                    var range = Negate(ActionRange(opposing, points));
                    y = y.Add(range); z = z.Add(range);
                    cursor = opposing.After ?? cursor;
                    if (points != 0) notes.Add($"First opposing automatic turn {-points:+0;-0;0} from the acting side's perspective.");
                }
                else
                {
                    var bounded = Negate(AutomaticFallback(cursor, other));
                    y = y.Add(bounded); z = z.Add(bounded);
                    notes.Add("First opposing automatic turn is bounded because a status, condition or random tie is unread.");
                }
            }
            if (secondPair)
            {
                var candidateIds = value.Position.Zone(value.Side, CardZone.Hand).Cards
                    .Where(card => card.CardId == value.Card.Id).Select(card => card.InstanceId).ToHashSet();
                var candidateOrder = cursor.Zones.Where(zone => zone.Side == value.Side && zone.Zone == CardZone.Board)
                    .SelectMany(zone => zone.Cards).FirstOrDefault(card => candidateIds.Contains(card.InstanceId) &&
                        card.Charges > 0 && card.Cooldown == 0 && horizonBook.Rules.GetValueOrDefault(card.CardId)?.Order is not null);
                if (candidateOrder is not null)
                {
                    var clicked = engine.MaximumOrder(cursor, candidateOrder.InstanceId, value.Side);
                    if (clicked.Points is { } clickPoints)
                    {
                        var clickRange = ActionRange(clicked, clickPoints);
                        y = y.Add(clickRange); z = z.Add(clickRange); cursor = clicked.After ?? cursor;
                        notes.Add($"Newly-readied {value.Card.Name} Order contributes {clickPoints:+0;-0;0} once; it is not repeated while passing.");
                    }
                    else notes.Add($"{value.Card.Name} Order is ready after one turn, but its target/pool remains bounded or unresolved.");
                }
                var acting = Advance(cursor, value.Side);
                if (acting.Points is { } actingPoints)
                {
                    z = z.Add(ActionRange(acting, actingPoints)); cursor = acting.After ?? cursor;
                    if (actingPoints != 0) notes.Add($"Second acting automatic turn {actingPoints:+0;-0;0}.");
                }
                else
                {
                    z = z.Add(AutomaticFallback(cursor, value.Side));
                    notes.Add("Second acting automatic turn is bounded because a status, condition or random tie is unread.");
                }
                if (futureEnemyWindows >= 2)
                {
                    var opposing = Advance(cursor, other);
                    if (opposing.Points is { } opposingPoints)
                    {
                        z = z.Add(Negate(ActionRange(opposing, opposingPoints)));
                        if (opposingPoints != 0) notes.Add($"Second opposing automatic turn {-opposingPoints:+0;-0;0} from the acting side's perspective.");
                    }
                    else
                    {
                        z = z.Add(Negate(AutomaticFallback(cursor, other)));
                        notes.Add("Second opposing automatic turn is bounded because a status, condition or random tie is unread.");
                    }
                }
            }
        }
        else
        {
            var deferred = value.BoardContributions.Where(item => item.Deferred && !item.RequiresAction)
                .Aggregate(new PointRange(0, 0), (sum, item) => sum.Add(new PointRange(item.Minimum, item.Maximum)));
            if (secondPair) z = z.Add(deferred);
            if (!deferred.Exact || deferred.Maximum != 0) notes.Add("Second automatic engine/Vitality tick extrapolated from the bounded board reaction.");
        }

        if (value.BoardContributions.Any(item => item.RequiresAction))
        {
            notes.Add("Click-dependent Order/Fee/charge value may appear in Y but is not repeated in pass-safe Z.");
            if (value.After is not null)
            {
                var enabled = value.BoardContributions.Where(item => item.RequiresAction)
                    .Aggregate(new PointRange(0, 0, Approximate: true), (sum, item) => sum.Add(new PointRange(item.Minimum, item.Maximum)));
                y = y.Add(enabled); z = z.Add(enabled);
                notes.Add("Newly enabled existing-engine clicks are valued once, separately from the unspent simulated board.");
            }
        }

        var existingManual = ExistingManualReach(value.Position, value.Side);
        if (existingManual.Maximum != 0 || existingManual.Minimum != 0)
        {
            y = y.Add(existingManual); z = z.Add(existingManual);
            notes.Add($"Existing click engines contribute {existingManual.Compact} once in Y; Z retains points already taken but does not click them again while passing.");
        }

        var weatherPosition = value.After ?? value.Position;
        // Turns alternate even when a hand is empty: weather and automatic engines still resolve. The first
        // enemy window therefore exists unless that side has passed. A second requires either side to have a
        // card action left after this candidate, otherwise the terminal empty-hand pair ends the round.
        foreach (var effect in context?.Weather ?? [])
        {
            if (effect.AffectedSide == value.Side)
            {
                notes.Add($"{effect.Name} on the acting side is excluded: its current-turn start tick occurred before this candidate was played.");
                continue;
            }
            var tick = Weather(effect.Name, weatherPosition, effect.AffectedSide, effect.Row, value.Side);
            if (effect.RemainingTurns is null)
            {
                if (futureEnemyWindows >= 1) { y = y.Add(IncludeZero(tick)); z = z.Add(IncludeZero(tick)); }
                if (futureEnemyWindows >= 2) z = z.Add(IncludeZero(tick));
                notes.Add($"{effect.Name} duration unread; Y includes 0-to-one tick and Z includes 0-to-two ticks.");
            }
            else
            {
                if (effect.RemainingTurns >= 1 && futureEnemyWindows >= 1) { y = y.Add(tick); z = z.Add(tick); }
                if (effect.RemainingTurns >= 2 && futureEnemyWindows >= 2) z = z.Add(tick);
            }
        }
        if (context?.WeatherKnown == false)
            notes.Add("One or more current row-effect scans are unavailable; unrecognized weather is not invented and may change Y/Z.");

        var producedWeather = ProducedWeather(value.Position, value.After);
        if (producedWeather.Count > 0)
        {
            foreach (var produced in producedWeather)
            {
                var tick = fucusyaAssumption && produced.Name.Equals("Rain", StringComparison.OrdinalIgnoreCase)
                    ? new PointRange(2, 2) : Weather(produced.Name, weatherPosition, produced.AffectedSide, produced.Row, value.Side);
                if (produced.OneTurnTicks > 0 && futureEnemyWindows >= 1) y = y.Add(tick);
                if (produced.TwoTurnTicks > 0 && futureEnemyWindows >= 1) z = z.Add(tick);
                if (produced.TwoTurnTicks > 1 && futureEnemyWindows >= 2) z = z.Add(tick);
                notes.Add($"Candidate-created {produced.Name} contributes {produced.OneTurnTicks} tick in Y and {produced.TwoTurnTicks} ticks in Z.");
            }
        }
        else if (value.After is null && SpawnWeather.Match(PlayRules.Normalize(value.Card.AbilityText)) is { Success: true } candidateWeather)
        {
            var other = value.Side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User;
            var ticks = Enum.GetValues<BoardRow>().Select(row => Weather(candidateWeather.Groups["name"].Value,
                weatherPosition, other, row, value.Side)).ToArray();
            var best = new PointRange(ticks.Max(tick => tick.Minimum), ticks.Max(tick => tick.Maximum));
            var turns = int.Parse(candidateWeather.Groups["turns"].Value);
            // ApproximatePointModel already includes the first favorable weather tick in Y.
            if (futureEnemyWindows == 0) { y = y.Add(-best.Maximum); z = z.Add(-best.Maximum); }
            else if (turns >= 2 && futureEnemyWindows >= 2) z = z.Add(best);
            notes.Add($"Candidate-created {candidateWeather.Groups["name"].Value} first tick is in the bounded Y estimate and its second tick is added to Z; target row is optimized within the visible board.");
        }

        bool? relevant = context?.CardsInHandBeforePlay switch { <= 1 => false, null => null, _ => true };
        if (relevant == false) notes.Add("Z is faded because this is the side's last known card; the round may end before a second own-turn horizon.");
        else if (relevant is null) notes.Add("Remaining hand count unread; Z horizon relevance is uncertain.");
        notes.Add("X card only · Y card plus first engine/timed window · Z adds only automatic pass-safe growth through the second window.");
        if (context?.OpposingPassed == true) notes.Add("Weather-affected side has passed; no further weather window is counted.");
        else if (context is { CardsInHandBeforePlay: <= 1, OpposingCardsInHand: 0 })
            notes.Add("Both sides are out of card actions after this candidate; one terminal opposing engine/weather turn is counted, not a second.");
        if (approximate)
        {
            if (value.Estimate is not null) x = x.MarkApproximate();
            y = y.MarkApproximate(); z = z.MarkApproximate();
            notes.Add("* Reach includes an explicit bounded/flat interaction assumption.");
        }
        return new(x, y, z, relevant, notes.Distinct().ToArray());
    }

    private sealed record ProducedRowWeather(PlayerSide AffectedSide, BoardRow Row, string Name, int OneTurnTicks, int TwoTurnTicks);
    private static IReadOnlyList<ProducedRowWeather> ProducedWeather(GamePosition before, GamePosition? after)
    {
        if (before.RowEffects is null || after?.RowEffects is null) return [];
        var result = new List<ProducedRowWeather>();
        foreach (var effect in after.RowEffects.Value)
        {
            if (effect.RemainingTurns is null) continue;
            var prior = before.RowEffects.Value.FirstOrDefault(item => item.AffectedSide == effect.AffectedSide && item.Row == effect.Row &&
                item.Name.Equals(effect.Name, StringComparison.OrdinalIgnoreCase))?.RemainingTurns ?? 0;
            var one = Math.Max(0, Math.Min(effect.RemainingTurns.Value, 1) - Math.Min(prior, 1));
            var two = Math.Max(0, Math.Min(effect.RemainingTurns.Value, 2) - Math.Min(prior, 2));
            if (one > 0 || two > 0) result.Add(new(effect.AffectedSide, effect.Row, effect.Name, one, two));
        }
        return result;
    }

    private PointRange AutomaticFallback(GamePosition position, PlayerSide side)
    {
        var low = 0; var high = 0;
        foreach (var card in position.Zones.Where(zone => zone.Side == side && zone.Zone == CardZone.Board).SelectMany(zone => zone.Cards))
        {
            if (card.Statuses?.Contains(CardStatus.Vitality) == true && Remaining(card, CardStatus.Vitality)) { low++; high++; }
            if (card.Statuses?.Contains(CardStatus.Bleeding) == true && Remaining(card, CardStatus.Bleeding)) { low--; high--; }
            if (card.Statuses?.Contains(CardStatus.Locked) == true) continue;
            var reaction = _book.Rules.GetValueOrDefault(card.CardId)?.Reaction;
            if (reaction is "hound" or "warcrier") high++;
            if (reaction == "tugo") high += 1;
        }
        return new(Math.Min(low, high), Math.Max(low, high));
    }

    private PointRange ExistingManualReach(GamePosition position, PlayerSide side)
    {
        var enemies = position.Zones.Where(zone => zone.Side != side && zone.Zone == CardZone.Board)
            .SelectMany(zone => zone.Cards).Where(card => _cards.GetValueOrDefault(card.CardId)?.Kind == CardKind.Unit).ToArray();
        var minimum = 0; var maximum = 0; var approximate = false;
        foreach (var card in position.Zones.Where(zone => zone.Side == side && zone.Zone == CardZone.Board).SelectMany(zone => zone.Cards))
        {
            if (card.Statuses?.Contains(CardStatus.Locked) == true || !_book.Rules.TryGetValue(card.CardId, out var rule)) continue;
            if (rule.Reaction == "manual-engine-2") { minimum += 2; maximum += 2; approximate = true; continue; }
            if (rule.Reaction == "tactic-charge" && card.Charges != 0) { maximum += 1; approximate = true; continue; }
            if (rule.Reaction == "order-armor" && card.Charges > 0 && card.Cooldown == 0)
            { minimum += card.Armor ?? 0; maximum += card.Armor ?? 0; continue; }
            if (rule.Card.Id == "152306" && card.Charges > 0 && card.Cooldown == 0)
            { minimum += card.Charges.Value; maximum += card.Charges.Value; continue; }
            if (rule.Order == "duel" && card.Charges > 0 && card.Cooldown == 0 && enemies.Length > 0)
            {
                var cap = 2 * (card.Power ?? rule.Card.Power);
                var values = enemies.Select(enemy => Math.Min(enemy.Power ?? cap, Math.Max(0, cap - (enemy.Armor ?? 0)))).ToArray();
                minimum += values.Min(); maximum += values.Max(); approximate = true; continue;
            }
            if (rule.Order == "professional" && card.Charges > 0 && card.Cooldown == 0 && enemies.Length > 0)
            {
                var values = enemies.Select(enemy => enemy.Power is { } power && power % 3 == 0 ? power :
                    Math.Min(enemy.Power ?? 3, Math.Max(0, 3 - (enemy.Armor ?? 0)))).ToArray();
                minimum += values.Min(); maximum += values.Max();
            }
        }
        return new(minimum, maximum, Approximate: approximate);
    }

    private static bool Remaining(PositionCard card, CardStatus status) =>
        card.StatusTurns is null || !card.StatusTurns.TryGetValue(status, out var turns) || turns > 0;

    private PointRange Weather(string name, GamePosition position, PlayerSide affected, BoardRow row, PlayerSide actor)
    {
        var zone = position.Zone(affected, CardZone.Board, row);
        var units = zone.Cards.Where(card => _cards.GetValueOrDefault(card.CardId)?.Kind == CardKind.Unit).ToArray();
        var losses = name.ToLowerInvariant() switch
        {
            "frost" => SelectPower(units, highest: true, 2),
            "fog" => SelectPower(units, highest: false, 2),
            "rain" => RandomDistinct(units, 2, 1),
            "storm" => SumDamage(units, 1),
            "cataclysm" => SplitDamage(units, 3),
            "blood moon" => BloodMoon(units),
            _ => new PointRange(0, 0)
        };
        if (!zone.Complete) losses = new(0, Math.Max(losses.Maximum, name.Equals("Storm", StringComparison.OrdinalIgnoreCase) ? 9 : 3));
        return affected == actor ? new(-losses.Maximum, -losses.Minimum) : losses;
    }

    private static PointRange SelectPower(PositionCard[] units, bool highest, int amount)
    {
        if (units.Length == 0) return new(0, 0);
        var known = units.Where(unit => unit.Power is not null).ToArray();
        if (known.Length != units.Length) return new(0, amount);
        var power = highest ? known.Max(unit => unit.Power!.Value) : known.Min(unit => unit.Power!.Value);
        var targets = known.Where(unit => unit.Power == power).Select(unit => Damage(unit, amount)).ToArray();
        return new(targets.Min(), targets.Max());
    }

    private static PointRange RandomDistinct(PositionCard[] units, int count, int amount)
    {
        if (units.Length == 0) return new(0, 0);
        var values = units.Select(unit => Damage(unit, amount)).Order().ToArray();
        count = Math.Min(count, values.Length);
        return new(values.Take(count).Sum(), values.TakeLast(count).Sum());
    }

    private static PointRange SumDamage(PositionCard[] units, int amount)
    {
        var values = units.Select(unit => Damage(unit, amount)).ToArray();
        return new(values.Sum(), values.Sum());
    }

    private static PointRange SplitDamage(PositionCard[] units, int amount)
    {
        if (units.Length == 0) return new(0, 0);
        var outcomes = new List<int>();
        void Visit(int remaining, int[] hits)
        {
            if (remaining == 0) { outcomes.Add(units.Select((unit, index) => Damage(unit, hits[index])).Sum()); return; }
            for (var i = 0; i < units.Length; i++) { hits[i]++; Visit(remaining - 1, hits); hits[i]--; }
        }
        Visit(amount, new int[units.Length]);
        return new(outcomes.Min(), outcomes.Max());
    }

    private static PointRange BloodMoon(PositionCard[] units)
    {
        if (units.Length == 0) return new(0, 0);
        var values = units.Select(unit =>
        {
            if (unit.Statuses is null || unit.Power is null) return new PointRange(0, 3);
            if (!unit.Statuses.Contains(CardStatus.Bleeding)) return new PointRange(1, 1); // applied Bleeding ticks at turn end
            var direct = Damage(unit, 2);
            var bleed = unit.Power.Value > direct ? 1 : 0;
            return new PointRange(direct + bleed, direct + bleed);
        }).ToArray();
        return new(values.Min(value => value.Minimum), values.Max(value => value.Maximum));
    }

    private static int Damage(PositionCard card, int amount)
    {
        if (amount <= 0 || card.Statuses?.Contains(CardStatus.Shield) == true) return 0;
        return Math.Min(card.Power ?? amount, Math.Max(0, amount - (card.Armor ?? 0)));
    }

    private static PointRange IncludeZero(PointRange value) => new(Math.Min(0, value.Minimum), Math.Max(0, value.Maximum));
    private static PointRange Negate(PointRange value) => new(-value.Maximum, -value.Minimum,
        value.Mean is null ? null : -value.Mean, value.Sigma, value.Approximate);
}
