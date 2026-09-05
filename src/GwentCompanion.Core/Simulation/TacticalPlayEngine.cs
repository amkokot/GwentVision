using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed record PlaySearchResult(int? MaximumPoints, int? BestModeledPoints, GamePosition? After,
    IReadOnlyList<string> Line, IReadOnlyList<string> Missing, IReadOnlyList<string> Assumptions,
    bool SearchComplete, bool FavorableRandomness, int Branches, ApproximatePointEstimate? Approximation = null,
    int? MinimumPoints = null, int? MinimumModeledPoints = null, bool UsedProjection = false,
    int? CardOnlyMinimum = null, int? CardOnlyMaximum = null)
{
    public bool ExactUnderSuppliedState => MaximumPoints is not null && Assumptions.Count == 0;
}

/// <summary>Bounded pure search over tiny positions. Unknown reactions invalidate a maximum, not silently become zero.
/// Each search owns its engine instance; no game-client integration, simulation images or Monte Carlo.</summary>
public sealed partial class TacticalPlayEngine
{
    private readonly IReadOnlyDictionary<string, PlayRule> _rules;
    private readonly IReadOnlyDictionary<string, string> _names;
    private readonly bool _projectionMode;
    private int _branches, _limit;
    private int _playedCardLimit = int.MaxValue;
    private bool _truncated;
    private CancellationToken _cancellation;
    private IReadOnlyDictionary<string, PlaySelection>? _selections;
    private GamePosition? _agentObjectivePosition;
    private PlayerSide _reachActor;
    private string? _rootPlayId;
    private readonly HashSet<string> _selectionErrors = [];
    private readonly HashSet<string> _orderVisits = [];
    public TacticalPlayEngine(IEnumerable<CardDefinition> catalog) : this(new PlayRuleBook(catalog)) { }
    public TacticalPlayEngine(PlayRuleBook book, bool allowReachProjection = false)
    {
        if (allowReachProjection) book = book.ForProjection;
        _rules = book.Rules; _names = book.Names; _projectionMode = book.IsProjection;
    }
    private sealed record LineState(GamePosition Position, ImmutableArray<string> Log, ImmutableHashSet<string> Missing,
        bool Random = false, int NextId = 0, ImmutableHashSet<string>? PlayedIds = null, int PlayCount = 0,
        int ReactivePoints = 0, int? CardOnlyPoints = null)
    {
        public LineState Note(string value) => this with { Log = Log.Add(value) };
        public LineState Unknown(string value) => this with { Missing = Missing.Add(value) };
    }
    public PlayRule? Rule(string id) => _rules.GetValueOrDefault(id);
    private static bool UnmodeledInZone(PlayRule rule, CardZone zone, PositionCard? card = null) => rule.Unmodeled is not null &&
        !(rule.Reaction == "portal-timer" && zone == CardZone.Board && card?.Cooldown == 0) &&
        (zone == CardZone.Board || zone is CardZone.Deck or CardZone.Graveyard or CardZone.Hand &&
            rule.Reaction != "portal-timer" && PlayRules.HasOffBoardEffect(rule.Card));
    private static PlayerSide Other(PlayerSide side) => side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User;
    private static PositionZone? Locate(GamePosition p, string id) => p.Zones.FirstOrDefault(zone => zone.Cards.Any(card => card.InstanceId == id));
    private static PositionCard? Find(GamePosition p, string id) => Locate(p, id)?.Cards.FirstOrDefault(card => card.InstanceId == id);
    private static IEnumerable<PositionCard> Board(GamePosition p, PlayerSide? side = null) => p.Zones.Where(zone => zone.Zone == CardZone.Board && (side is null || zone.Side == side)).SelectMany(zone => zone.Cards);
    private static GamePosition Set(GamePosition p, PositionZone zone, IEnumerable<PositionCard> cards)
    {
        var values = cards.ToImmutableArray(); var delta = values.Length - zone.Cards.Length;
        return p with { Zones = p.Zones.Select(item => item.Side == zone.Side && item.Zone == zone.Zone && item.Row == zone.Row
            ? zone with { Cards = values, TotalCount = zone.TotalCount is { } total ? Math.Max(values.Length, total + delta) : null } : item).ToImmutableArray() };
    }
    private static GamePosition Change(GamePosition p, PositionCard card)
    {
        var zone = Locate(p, card.InstanceId); return zone is null ? p : Set(p, zone, zone.Cards.Select(item => item.InstanceId == card.InstanceId ? card : item));
    }
    private static GamePosition Remove(GamePosition p, string id)
    {
        var zone = Locate(p, id); return zone is null ? p : Set(p, zone, zone.Cards.Where(card => card.InstanceId != id));
    }
    private int Score(GamePosition p, PlayerSide side) => Board(p, side).Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).Sum(card => card.Power ?? 0);
    private int Gap(GamePosition p, PlayerSide side) => Score(p, side) - Score(p, Other(side));
    private static int ReachStored(GamePosition position, PlayerSide side) => (position.CardValues ?? [])
        .Where(value => value.Side == side && value.Kind is "reach-carryover" or "transient-reach")
        .Sum(value => value.Maximum ?? value.Minimum ?? 0);
    // Newly gained Coins are reach at 1 Coin = 1 point. Spending an existing reserve is not charged
    // against the board swing: the Fee action is the conversion the threat estimate is trying to show.
    private int ReachSwing(GamePosition before, GamePosition after, PlayerSide side)
    {
        var prior = side == PlayerSide.User ? before.User.Coins : before.Opponent.Coins;
        var current = side == PlayerSide.User ? after.User.Coins : after.Opponent.Coins;
        var gained = prior is not null && current is not null ? Math.Max(0, current.Value - prior.Value) : 0;
        var stored = ReachStored(after, side) - ReachStored(before, side) -
            (ReachStored(after, Other(side)) - ReachStored(before, Other(side)));
        return Gap(after, side) - Gap(before, side) + gained + stored;
    }
    private bool Spend()
    {
        _cancellation.ThrowIfCancellationRequested();
        GwentCompanion.Core.Vision.ReachWorkScope.Checkpoint();
        if (_branches >= _limit) { _truncated = true; return false; }
        _branches++; return true;
    }
    private IEnumerable<LineState> Bound(IEnumerable<LineState> states)
    {
        foreach (var state in states) { if (!Spend()) yield break; yield return state; }
    }
    public PlaySearchResult Maximum(GamePosition position, string instanceId, PlayerSide side, bool endTurn = true,
        int branchLimit = 4096, CancellationToken cancellationToken = default)
        => SearchPlay(position, instanceId, side, endTurn, branchLimit, cancellationToken, null, true);

    /// <summary>Live one-card estimate: automatic reactions, bounded recursive fetched/created plays, and the newly played cards' ready Orders.
    /// Existing board Orders and longer play chains are not searched. This is not a full-turn maximum.</summary>
    public PlaySearchResult ImmediateMaximum(GamePosition position, string instanceId, PlayerSide side,
        int branchLimit = 1536, CancellationToken cancellationToken = default)
        => SearchPlay(position, instanceId, side, true, branchLimit, cancellationToken, null, true, immediate: true);

    private PlaySearchResult SearchPlay(GamePosition position, string instanceId, PlayerSide side, bool endTurn,
        int branchLimit, CancellationToken cancellationToken, IReadOnlyDictionary<string, PlaySelection>? selections, bool spendOrders, bool immediate = false)
    {
        _selections = selections; _selectionErrors.Clear(); _orderVisits.Clear();
        _agentObjectivePosition = position;
        _reachActor = side; _rootPlayId = instanceId;
        _playedCardLimit = immediate ? 2 : int.MaxValue;
        _branches = 0; _limit = Math.Clamp(branchLimit, 32, 20000); _truncated = false; _cancellation = cancellationToken;
        var assumptions = position.Zones.Where(zone => !zone.Complete).Select(zone => $"Partial {zone.Side} {zone.Zone}: unseen identities/effects excluded").Distinct().ToList();
        if (!position.RowEffectsKnownInactive && position.RowEffects is null) assumptions.Add("No unobserved row effects assumed");
        if (immediate) assumptions.Add("One-card estimate: immediate reactions, recursive generated choices, one fetched play, and newly played cards' ready Orders only; existing board Orders excluded.");
        var input = new LineState(position, [], ImmutableHashSet<string>.Empty);
        var expected = GamePosition.EmptyKnown().Zones;
        if (position.Zones.Length != 12 || expected.Any(zone => position.Zones.Count(item => item.Side == zone.Side && item.Zone == zone.Zone && item.Row == zone.Row) != 1))
            return new(null, null, null, [], ["Malformed position zones"], [], true, false, 0);
        var cards = position.Zones.SelectMany(zone => zone.Cards).ToArray();
        if (cards.Select(card => card.InstanceId).Distinct().Count() != cards.Length ||
            position.Zones.Any(zone => zone.Cards.Length > (zone.Zone == CardZone.Board ? 9 : 200)))
            return new(null, null, null, [], ["Invalid instance identity or zone capacity"], [], true, false, 0);
        foreach (var zone in position.Zones)
        foreach (var card in zone.Cards)
        {
            var rule = Rule(card.CardId);
            if (rule is null) { input = input.Unknown("Unknown card " + card.CardId); continue; }
            if (rule.Partial is not null && (zone.Zone == CardZone.Board || zone.Zone == CardZone.Hand && card.InstanceId == instanceId))
                assumptions.Add(rule.Card.Name + ": " + rule.Partial);
            if (UnmodeledInZone(rule, zone.Zone, card) && !(immediate && zone.Zone==CardZone.Board &&
                DormantInImmediatePlay(rule,zone.Side,side,position,instanceId)))
                input = input.Unknown(rule.Card.Name + ": unmodeled reaction/ability");
            if (zone.Zone == CardZone.Board && (card.Power is null || card.BasePower is null || card.Armor is null || card.Statuses is null))
                return new(null, null, null, [], ["Unread board power/armor/statuses: " + rule.Card.Name], assumptions, true, false, _branches);
            if (card.Power < 0 || card.Armor < 0 || card.BasePower < 0) return new(null, null, null, [], ["Invalid negative stats"], assumptions, true, false, _branches);
            if (zone.Zone == CardZone.Board && card.Statuses?.Any(status => status == CardStatus.Infused && card.ExtraThrive == 0) == true)
                input = input.Unknown(rule.Card.Name + ": infusion effect unresolved");
        }
        foreach (var resources in new[] { position.User, position.Opponent })
        {
            if (resources.CurrentLeaderId is { } leader && !LeaderPassiveSupported(leader)) input = input.Unknown("Unmodeled leader passive: " + (Rule(leader)?.Card.Name ?? leader));
            else if (resources.CurrentLeaderId is null && !resources.PassiveEffectsKnownInactive) assumptions.Add(resources.Side + " leader/passive unknown");
        }
        var source = Locate(position, instanceId);
        if (source?.Side != side || source.Zone != CardZone.Hand)
            return new(null, null, null, [], ["Choose a card in this side's hand (hypothesized hand must be labeled)"], assumptions, true, false, _branches);
        var states = Play(input, instanceId, side, 0);
        if (spendOrders) states = states.SelectMany(state => Orders(state, side, 0, immediate ? state.PlayedIds : null)
            .Select(output => output with { CardOnlyPoints = state.CardOnlyPoints is { } direct ? direct +
                ReachSwing(state.Position, output.Position, side) - (output.ReactivePoints - state.ReactivePoints) : null }));
        if (endTurn) states = states.SelectMany(state => EndTurn(state, side, 0));
        LineState? best = null; int? points = null; int? minimum = null; var allMissing = input.Missing;
        string? replayOutcome = null;
        int? minimumDirect = null;
        foreach (var state in states)
        {
            if (_selections is not null)
            {
                var outcome = PositionNotation.Write(state.Position);
                if (replayOutcome is not null && replayOutcome != outcome) allMissing = allMissing.Add("Unresolved random outcome in recorded play");
                replayOutcome = outcome;
            }
            allMissing = allMissing.Union(state.Missing);
            var value = ReachSwing(position, state.Position, side);
            minimum = Math.Min(minimum ?? int.MaxValue, value);
            if (state.CardOnlyPoints is { } direct) minimumDirect = Math.Min(minimumDirect ?? direct, direct);
            if (points is null || value > points) { points = value; best = state; }
        }
        allMissing = allMissing.Union(_selectionErrors);
        if (_truncated) allMissing = allMissing.Add("Search budget reached; best found is not a proven maximum");
        if (best is null) allMissing = allMissing.Add("No supported legal play/row available");
        if (best?.Log.Any(entry => entry.StartsWith("[agent]", StringComparison.Ordinal)) == true)
            assumptions.Add("Greedy internal-player policy: locally best supported card-controlled choices, not a proven full-turn optimum.");
        // An unsupported alternative might exceed the best supported one, invalidating the maximum globally.
        return new(allMissing.Count == 0 ? points : null, points, best?.Position, best?.Log ?? [], allMissing.Order().ToArray(),
            assumptions, !_truncated, best?.Random ?? false, _branches, MinimumPoints: allMissing.Count == 0 ? minimum : null,
            MinimumModeledPoints: minimum, UsedProjection: _projectionMode,
            CardOnlyMinimum: best?.Random == true ? minimumDirect : best?.CardOnlyPoints, CardOnlyMaximum: best?.CardOnlyPoints);
    }

    private IEnumerable<LineState> Play(LineState state, string id, PlayerSide side, int depth, bool explicitSequenceStep = false)
    {
        var playedIds = state.PlayedIds ?? ImmutableHashSet<string>.Empty;
        if (!explicitSequenceStep && state.PlayCount >= _playedCardLimit && !_projectionMode)
        { yield return state.Unknown("Long play chain excluded from live estimate; no proven one-card maximum"); yield break; }
        state = state with { PlayedIds = playedIds.Add(id), PlayCount = state.PlayCount + 1 };
        if (depth > 12) { yield return state.Unknown("Nested play limit"); yield break; }
        var card = Find(state.Position, id); if (card is null || Rule(card.CardId) is not { } rule) { yield return state.Unknown("Unknown played card"); yield break; }
        if (rule.Unmodeled is not null || rule.UnmodeledDeploy is not null) { yield return state.Unknown(rule.Card.Name + ": played ability unsupported"); yield break; }
        if (rule.Card.Kind == CardKind.Special)
        {
            foreach (var output in Enter(state, id, side, BoardRow.Melee, 0, true, depth)) yield return output;
            yield break;
        }
        var placementSide = rule.Disloyal ? Other(side) : side;
        foreach (var row in SelectOptions(id, "row", Enum.GetValues<BoardRow>(), item => item.ToString(), Selection(id)?.Row?.ToString()))
        {
            var zone = state.Position.Zone(placementSide, CardZone.Board, row);
            if (zone.Cards.Length >= 9) continue;
            var adjacencyMatters = _selections is not null || rule.Effect is PlayEffect.AdjacentBoost or PlayEffect.AdjacentTransform || rule.FeeAction == "guard" || rule.Deathwish == "adjacent2" ||
                rule.Projection is { } projection && (projection.Orders.Any(step => step.Kind == "transform-right") || projection.Deploy.Any(step => step.Argument?.Contains("each side") == true)) ||
                rule.Reaction == "raffard-crew" ||
                Board(state.Position, side).Any(unit => Rule(unit.CardId)?.Reaction == "raffard-crew") ||
                Board(state.Position, side).Any(unit => Rule(unit.CardId)?.Deathwish == "adjacent2");
            foreach (var index in SelectOptions(id, "slot", Enumerable.Range(0, adjacencyMatters ? zone.Cards.Length + 1 : 1), item => item.ToString(), Selection(id)?.Slot?.ToString()))
            {
                foreach (var output in Enter(state, id, side, row, index, true, depth)) yield return output;
                if (_truncated) yield break;
            }
        }
    }
    private IEnumerable<LineState> Enter(LineState state, string id, PlayerSide side, BoardRow row, int slot, bool played, int depth)
    {
        if (!Spend()) yield break;
        if (depth > 12) { yield return state.Unknown("Trigger depth limit"); yield break; }
        var source = Locate(state.Position, id); var card = Find(state.Position, id);
        if (source is null || card is null || Rule(card.CardId) is not { } rule) yield break;
        if (rule.Veteran && state.Position.Round is >= 2 and <= 3 && card.Power == rule.Card.Power && card.BasePower == rule.Card.Power)
        {
            var veteran = state.Position.Round.Value - 1;
            card = card with { Power = card.Power + veteran, BasePower = card.BasePower + veteran };
        }
        var before = state.Position;
        var p = Remove(before, id);
        if (played || source.Zone == CardZone.Graveyard)
            card = card with { Charges = rule.InitialCharges, Cooldown = rule.FeeCost > 0 || rule.Zeal ? 0 : 1 };
        if (rule.PowerInvariant == "armor" && card.Armor is { } armor) card = card with { Power = armor, BasePower = armor };
        if (played && rule.Reaction == "portal-timer") card = card with { Charges = 1, Cooldown = 3 };
        if (source.Zone == CardZone.Graveyard)
            card = card with { Power = card.BasePower ?? rule.Card.Power, BasePower = card.BasePower ?? rule.Card.Power, Armor = rule.Card.PrintedArmor ?? 0,
                Statuses = card.Statuses?.Contains(CardStatus.Doomed) == true ? rule.PrintedStatuses!.Add(CardStatus.Doomed) : rule.PrintedStatuses,
                StatusTurns = ImmutableDictionary<CardStatus, int>.Empty,
                Charges = rule.InitialCharges, Cooldown = rule.FeeCost > 0 || rule.Zeal ? 0 : 1, ExtraThrive = 0 };
        if (card.Power is null || card.BasePower is null || card.Armor is null || card.Statuses is null)
        { yield return state.Unknown("Unknown entry stats on " + rule.Card.Name); yield break; }
        var placementSide = played && rule.Disloyal ? Other(side) : side;
        if (rule.Card.Kind != CardKind.Special)
        {
            var zone = p.Zone(placementSide, CardZone.Board, row);
            if (zone.Cards.Length >= 9) { yield return state; yield break; }
            p = Set(p, zone, zone.Cards.Insert(Math.Clamp(slot, 0, zone.Cards.Length), card));
        }
        var entered = (state with { Position = p }).Note($"{(played ? "Play" : "Summon")} {rule.Card.Name} · {placementSide} {row}");
        if (rule.Card.Kind == CardKind.Unit)
        {
            // Status-bearing entries notify enemy status engines once per carried status.
            foreach (var status in card.Statuses)
            {
                entered = ReceivedStatus(entered, id);
                if (status == CardStatus.Poison) entered = PoisonCoinListeners(entered);
            }
            if (played && rule.Disloyal && !card.Statuses.Contains(CardStatus.Spying))
                entered = GrantSpying(entered, id, side);
        }
        if (rule.Deathwish == "consumed-copy")
            entered = entered with { Position = SetCardValue(entered.Position,
                new(side, card.CardId, "consumed-" + id)) };
        if (played && rule.Formation && Find(entered.Position, id) is { } formationCard)
        {
            if (row == BoardRow.Melee)
                entered = entered with { Position = Change(entered.Position, formationCard with { Cooldown = 0 }) };
            else if (rule.Card.Kind == CardKind.Unit)
                entered = Boost(entered.Note("Formation ranged boost"), id, 1);
        }
        if (played && rule.Card.Kind == CardKind.Unit)
        {
            var current = entered.Position;
            var owner = side == PlayerSide.User ? current.User : current.Opponent;
            owner = owner with { LastPlayedUnitId = id };
            entered = entered with { Position = side == PlayerSide.User ? current with { User = owner } : current with { Opponent = owner } };
        }
        if (!played && rule.Unmodeled is not null) entered = entered.Unknown("Unmodeled summoned ability: " + rule.Card.Name);
        IEnumerable<LineState> states = rule.Profit > 0 ? [GainCoins(entered.Note($"Profit {rule.Profit}"), side, rule.Profit)] : [entered];
        if (played && !card.Statuses.Contains(CardStatus.Locked) && (rule.RequiredRow is null || rule.RequiredRow == row))
            states = states.SelectMany(item => Effect(item, card, rule, side, row, before, depth + 1));
        if (played && id == _rootPlayId && _agentObjectivePosition is not null)
            states = states.Select(item => item with { CardOnlyPoints = ReachSwing(_agentObjectivePosition, item.Position, side) - item.ReactivePoints });
        if (played) states = states.SelectMany(item => Reactions(item, card, side, row, before, depth + 1));
        foreach (var item in states)
        {
            if (rule.Card.Kind != CardKind.Special) { yield return item; continue; }
            var zone = item.Position.Zone(side, card.Statuses.Contains(CardStatus.Doomed) ? CardZone.Banished : CardZone.Graveyard);
            yield return item with { Position = Set(item.Position, zone, zone.Cards.Add(card with { Statuses = ImmutableHashSet<CardStatus>.Empty, StatusTurns = null })) };
        }
    }
    private bool CanTarget(GamePosition p, PositionCard card, PlayerSide actor, TargetSide target, bool unitsOnly = true)
    {
        var zone = Locate(p, card.InstanceId); if (zone?.Zone != CardZone.Board) return false;
        if (unitsOnly && Rule(card.CardId)?.Card.Kind != CardKind.Unit) return false;
        if (target == TargetSide.Allied && zone.Side != actor || target == TargetSide.Enemy && zone.Side == actor) return false;
        if (card.Statuses?.Contains(CardStatus.Immune) == true) return false;
        return zone.Side == actor || card.Statuses?.Contains(CardStatus.Defender) == true || !zone.Cards.Any(item => item.Statuses?.Contains(CardStatus.Defender) == true);
    }
    private IEnumerable<LineState> Effect(LineState state, PositionCard played, PlayRule rule, PlayerSide side, BoardRow row, GamePosition before, int depth)
    {
        if (rule.Condition is { } key && PlayConditions.Find(key) is { } requirement)
        {
            var eligibility = PlayConditions.Evaluate(requirement, before, side, Rule);
            if (eligibility == ConditionTruth.NotMet) return [state.Note("Condition not met: " + key)];
            if (eligibility == ConditionTruth.Unknown) return [state.Unknown("Condition unread: " + key)];
        }
        if (rule.Effect == PlayEffect.None) return [state];
        if (rule.Effect == PlayEffect.Projection)
            return ProjectionSequence(state, played, side, row, before, rule.Projection!.Deploy, depth);
        if (rule.Effect == PlayEffect.HenGaidth) return HenGaidth(state, played, rule, side, depth);
        if (rule.Effect == PlayEffect.FrogMatingSeason) return FrogMatingSeason(state, played, side, depth);
        if (rule.Effect == PlayEffect.Aerondight) return Aerondight(state, played, side, depth);
        if (rule.Effect == PlayEffect.Scenario) return ScenarioPrologue(state, played, side, row, depth);
        if (rule.Effect is PlayEffect.OrderDeckByProvision or PlayEffect.GiveSpying or PlayEffect.Fucusya or
            PlayEffect.Artaud or PlayEffect.TorresFounder or PlayEffect.TorresPriest or PlayEffect.Emhyr or PlayEffect.Aucwenn)
            return StrategicCardEffect(state, played, rule, side, row, before, depth);
        if (rule.Effect is PlayEffect.BattleStations or PlayEffect.Abordage or PlayEffect.LippyReach or
            PlayEffect.Birna or PlayEffect.Erland or PlayEffect.SelfDamage or PlayEffect.GeraltProfessional or
            PlayEffect.ChampionCharge or PlayEffect.CoupDeGrace or PlayEffect.Sihil or PlayEffect.HaraldGord or
            PlayEffect.HighlandWarlord or PlayEffect.BountyBrute or PlayEffect.BountyIgnatius or PlayEffect.DimunCaptain or
            PlayEffect.BloodEagle or PlayEffect.BloodthirstDamage or PlayEffect.NovigradianJustice or
            PlayEffect.PhilippaBlindFury or PlayEffect.GeraltAard)
            return ReachClosureEffect(state, played, rule, side, row, before, depth);
        if (rule.Effect == PlayEffect.GreedyAgent)
            return GreedyAgentEffect(state, played, rule, side, row, before, depth);
        if (rule.Effect is PlayEffect.HandBaseCopy or PlayEffect.EnemyBronzeBaseCopy or PlayEffect.AlliedBronzeBaseCopy or
            PlayEffect.ControlledUnitDamage or PlayEffect.NatureRebuke or PlayEffect.MultiCategoryBoost or
            PlayEffect.StartingDeckBonded or PlayEffect.AdjacentTripleBoost)
            return GapClosureEffect(state, played, rule, side, row, depth);
        if (rule.Effect is PlayEffect.CreatePlay or PlayEffect.SpawnPlayChoice or PlayEffect.StartingDeckTacticSpawns or
            PlayEffect.HandDiscardBoost or PlayEffect.StartingDeckCategoryBoost or PlayEffect.AdjacentTransform or PlayEffect.TreantBoar)
            return RecursiveChoiceEffect(state, played, rule, side, row, before, depth);
        if (rule.Effect is PlayEffect.DrawTopUnitsShuffle or PlayEffect.EnemyRowCountBoost or PlayEffect.ChoiceBuff or
            PlayEffect.Ida or PlayEffect.CaravanVanguard or PlayEffect.OrchardMantrap or PlayEffect.VeteranBerserk)
            return StraightforwardEffect(state, played, rule, side, row, before, depth);
        if (rule.Effect == PlayEffect.RandomSplit)
        {
            var amount = rule.Amount + (rule.Argument == "engines" ? Board(state.Position, side).Count(card => Rule(card.CardId)?.Card.HasCategory("Siege Engine") == true) : 0);
            IEnumerable<LineState> randomStates = [state with { Random = true }];
            for (var hit = 0; hit < amount; hit++)
                randomStates = Bound(randomStates.SelectMany(s =>
                {
                    var targets = Board(s.Position, Other(side)).Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                    return targets.Length == 0 ? [s] : targets.SelectMany(target => Damage(s, target.InstanceId, 1, depth));
                })).ToArray();
            return randomStates;
        }
        if (rule.Effect is PlayEffect.BackupPlan or PlayEffect.NekkerWarrior or PlayEffect.Oakcritters or PlayEffect.Agitator or PlayEffect.Braenn or PlayEffect.Skirmisher or PlayEffect.Artorius or PlayEffect.Buhurt)
            return RecordedEffect(state, played, rule, side, row, before, depth);
        if (rule.Effect == PlayEffect.SpawnPlay) return SpawnAndPlay(state, rule.Argument!, side, depth);
        if (rule.Effect == PlayEffect.SelfDuration)
            return [Duration(state, played.InstanceId, rule.Argument == "Bleeding" ? CardStatus.Bleeding : CardStatus.Vitality, rule.Amount)];
        if (rule.Effect == PlayEffect.SpawnWeather)
        {
            if (state.Position.RowEffects is null) return [state.Unknown(rule.Card.Name + ": current row effects unread")];
            var affected = Other(side);
            return Bound(Enum.GetValues<BoardRow>().Select(targetRow =>
            {
                var effects = state.Position.RowEffects.Value;
                var existing = effects.FirstOrDefault(effect => effect.AffectedSide == affected && effect.Row == targetRow &&
                    effect.Name.Equals(rule.Argument, StringComparison.OrdinalIgnoreCase));
                if (effects.Any(effect => effect.AffectedSide == affected && effect.Row == targetRow && existing is null))
                    return state.Unknown(rule.Card.Name + ": replacement of a different row effect is unresolved");
                if (existing is { RemainingTurns: null }) return state.Unknown(rule.Card.Name + ": current duration unread");
                effects = existing is null ? effects.Add(new(affected, targetRow, rule.Argument!, rule.Amount)) :
                    effects.Replace(existing, existing with { RemainingTurns = existing.RemainingTurns + rule.Amount });
                return state with { Position = state.Position with { RowEffects = effects, RowEffectsKnownInactive = false } };
            }));
        }
        if (rule.Effect == PlayEffect.PlayAllCopies)
        {
            var deck = state.Position.Zone(side, CardZone.Deck);
            if (!deck.Complete) return [state.Unknown(rule.Card.Name + ": complete ordered deck inventory required")];
            bool Eligible(PositionCard card) => Rule(card.CardId)?.Card is { } definition && (rule.Argument switch
            {
                "weather" => definition.Name is "Biting Frost" or "Impenetrable Fog" or "Torrential Rain",
                "bronze-special" => definition.Kind == CardKind.Special && !definition.IsGold,
                _ => false
            });
            var groups = deck.Cards.Where(Eligible).GroupBy(card => card.CardId).ToArray();
            if (groups.Length == 0) return [state.Note("No eligible all-copies deck target")];
            return Bound(SelectOptions(played.InstanceId, "all-copies identity", groups, group => group.Key, Selection(played.InstanceId)?.TutorId)
                .SelectMany(group => Sequential([state.Note("Play all copies of " + Rule(group.Key)?.Card.Name)], group.Select(card => card.InstanceId),
                    (line, id) => Locate(line.Position, id)?.Zone == CardZone.Deck ? Play(line, id, side, depth, explicitSequenceStep: true) : [line])));
        }
        if (rule.Effect == PlayEffect.PlayTop)
        {
            var count = rule.Argument == "any" ? rule.Amount : 1;
            IEnumerable<LineState> outputs = [state];
            for (var index = 0; index < count; index++)
                outputs = Bound(outputs.SelectMany(line => PlayTop(line, side, depth, rule.Argument!, rule.Argument == "non-disloyal-unit" ? rule.Amount : 0))).ToArray();
            return outputs;
        }
        if (rule.Effect == PlayEffect.PlayGoldenNekker)
        {
            var resources = side == PlayerSide.User ? state.Position.User : state.Position.Opponent;
            if (resources.StartingDeckIds is null) return [state.Unknown("Golden Nekker: starting-deck provision inventory unread")];
            var definitions = resources.StartingDeckIds.Select(id => Rule(id)?.Card).ToArray();
            if (definitions.Any(card => card is null)) return [state.Unknown("Golden Nekker: unknown starting-deck identity")];
            if (definitions.Any(card => card!.Provision >= 10 && card.Name is not "Golden Nekker" and not "Ciri: Nova"))
                return [state.Note("Golden Nekker starting-deck condition not met")];
            if (!state.Position.Zone(side, CardZone.Deck).Complete) return [state.Unknown("Golden Nekker: complete ordered deck inventory required")];
            IEnumerable<LineState> outputs = [state];
            foreach (var kind in new[] { "unit", "special", "artifact" })
                outputs = Bound(outputs.SelectMany(line => PlayTop(line, side, depth, kind, 0))).ToArray();
            return outputs;
        }
        if (rule.Effect == PlayEffect.SpendAllCoinsDamage)
        {
            var resources = side == PlayerSide.User ? state.Position.User : state.Position.Opponent;
            if (resources.Coins is null) return [state.Unknown("Coin count needed to spend the pouch")];
            var amount = resources.Coins.Value;
            var spent = SetCoins(state.Note($"Spend {amount} Coins"), side, 0);
            var enemies = Board(spent.Position, Other(side)).Where(card => CanTarget(spent.Position, card, side, TargetSide.Enemy)).ToArray();
            return enemies.Length == 0 ? [spent] : SelectOptions(played.InstanceId, "target", enemies, card => card.InstanceId,
                Selection(played.InstanceId)?.TargetId).SelectMany(target => Damage(spent, target.InstanceId, amount, depth));
        }
        if (rule.Effect == PlayEffect.RowCountBoost)
            return [Boost(state, played.InstanceId, state.Position.Zone(side, CardZone.Board, row).Cards.Count(card => card.InstanceId != played.InstanceId && Rule(card.CardId)?.Card.Kind == CardKind.Unit))];
        if (rule.Effect == PlayEffect.OppositeBaseLoss)
        {
            var loss = state.Position.Zone(Other(side), CardZone.Board, row).Cards.Count(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit);
            var basePower = Math.Max(0, (played.BasePower ?? 0) - loss);
            return basePower == 0 ? Destroy(state, played.InstanceId, false, depth) :
                [state with { Position = Change(state.Position, played with { BasePower = basePower, Power = Math.Max(1, (played.Power ?? 0) - loss) }) }];
        }
        if (rule.Effect == PlayEffect.AdjacentBoost)
        {
            var cards = state.Position.Zone(side, CardZone.Board, row).Cards; var index = Array.FindIndex(cards.ToArray(), card => card.InstanceId == played.InstanceId);
            foreach (var neighbor in new[] { index - 1, index + 1 }.Where(i => i >= 0 && i < cards.Length).Select(i => cards[i]))
                if (Rule(neighbor.CardId)?.Card is { Kind: CardKind.Unit } definition && (rule.Argument is null || definition.HasCategory(rule.Argument)))
                    state = Boost(state, neighbor.InstanceId, rule.Amount);
            return [state];
        }
        if (rule.Effect == PlayEffect.Griffin)
        {
            var victims = state.Position.Zone(side, CardZone.Board, row).Cards.Where(card => card.InstanceId != played.InstanceId && CanTarget(state.Position, card, side, TargetSide.Allied)).ToArray();
            return victims.Length == 0 ? Destroy(state, played.InstanceId, false, depth) : Bound(victims.SelectMany(card => Destroy(state, card.InstanceId, false, depth)));
        }
        if (rule.Effect == PlayEffect.TriggerDeathwish)
        {
            var victims = Board(state.Position, side).Where(card => card.InstanceId != played.InstanceId && CanTarget(state.Position, card, side, TargetSide.Allied) &&
                Rule(card.CardId)?.Deathwish is not null && (rule.Argument != "bronze" || Rule(card.CardId)?.Card.IsGold == false)).ToArray();
            return victims.Length == 0 ? [state] : Bound(victims.SelectMany(card => card.Statuses!.Contains(CardStatus.Locked) ? [state] :
                Deathwish(state, card, side, Locate(state.Position, card.InstanceId)!.Row!.Value, depth)));
        }
        if (rule.Effect == PlayEffect.GraveMassBanish)
        {
            var victims = state.Position.Zone(side, CardZone.Graveyard).Cards.Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).ToArray();
            foreach (var victim in victims) state = BanishOffBoard(state, victim.InstanceId);
            return [Boost(state, played.InstanceId, victims.Length)];
        }
        if (rule.Effect == PlayEffect.ConsumeMany)
        {
            IEnumerable<LineState> states = [state];
            for (var index = 0; index < rule.Amount; index++)
                states = Bound(states.SelectMany(s =>
                {
                    var victims = Board(s.Position, side).Where(card => card.InstanceId != played.InstanceId && CanTarget(s.Position, card, side, TargetSide.Allied)).ToArray();
                    return victims.Length == 0 ? [s] : victims.SelectMany(card => Consume(s, played.InstanceId, card.InstanceId, depth));
                })).ToArray();
            return states;
        }
        if (rule.Effect == PlayEffect.AllOthers)
            return Sequential([state], Board(state.Position).Where(card => card.InstanceId != played.InstanceId && Rule(card.CardId)?.Card.Kind == CardKind.Unit).Select(card => card.InstanceId),
                (s, id) => row == BoardRow.Melee ? Damage(s, id, rule.Amount, depth) : [Boost(s, id, rule.Amount)]);
        if (rule.Effect == PlayEffect.SelfBoost) return [Boost(state, played.InstanceId, rule.Amount)];
        if (rule.Effect == PlayEffect.SpawnRowNamed)
        {
            var prefix = row + ":";
            var name = (rule.Argument ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
            return name is null ? [state.Unknown("Missing row-specific spawn definition")] : Spawn(state, _names.GetValueOrDefault(name), side, row, depth);
        }
        if (rule.Effect == PlayEffect.ClashHighest)
        {
            var enemies = Board(state.Position, Other(side)).Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).ToArray();
            if (enemies.Length == 0) return [state];
            var highest = enemies.Max(card => card.Power);
            return Bound(enemies.Where(card => card.Power == highest).SelectMany(target =>
                Clash(state with { Random = enemies.Count(card => card.Power == highest) > 1 }, played.InstanceId, target.InstanceId, depth)));
        }
        if (rule.Effect == PlayEffect.SummonProvision)
        {
            var deck = state.Position.Zone(side, CardZone.Deck);
            var choices = deck.Cards.Where(card => Rule(card.CardId)?.Card is { Kind: CardKind.Unit } definition &&
                definition.Provision == rule.Amount).ToArray();
            if (choices.Length == 0) return [deck.Complete ? state.Note("No eligible summon target") : state.Unknown("No known provision-matched summon target; draw-pile inventory incomplete")];
            return Bound(SelectOptions(played.InstanceId, "random summon", choices, card => card.InstanceId, Selection(played.InstanceId)?.TutorId)
                .SelectMany(card => Summon((state with { Random = true }).Note("Summon " + Rule(card.CardId)?.Card.Name), card.InstanceId, side, row, depth)));
        }
        if (rule.Effect == PlayEffect.GravePlay)
        {
            var arguments = (rule.Argument ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var category = arguments.FirstOrDefault(value => value.StartsWith("category:", StringComparison.OrdinalIgnoreCase))?[9..];
            var graveyardSide = arguments.Contains("opponent") ? Other(side) : side;
            var graveyard = state.Position.Zone(graveyardSide, CardZone.Graveyard);
            var maxProvision = arguments.FirstOrDefault(value => value.StartsWith("max-provision:", StringComparison.OrdinalIgnoreCase));
            var maximum = maxProvision is null ? (int?)null : int.Parse(maxProvision[14..]);
            var choices = graveyard.Cards.Where(card => Rule(card.CardId)?.Card is { } definition &&
                (!arguments.Contains("unit") || definition.Kind == CardKind.Unit) &&
                (!arguments.Contains("special") || definition.Kind == CardKind.Special) &&
                (!arguments.Contains("bronze") || !definition.IsGold) &&
                (!arguments.Contains("non-neutral") || definition.Faction != "Neutral") &&
                (maximum is null || definition.Provision <= maximum) &&
                (category is null || definition.HasCategory(category))).ToArray();
            if (choices.Length == 0) return [graveyard.Complete ? state.Note("No eligible graveyard play") : state.Unknown("No known graveyard target; inventory incomplete")];
            return Bound(SelectOptions(played.InstanceId, "graveyard play", choices, card => card.InstanceId, Selection(played.InstanceId)?.TutorId)
                .SelectMany(card =>
                {
                    var prepared = arguments.Contains("doomed") ? Status(state, card.InstanceId, CardStatus.Doomed) : state;
                    IEnumerable<LineState> outputs = Play(prepared.Note("Play from graveyard " + Rule(card.CardId)?.Card.Name), card.InstanceId, side, depth);
                    var selfDamage = arguments.FirstOrDefault(value => value.StartsWith("self-damage:", StringComparison.OrdinalIgnoreCase));
                    return selfDamage is null ? outputs : outputs.SelectMany(output => Damage(output, played.InstanceId, int.Parse(selfDamage[12..]), depth));
                }));
        }
        if (rule.Effect is PlayEffect.SpawnCopy or PlayEffect.SpawnNamed)
        {
            var name = rule.Argument ?? ""; var id = rule.Effect == PlayEffect.SpawnCopy ? played.CardId : _names.GetValueOrDefault(name) ?? _names.GetValueOrDefault(name.TrimEnd('s'));
            IEnumerable<LineState> states = [state];
            for (var count = 0; count < Math.Max(1, rule.Amount); count++) states = Bound(states.SelectMany(s => Spawn(s, id, side, row, depth))).ToArray();
            return states;
        }
        if (rule.Effect == PlayEffect.SummonCopies)
        {
            var condition = rule.Condition switch
            {
                "dominance" => Board(before, side).Any() && Board(before, side).Max(card => card.Power ?? 0) >= Board(before, Other(side)).Select(card => card.Power ?? 0).DefaultIfEmpty(0).Max(),
                "dwarf" => Board(before, side).Any(card => Rule(card.CardId)?.Card.HasCategory("Dwarf") == true), _ => true
            };
            return condition ? SummonAll(state, side, played.CardId, row, depth) : [state.Note("Summon condition inactive")];
        }
        if (rule.Effect == PlayEffect.Tutor)
        {
            var choices = state.Position.Zone(side, CardZone.Deck).Cards.Where(card => rule.Argument == "any" ||
                rule.Argument == "echo" && Rule(card.CardId)?.Echo == true ||
                rule.Argument == "non-neutral-unit" && Rule(card.CardId)?.Card is { Kind: CardKind.Unit } unit && unit.Faction != "Neutral" ||
                rule.Argument == "council" && Rule(card.CardId)?.Card is { } council && new[] { "Dwarf", "Dryad", "Elf" }.Any(council.HasCategory) ||
                rule.Argument == "unit" && Rule(card.CardId)?.Card.Kind == CardKind.Unit || rule.Argument == "special" && Rule(card.CardId)?.Card.Kind == CardKind.Special ||
                rule.Argument == "deathwish-unit" && Rule(card.CardId) is { Card.Kind: CardKind.Unit } deathwish &&
                    (deathwish.Deathwish is not null || deathwish.Card.AbilityText?.Contains("Deathwish:", StringComparison.Ordinal) == true) ||
                rule.Argument == "artifact" && Rule(card.CardId)?.Card.Kind == CardKind.Artifact || rule.Argument?.StartsWith("category:") == true && Rule(card.CardId)?.Card.HasCategory(rule.Argument[9..]) == true).ToArray();
            return choices.Length == 0 ? [state.Position.Zone(side, CardZone.Deck).Complete ? state.Note("No eligible tutor target") : state.Unknown("No known tutor target; draw-pile inventory incomplete")] : Bound(SelectOptions(played.InstanceId, "tutor", choices, card => card.InstanceId, Selection(played.InstanceId)?.TutorId)
                .SelectMany(card => Play((state with { Random = state.Random || rule.Argument == "council" }).Note("Tutor " + Rule(card.CardId)?.Card.Name), card.InstanceId, side, depth)
                    .Select(output => rule.Amount > 0 ? Boost(output, card.InstanceId, rule.Amount) : output)));
        }
        if (rule.Effect == PlayEffect.Resurrect)
        {
            var choices = state.Position.Zone(side, CardZone.Graveyard).Cards.Where(card => Rule(card.CardId)?.Card is { Kind: CardKind.Unit } definition && definition.Faction != "Neutral").ToArray();
            return choices.Length == 0 ? [state.Note("No known resurrection target")] : Bound(choices.SelectMany(card => SummonRows(state, card.InstanceId, side, depth)
                .Select(output => Locate(output.Position, card.InstanceId)?.Zone == CardZone.Board ? Status(output, card.InstanceId, CardStatus.Doomed) : output)));
        }
        if (rule.Effect is PlayEffect.GraveConsume or PlayEffect.GraveBanish)
        {
            var owner = rule.Effect == PlayEffect.GraveBanish || rule.Argument == "ozzrel" && row == BoardRow.Melee ? Other(side) : side;
            var choices = state.Position.Zone(owner, CardZone.Graveyard).Cards.Where(card => rule.Effect == PlayEffect.GraveBanish ||
                Rule(card.CardId)?.Card is { Kind: CardKind.Unit } definition && (rule.Argument != "bronze-own" || !definition.IsGold)).ToArray();
            return choices.Length == 0 ? [state.Note("No known graveyard target")] : Bound(SelectOptions(played.InstanceId, "graveyard target", choices,
                card => card.InstanceId, Selection(played.InstanceId)?.TargetId).Select(card =>
            {
                var output = BanishOffBoard(state, card.InstanceId);
                return rule.Effect == PlayEffect.GraveConsume ? Boost(output.Note("Consume graveyard " + Rule(card.CardId)?.Card.Name), played.InstanceId, card.Power ?? Rule(card.CardId)!.Card.Power) : output;
            }));
        }
        if (rule.Effect is PlayEffect.RowDamage or PlayEffect.RowBoost or PlayEffect.RowReset)
            return Bound(state.Position.Zones.Where(zone => zone.Zone == CardZone.Board && (rule.Target == TargetSide.Any || (rule.Target == TargetSide.Allied) == (zone.Side == side)))
                .SelectMany(zone => Sequential([state.Note("Target row " + zone.Side + " " + zone.Row)], zone.Cards.Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).Select(card => card.InstanceId),
                    (line, id) => rule.Effect == PlayEffect.RowDamage ? Damage(line, id, rule.Amount, depth) : [rule.Effect == PlayEffect.RowBoost ? Boost(line, id, rule.Amount) : Reset(line, id)])));
        var targets = Board(state.Position).Where(card => card.InstanceId != played.InstanceId && CanTarget(state.Position, card, side, rule.Target, rule.Effect != PlayEffect.Banish))
            .Where(card => rule.Effect != PlayEffect.Destroy || card.Power >= rule.Amount)
            .Where(card => rule.Effect != PlayEffect.Consume || rule.Amount <= 0 || card.Power <= rule.Amount)
            .Where(card => rule.Effect != PlayEffect.BanishSmall || card.Power <= rule.Amount).ToArray();
        if (targets.Length == 0) return [state.Note("No legal target; deploy/special has no effect")];
        return Bound(SelectOptions(played.InstanceId, "target", targets, card => card.InstanceId, Selection(played.InstanceId)?.TargetId).SelectMany(target =>
        {
            var s = TargetedBySpecial(state.Note("Target " + Rule(target.CardId)?.Card.Name + " #" + target.InstanceId),
                played, side, target.InstanceId);
            return rule.Effect switch
            {
                PlayEffect.Damage => Damage(s, target.InstanceId, RaidDamage(s, side, rule, rule.Amount), depth),
                PlayEffect.Boost => [Boost(s, target.InstanceId, rule.Amount)],
                PlayEffect.Heal => [Heal(s, target.InstanceId, rule.Amount)],
                PlayEffect.Reset => [Reset(s, target.InstanceId)],
                PlayEffect.SetPower => [SetPower(s, target.InstanceId, rule.Amount)],
                PlayEffect.Destroy => Destroy(s, target.InstanceId, false, depth),
                PlayEffect.Banish => Destroy(s, target.InstanceId, true, depth),
                PlayEffect.BanishSmall => Destroy(s, target.InstanceId, true, depth),
                PlayEffect.LockMove => Move(Status(s, target.InstanceId, CardStatus.Locked), target.InstanceId, depth, side),
                PlayEffect.BoostedDamage => Damage(s, target.InstanceId, played.Power > played.BasePower ? 3 : 1, depth),
                PlayEffect.Purify => [s with { Position = Change(s.Position, target with { Statuses = ImmutableHashSet<CardStatus>.Empty, StatusTurns = ImmutableDictionary<CardStatus, int>.Empty }) }],
                PlayEffect.Lock => [Status(s, target.InstanceId, CardStatus.Locked)],
                PlayEffect.Poison => ApplyPoison(s, target.InstanceId, depth),
                PlayEffect.Shield => [Status(Boost(s, target.InstanceId, rule.Amount), target.InstanceId, CardStatus.Shield)],
                PlayEffect.BoostArmor => [BoostArmor(s, target.InstanceId, rule.Amount)],
                PlayEffect.Preparation => [Preparation(s, target.InstanceId)],
                PlayEffect.Consume => Consume(s, played.InstanceId, target.InstanceId, depth),
                PlayEffect.Duration => [Duration(s, target.InstanceId,
                    rule.Argument == "Bleeding" ? CardStatus.Bleeding : CardStatus.Vitality, rule.Amount)],
                PlayEffect.MoveDamage => Move(s, target.InstanceId, depth, side).SelectMany(output => Damage(output, target.InstanceId, rule.Amount, depth)),
                PlayEffect.DamageThenBoost => Damage(s, target.InstanceId, rule.Amount, depth).Select(output => Boost(output, target.InstanceId, int.Parse(rule.Argument!))),
                _ => [s.Unknown("Unimplemented effect " + rule.Effect)]
            };
        }));
    }
    private IEnumerable<LineState> PlayTop(LineState state, PlayerSide side, int depth, string filter, int boost)
    {
        var deck = state.Position.Zone(side, CardZone.Deck);
        if (!deck.Complete) return [state.Unknown("Complete ordered deck inventory required")];
        bool Eligible(PositionCard card) => Rule(card.CardId) is { } candidate && filter switch
        {
            "any" => true,
            "unit" => candidate.Card.Kind == CardKind.Unit,
            "special" => candidate.Card.Kind == CardKind.Special,
            "artifact" => candidate.Card.Kind == CardKind.Artifact,
            "non-disloyal-unit" => candidate.Card.Kind == CardKind.Unit && !candidate.Disloyal,
            _ => false
        };
        var target = deck.Cards.FirstOrDefault(Eligible);
        if (target is null) return [state.Note("No eligible top-deck " + filter)];
        return Play(state.Note("Play top " + filter + ": " + Rule(target.CardId)?.Card.Name), target.InstanceId, side, depth, explicitSequenceStep: true)
            .Select(line => boost > 0 && Locate(line.Position, target.InstanceId)?.Zone == CardZone.Board ? Boost(line, target.InstanceId, boost) : line);
    }
    private IEnumerable<LineState> Sequential(IEnumerable<LineState> states, IEnumerable<string> ids, Func<LineState, string, IEnumerable<LineState>> operation)
    {
        foreach (var id in ids.ToArray()) { var key = id; states = Bound(states.SelectMany(state => operation(state, key))).ToArray(); if (_truncated) break; }
        return states;
    }

    private IEnumerable<LineState> Clash(LineState state, string alliedId, string enemyId, int depth)
    {
        var allied = Find(state.Position, alliedId); var enemy = Find(state.Position, enemyId);
        if (allied?.Power is null || enemy?.Power is null) return [state.Unknown("Clash powers unread")];
        var alliedDamage = enemy.Power.Value; var enemyDamage = allied.Power.Value;
        // Clash damage is simultaneous. Apply both packets from the original powers before resolving either Deathwish.
        var alliedResult = GwentRules.Damage(new(allied.Power.Value, allied.BasePower!.Value, allied.Armor!.Value, allied.Statuses!), alliedDamage);
        var enemyResult = GwentRules.Damage(new(enemy.Power.Value, enemy.BasePower!.Value, enemy.Armor!.Value, enemy.Statuses!), enemyDamage);
        var position = Change(Change(state.Position, allied with { Power = alliedResult.Unit.Power, Armor = alliedResult.Unit.Armor,
            Statuses = alliedResult.Unit.Statuses }), enemy with { Power = enemyResult.Unit.Power, Armor = enemyResult.Unit.Armor,
            Statuses = enemyResult.Unit.Statuses });
        IEnumerable<LineState> states = [state with { Position = position }];
        if (enemyResult.Destroyed) states = states.SelectMany(output => Destroy(output, enemyId, false, depth)).ToArray();
        if (alliedResult.Destroyed) states = states.SelectMany(output => Destroy(output, alliedId, false, depth)).ToArray();
        return states;
    }
}
