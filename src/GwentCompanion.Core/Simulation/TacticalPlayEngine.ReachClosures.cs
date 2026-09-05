using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

/// <summary>
/// Reach-oriented closures whose exact hidden choice is represented by the inferred hand/deck inventory.
/// The simulator still executes every retained endpoint, so board listeners and generated plays are shared
/// with ordinary card resolution rather than being added as flat points afterwards.
/// </summary>
public sealed partial class TacticalPlayEngine
{
    private static int KnownCount(PositionZone zone) => Math.Max(zone.Cards.Length, zone.TotalCount ?? 0);

    private int Stored(GamePosition position, PlayerSide side, string kind, string? cardId = null) =>
        (position.CardValues ?? []).Where(value => value.Side == side &&
            value.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) && (cardId is null || value.CardId == cardId))
            .Select(value => value.Maximum ?? value.Minimum ?? 0).DefaultIfEmpty(0).Max();

    private LineState AddStored(LineState state, PlayerSide side, string cardId, string kind, int amount)
    {
        if (amount == 0) return state;
        var current = (state.Position.CardValues ?? []).FirstOrDefault(value => value.Side == side &&
            value.CardId == cardId && value.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
        var minimum = Math.Max(0, (current?.Minimum ?? 0) + amount);
        var maximum = Math.Max(minimum, (current?.Maximum ?? current?.Minimum ?? 0) + amount);
        return state with { Position = SetCardValue(state.Position, new(side, cardId, kind, minimum, maximum)) };
    }

    private int RaidDamage(LineState state, PlayerSide side, PlayRule source, int printed) =>
        printed + (source.Card.HasCategory("Raid") ? Stored(state.Position, side, "raid-damage") : 0);

    private static bool? Bloodthirst(GamePosition position, PlayerSide side, int threshold)
    {
        var other = Other(side);
        var rows = position.Zones.Where(zone => zone.Side == other && zone.Zone == CardZone.Board).ToArray();
        var count = rows.SelectMany(zone => zone.Cards).Count(card => card.Power < card.BasePower);
        if (count >= threshold) return true;
        return rows.All(zone => zone.Complete) ? false : null;
    }

    private int ReachHeuristic(PositionCard card)
    {
        var rule = Rule(card.CardId);
        if (rule is null) return 0;
        var body = rule.Card.Kind == CardKind.Unit ? card.Power ?? rule.Card.Power : 0;
        return body + rule.Effect switch
        {
            PlayEffect.Damage or PlayEffect.Boost or PlayEffect.SelfBoost or PlayEffect.RowDamage or PlayEffect.RowBoost => rule.Amount,
            PlayEffect.RandomSplit => rule.Amount,
            _ => 0
        };
    }

    private PositionCard[] BoundedPool(IEnumerable<PositionCard> cards, int endpointCount = 5)
    {
        var pool = cards.ToArray();
        if (pool.Length <= endpointCount * 2) return pool;
        return pool.OrderBy(ReachHeuristic).Take(endpointCount)
            .Concat(pool.OrderByDescending(ReachHeuristic).Take(endpointCount))
            .DistinctBy(card => card.InstanceId).ToArray();
    }

    private LineState DrawTriggers(LineState state, PlayerSide side, int draws)
    {
        if (draws <= 0) return state;
        foreach (var snowdrop in Board(state.Position, side).Where(card =>
                     Rule(card.CardId)?.Reaction == "draw-boost" && card.Statuses?.Contains(CardStatus.Locked) != true).ToArray())
            state = Boost(state.Note($"Snowdrop reacts to {draws} draw{(draws == 1 ? "" : "s")}"), snowdrop.InstanceId, 2 * draws);
        return state;
    }

    private IEnumerable<LineState> DiscardDamage(LineState state, PlayerSide side, int discards, int depth)
    {
        IEnumerable<LineState> states = [state];
        for (var index = 0; index < discards; index++)
        foreach (var coral in Board(state.Position, side).Where(card => Rule(card.CardId)?.Reaction == "discard-damage" &&
                     card.Statuses?.Contains(CardStatus.Locked) != true).Select(card => card.InstanceId).ToArray())
        {
            var listener = coral;
            states = Bound(states.SelectMany(line =>
            {
                if (Locate(line.Position, listener)?.Zone != CardZone.Board) return [line];
                var enemies = Board(line.Position, Other(side)).Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                return enemies.Length == 0 ? [line] : SelectOptions(listener, "Coral random target", enemies,
                    card => card.InstanceId, Selection(listener)?.RandomTargetId).SelectMany(target =>
                        Damage((line with { Random = line.Random || enemies.Length > 1 }).Note("Coral discard damage"),
                            target.InstanceId, 2, depth));
            })).ToArray();
        }
        return states;
    }

    private LineState MoveToGrave(LineState state, PositionCard card, PlayerSide side)
    {
        var position = Remove(state.Position, card.InstanceId);
        var grave = position.Zone(side, CardZone.Graveyard);
        var discarded = card with { Power = card.BasePower, Armor = 0, Statuses = ImmutableHashSet<CardStatus>.Empty,
            StatusTurns = null, Charges = 0, Cooldown = 0 };
        return (state with { Position = Set(position, grave, grave.Cards.Add(discarded)) })
            .Note("Discard " + (Rule(card.CardId)?.Card.Name ?? card.CardId));
    }

    private IEnumerable<LineState> DiscardSummon(LineState state, PositionCard card, PlayerSide side, int depth)
    {
        var discarded = MoveToGrave(state, card, side);
        var name = Rule(card.CardId)?.Card.Name;
        if (name == "Tuirseach Skirmisher") return Summon(discarded, card.InstanceId, side, BoardRow.Melee, depth);
        if (name == "Morkvarg") return Summon(discarded, card.InstanceId, side, BoardRow.Melee, depth)
            .Select(line => Locate(line.Position, card.InstanceId)?.Zone == CardZone.Board
                ? Status(line, card.InstanceId, CardStatus.Doomed) : line);
        return [discarded];
    }

    private IEnumerable<LineState> UnseenElderEndTurn(LineState state, string sourceId, PlayerSide side, int depth)
    {
        var enemies = Board(state.Position, Other(side)).Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit &&
            card.Statuses?.Contains(CardStatus.Bleeding) != true).ToArray();
        IEnumerable<LineState> outputs = enemies.Length == 0 ? [state] : SelectOptions(sourceId, "Unseen Elder Bleeding target",
            enemies, card => card.InstanceId, Selection(sourceId)?.RandomTargetId).Select(target =>
                Duration((state with { Random = state.Random || enemies.Length > 1 }).Note("Unseen Elder gives Bleeding 2"),
                    target.InstanceId, CardStatus.Bleeding, 2));
        var devotion = (side == PlayerSide.User ? state.Position.User : state.Position.Opponent).Devotion;
        if (devotion == false) return outputs;
        return outputs.SelectMany(line => Sequential([devotion is null
                ? Approximate(line, "Unseen Elder Devotion assumed from the inferred faction deck") : line],
            Board(line.Position, Other(side)).Where(card => card.Statuses?.Contains(CardStatus.Bleeding) == true)
                .Select(card => card.InstanceId), (current, id) => TickBleeding(current, id, depth)));
    }

    private IEnumerable<LineState> TickBleeding(LineState state, string id, int depth)
    {
        var card = Find(state.Position, id);
        if (card?.Statuses?.Contains(CardStatus.Bleeding) != true) return [state];
        if (card.StatusTurns is null || !card.StatusTurns.TryGetValue(CardStatus.Bleeding, out var turns) || turns <= 0)
            return [state.Unknown("Bleeding duration unread")];
        return Damage(state.Note("Bleeding tick"), id, 1, depth, true).Select(line =>
        {
            var current = Find(line.Position, id);
            if (current is null || Locate(line.Position, id)?.Zone != CardZone.Board) return line;
            return line with { Position = Change(line.Position, current with
            {
                StatusTurns = current.StatusTurns!.SetItem(CardStatus.Bleeding, turns - 1),
                Statuses = turns == 1 ? current.Statuses!.Remove(CardStatus.Bleeding) : current.Statuses
            }) };
        });
    }

    private IEnumerable<LineState> PatienceDamage(LineState state, string sourceId, PlayerSide side, int depth)
    {
        var source = Find(state.Position, sourceId);
        if (source is null || source.Charges <= 0) return [state];
        var enemies = Board(state.Position, Other(side)).Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).ToArray();
        return enemies.Length == 0 ? [state] : SelectOptions(sourceId, "patience random target", enemies,
            card => card.InstanceId, Selection(sourceId)?.RandomTargetId).SelectMany(target =>
                Damage((state with { Random = state.Random || enemies.Length > 1 }).Note(
                    Rule(source.CardId)?.Card.Name + " unused Order engine"), target.InstanceId, 1, depth));
    }

    private IEnumerable<LineState> ReachClosureEffect(LineState state, PositionCard played, PlayRule rule,
        PlayerSide side, BoardRow row, GamePosition before, int depth)
    {
        if (rule.Effect == PlayEffect.BattleStations)
        {
            var hand = state.Position.Zone(side, CardZone.Hand);
            var handPool = hand.Cards.Where(card => Rule(card.CardId)?.Card.IsGold == false).ToArray();
            var inferred = false;
            var pool = handPool;
            if (pool.Length < 2)
            {
                inferred = true;
                pool = handPool.Concat(state.Position.Zone(side, CardZone.Deck).Cards
                    .Where(card => Rule(card.CardId)?.Card.IsGold == false)).DistinctBy(card => card.InstanceId).ToArray();
            }
            pool = BoundedPool(pool);
            var count = Math.Min(2, pool.Length);
            if (count == 0) return [Approximate(state, "Battle Stations inferred pool has no known bronze target")];
            IEnumerable<PositionCard[]> sequences = count == 1
                ? pool.Select(card => new[] { card })
                : pool.SelectMany(first => pool.Where(second => second.InstanceId != first.InstanceId)
                    .Select(second => new[] { first, second }));
            return Bound(sequences.SelectMany(sequence =>
            {
                IEnumerable<LineState> lines = [inferred ? Approximate(state,
                    "Battle Stations floor/ceiling uses bronze identities from the inferred draw-pile/hand pool") : state];
                foreach (var card in sequence)
                    lines = Bound(lines.SelectMany(line => Locate(line.Position, card.InstanceId)?.Zone is CardZone.Hand or CardZone.Deck
                        ? Play(line.Note("Battle Stations plays " + Rule(card.CardId)?.Card.Name), card.InstanceId, side, depth,
                            explicitSequenceStep: true) : [line])).ToArray();
                return lines.Select(line => DrawTriggers(line, side, sequence.Length));
            }));
        }

        if (rule.Effect == PlayEffect.Abordage)
        {
            var enemies = Board(state.Position, Other(side)).Where(card => CanTarget(state.Position, card, side, TargetSide.Enemy)).ToArray();
            if (enemies.Length == 0) return [state];
            var bloodthirst = Bloodthirst(before, side, 2);
            return Bound(SelectOptions(played.InstanceId, "Abordage target", enemies, card => card.InstanceId,
                Selection(played.InstanceId)?.TargetId).SelectMany(target =>
            {
                var damaged = Damage(state.Note("Abordage damage"), target.InstanceId, RaidDamage(state, side, rule, 2), depth);
                if (bloodthirst == false) return damaged;
                return damaged.SelectMany(line =>
                {
                    var hand = line.Position.Zone(side, CardZone.Hand);
                    var pool = hand.Cards.Where(card => Rule(card.CardId)?.Card.HasCategory("Pirate") == true).ToArray();
                    var inferred = false;
                    if (pool.Length == 0)
                    {
                        inferred = true;
                        pool = line.Position.Zone(side, CardZone.Deck).Cards.Where(card =>
                            Rule(card.CardId)?.Card.HasCategory("Pirate") == true).ToArray();
                    }
                    if (pool.Length == 0) return [bloodthirst is null
                        ? Approximate(line, "Abordage Bloodthirst and Pirate hand identity are uncertain")
                        : line.Note("Abordage Bloodthirst: no inferred Pirate target")];
                    return Bound(BoundedPool(pool).SelectMany(pirate => Play((inferred || bloodthirst is null)
                            ? Approximate(line, "Abordage Pirate uses the probable hand/draw-pile pool") : line,
                        pirate.InstanceId, side, depth, explicitSequenceStep: true)
                        .Select(output => DrawTriggers(output, side, 1))));
                });
            }));
        }

        if (rule.Effect == PlayEffect.LippyReach)
        {
            IEnumerable<LineState> outputs = [Approximate(state,
                "Lippy reach counts graveyard Roach/Knickers returns; the full deck/graveyard swap is intentionally omitted")];
            foreach (var name in new[] { "Roach", "Knickers" })
            {
                var ids = state.Position.Zone(side, CardZone.Graveyard).Cards.Where(card =>
                    Rule(card.CardId)?.Card.Name == name).Select(card => card.InstanceId).ToArray();
                foreach (var id in ids) outputs = Bound(outputs.SelectMany(line => SummonRows(line, id, side, depth, true))).ToArray();
            }
            return outputs;
        }

        if (rule.Effect == PlayEffect.Birna)
        {
            var deck = state.Position.Zone(side, CardZone.Deck);
            var draws = Math.Min(2, KnownCount(deck));
            var triggered = DrawTriggers(state, side, draws);
            var hand = triggered.Position.Zone(side, CardZone.Hand);
            var summonable = hand.Cards.Where(card => Rule(card.CardId)?.Card.Name is "Tuirseach Skirmisher" or "Morkvarg")
                .OrderByDescending(card => card.Power ?? card.BasePower ?? 0).Take(draws).ToArray();
            IEnumerable<LineState> baseline = DiscardDamage(triggered, side, draws, depth);
            if (summonable.Length == 0) return hand.Complete ? baseline : baseline.Select(line =>
                Approximate(line, "Birna hidden discards may include a graveyard-summoning unit"));
            IEnumerable<LineState> payoff = [triggered];
            foreach (var card in summonable)
                payoff = Bound(payoff.SelectMany(line => DiscardSummon(line, card, side, depth))).ToArray();
            payoff = payoff.SelectMany(line => DiscardDamage(line, side, draws, depth));
            return hand.Complete ? payoff : baseline.Concat(payoff.Select(line => Approximate(line,
                "Birna range includes known summoning discards; other hand identities remain hidden")));
        }

        if (rule.Effect == PlayEffect.Erland)
        {
            var deck = state.Position.Zone(side, CardZone.Deck);
            var units = deck.Cards.Count(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit);
            var count = deck.Complete ? units : Math.Max(units, deck.TotalCount ?? units);
            var line = AddStored(state, side, played.CardId, "reach-carryover", count)
                .Note($"Erland banks +{count} deck carryover");
            return deck.Complete ? [line] : [Approximate(line, "Erland assumes each unresolved inferred draw-pile slot is a boostable unit")];
        }

        if (rule.Effect == PlayEffect.SelfDamage)
            return Damage(state.Note(rule.Card.Name + " damages self"), played.InstanceId, rule.Amount, depth);

        if (rule.Effect == PlayEffect.GeraltProfessional)
        {
            var enemies = Board(state.Position, Other(side)).Where(card => CanTarget(state.Position, card, side, TargetSide.Enemy)).ToArray();
            var targets = BoundedPool(enemies, 3).Take(3).ToArray();
            IEnumerable<LineState> outputs = [state];
            foreach (var target in targets)
                outputs = outputs.SelectMany(line => Damage(line.Note("Geralt: Professional deploy ping"), target.InstanceId, 1, depth)).ToArray();
            return outputs.Select(line =>
            {
                var anotherWitcher = Board(before, side).Any(card => Rule(card.CardId)?.Card.HasCategory("Witcher") == true);
                var current = Find(line.Position, played.InstanceId);
                return anotherWitcher && current is not null
                    ? line with { Position = Change(line.Position, current with { Cooldown = 0 }) }
                    : line;
            });
        }

        if (rule.Effect == PlayEffect.ChampionCharge)
        {
            var targets = Board(state.Position).Where(card => CanTarget(state.Position, card, side, TargetSide.Any)).ToArray();
            if (targets.Length == 0) return [state];
            var destroy = Bloodthirst(before, side, 3);
            return Bound(SelectOptions(played.InstanceId, "Champion's Charge target", targets, card => card.InstanceId,
                Selection(played.InstanceId)?.TargetId).SelectMany(target => destroy switch
                {
                    true => Destroy(state.Note("Champion's Charge Bloodthirst 3"), target.InstanceId, false, depth),
                    false => Damage(state, target.InstanceId, RaidDamage(state, side, rule, 5), depth),
                    _ => Damage(Approximate(state, "Champion's Charge Bloodthirst is uncertain on a partial enemy board"),
                        target.InstanceId, RaidDamage(state, side, rule, 5), depth)
                        .Concat(Destroy(Approximate(state, "Champion's Charge Bloodthirst 3 branch"), target.InstanceId, false, depth))
                }));
        }

        if (rule.Effect == PlayEffect.CoupDeGrace)
        {
            var enemies = Board(state.Position, Other(side)).Where(card => CanTarget(state.Position, card, side, TargetSide.Enemy)).ToArray();
            return Bound(SelectOptions(played.InstanceId, "Coup target", enemies, card => card.InstanceId,
                Selection(played.InstanceId)?.TargetId).SelectMany(target =>
            {
                var conspiracy = target.Statuses?.Contains(CardStatus.Spying) == true;
                var definition = Rule(target.CardId)!.Card;
                return Damage(state.Note("Coup de Grace damage"), target.InstanceId, 3, depth).SelectMany(line =>
                    conspiracy || Locate(line.Position, target.InstanceId)?.Zone != CardZone.Board
                        ? SpawnAndPlay(line.Note(conspiracy ? "Coup Conspiracy replays target" : "Coup Deathblow replays target"),
                            definition.Name, side, depth, recursiveGeneratedChoice: true)
                        : [line]);
            }));
        }

        if (rule.Effect == PlayEffect.Sihil)
        {
            var amount = Math.Clamp(Stored(state.Position, side, "damage", played.CardId), 1, 3);
            var enemies = Board(state.Position, Other(side)).Where(card => CanTarget(state.Position, card, side, TargetSide.Enemy)).ToArray();
            return Bound(SelectOptions(played.InstanceId, "Sihil target", enemies, card => card.InstanceId,
                Selection(played.InstanceId)?.TargetId).SelectMany(target => Damage(state.Note($"Sihil damage {amount}"),
                    target.InstanceId, amount, depth).SelectMany(line =>
                {
                    if (Locate(line.Position, target.InstanceId)?.Zone == CardZone.Board) return [line];
                    // An absent ledger entry means printed Sihil damage 1, not zero. Persist the
                    // next damage explicitly so the first Initiative Deathblow advances 1 -> 2.
                    var grown = line with { Position = SetCardValue(line.Position,
                        new(side, played.CardId, "damage", Math.Min(3, amount + 1), Math.Min(3, amount + 1))) };
                    var pool = grown.Position.Zone(side, CardZone.Hand).Cards.Where(card =>
                        Rule(card.CardId)?.Card.IsGold == false).ToArray();
                    if (pool.Length == 0) pool = grown.Position.Zone(side, CardZone.Deck).Cards.Where(card =>
                        Rule(card.CardId)?.Card.IsGold == false).ToArray();
                    if (pool.Length == 0) return [Approximate(grown, "Sihil Deathblow bronze hand identity unavailable")];
                    return Bound(BoundedPool(pool).SelectMany(bronze => Play(Approximate(grown,
                        "Sihil Deathblow bronze uses the inferred hand/draw-pile pool"), bronze.InstanceId, side, depth,
                        explicitSequenceStep: true)));
                })));
        }

        if (rule.Effect == PlayEffect.HaraldGord)
        {
            var tracked = Stored(state.Position, side, "specials-played");
            var visible = state.Position.Zones.Where(zone => zone.Side == side && zone.Zone is CardZone.Graveyard or CardZone.Banished)
                .SelectMany(zone => zone.Cards).Count(card => Rule(card.CardId)?.Card.Kind == CardKind.Special);
            var amount = Math.Min(12, Math.Max(tracked, visible));
            return [Boost(state.Note($"Harald Gord: {amount} specials played"), played.InstanceId, amount)];
        }

        if (rule.Effect == PlayEffect.HighlandWarlord)
            return [AddStored(state, side, "203113", "raid-damage", 1).Note("Highland Warlord: Raid damage +1")];

        if (rule.Effect == PlayEffect.BountyBrute)
        {
            var placements = Stored(state.Position, side, "bounty-placements");
            var maximum = Stored(state.Position, side, "bounty-max-base-power");
            var profit = (side == PlayerSide.User ? state.Position.User : state.Position.Opponent).Coins is null
                ? AddStored(Approximate(state, "The Brute Profit uses reviewed Bounty placement history because Coins are unread"),
                    side, played.CardId, "transient-reach", placements)
                : GainCoins(state, side, placements);
            return [Boost(profit.Note($"The Brute: {placements} Bounty placements, max destroyed base {maximum}"),
                played.InstanceId, maximum)];
        }

        if (rule.Effect == PlayEffect.BountyIgnatius)
        {
            var total = Stored(state.Position, side, "bounty-total-base-power");
            var current = Find(state.Position, played.InstanceId);
            if (current is null) return [state];
            var power = Math.Min(current.BasePower ?? rule.Card.Power, 1 + total);
            var adjusted = state with { Position = Change(state.Position, current with { Power = power }) };
            return [adjusted.Note($"Ignatius Hale: 1 base plus {total} destroyed Bounty base power (capped at {current.BasePower})")];
        }

        if (rule.Effect == PlayEffect.DimunCaptain)
        {
            var current = Find(state.Position, played.InstanceId);
            if (current is null) return [state];
            var threshold = rule.Condition?.StartsWith("bloodthirst:", StringComparison.Ordinal) == true
                ? int.Parse(rule.Condition[12..]) : 2;
            var bloodthirst = Bloodthirst(before, side, threshold);
            if (bloodthirst == true)
                return [state with { Position = Change(state.Position, current with { Cooldown = 0 }) }];
            if (bloodthirst == false) return [state];
            return [state, Approximate(state with { Position = Change(state.Position, current with { Cooldown = 0 }) },
                $"{rule.Card.Name} Bloodthirst {threshold} is uncertain on the partial enemy board")];
        }

        if (rule.Effect == PlayEffect.BloodthirstDamage)
        {
            var threshold = int.Parse(rule.Condition![12..]);
            var bloodthirst = Bloodthirst(before, side, threshold);
            var low = rule.Amount; var high = int.Parse(rule.Argument!);
            var targets = Board(state.Position).Where(card => CanTarget(state.Position, card, side, TargetSide.Any)).ToArray();
            if (targets.Length == 0) return [state];
            return Bound(SelectOptions(played.InstanceId, "Bloodthirst damage target", targets, card => card.InstanceId,
                Selection(played.InstanceId)?.TargetId).SelectMany(target => bloodthirst switch
                {
                    true => Damage(state, target.InstanceId, RaidDamage(state, side, rule, high), depth),
                    false => Damage(state, target.InstanceId, RaidDamage(state, side, rule, low), depth),
                    _ => Damage(Approximate(state, $"{rule.Card.Name} Bloodthirst {threshold} is uncertain"),
                        target.InstanceId, RaidDamage(state, side, rule, low), depth)
                        .Concat(Damage(Approximate(state, $"{rule.Card.Name} Bloodthirst {threshold} branch"),
                            target.InstanceId, RaidDamage(state, side, rule, high), depth))
                }));
        }

        if (rule.Effect == PlayEffect.BloodEagle)
        {
            var enemies = Board(state.Position, Other(side)).Where(card => CanTarget(state.Position, card, side, TargetSide.Enemy)).ToArray();
            if (enemies.Length == 0) return [state];
            var bloodthirst = Bloodthirst(before, side, 3);
            return Bound(SelectOptions(played.InstanceId, "Blood Eagle target", enemies, card => card.InstanceId,
                Selection(played.InstanceId)?.TargetId).SelectMany(target =>
            {
                var damaged = Damage(state.Note("Blood Eagle damage"), target.InstanceId,
                    RaidDamage(state, side, rule, 2), depth);
                return damaged.SelectMany(line =>
                {
                    var deathblow = Locate(line.Position, target.InstanceId)?.Zone != CardZone.Board;
                    var broad = deathblow || bloodthirst != false;
                    var deck = line.Position.Zone(side, CardZone.Deck);
                    var pool = deck.Cards.Where(card => Rule(card.CardId)?.Card is { } definition &&
                        definition.HasCategory("Warrior") && (broad || definition.Provision <= 7)).ToArray();
                    if (pool.Length == 0) return [deck.Complete ? line.Note("Blood Eagle: no eligible Warrior") :
                        Approximate(line, "Blood Eagle inferred Warrior pool is empty/incomplete")];
                    return Bound(BoundedPool(pool).SelectMany(warrior => Play(bloodthirst is null && !deathblow
                            ? Approximate(line, "Blood Eagle Bloodthirst 3 is uncertain; bounded Warrior pool used") : line,
                        warrior.InstanceId, side, depth, explicitSequenceStep: true)));
                });
            }));
        }

        if (rule.Effect == PlayEffect.NovigradianJustice)
        {
            var controlled = Board(before, side).Any(card => Rule(card.CardId)?.Card is { } definition &&
                (definition.HasCategory("Dwarf") || definition.HasCategory("Crownsplitters")));
            var deck = state.Position.Zone(side, CardZone.Deck);
            var pool = deck.Cards.Where(card => Rule(card.CardId)?.Card is { IsGold: false } definition &&
                (definition.HasCategory("Dwarf") || definition.HasCategory("Crownsplitters"))).ToArray();
            if (pool.Length == 0) return [deck.Complete ? state.Note("Novigradian Justice: no eligible deck target") :
                Approximate(state, "Novigradian Justice inferred tutor pool is incomplete")];
            return Bound(BoundedPool(pool).SelectMany(target => Play(deck.Complete ? state :
                    Approximate(state, "Novigradian Justice uses the inferred Dwarf/Crownsplitter pool"),
                target.InstanceId, side, depth, explicitSequenceStep: true).SelectMany(line => controlled
                    ? Spawn(line.Note("Novigradian Justice spawns Cleaver's Muscle"),
                        _names.GetValueOrDefault("Cleaver's Muscle"), side, BoardRow.Melee, depth)
                    : [line])));
        }

        if (rule.Effect == PlayEffect.PhilippaBlindFury)
        {
            var initial = Board(state.Position, Other(side)).Where(card => CanTarget(state.Position, card, side, TargetSide.Enemy)).ToArray();
            if (initial.Length == 0) return [state];
            return Bound(SelectOptions(played.InstanceId, "Philippa: Blind Fury first target", initial,
                card => card.InstanceId, Selection(played.InstanceId)?.TargetId).SelectMany(target =>
            {
                IEnumerable<LineState> lines = Damage(state.Note("Philippa: Blind Fury 4"), target.InstanceId, 4, depth);
                foreach (var amount in new[] { 3, 2, 1 })
                    lines = Bound(lines.SelectMany(line =>
                    {
                        var targets = Board(line.Position, Other(side)).Where(card =>
                            CanTarget(line.Position, card, side, TargetSide.Enemy)).ToArray();
                        return targets.Length == 0 ? [line] : targets.SelectMany(random => Damage(
                            (line with { Random = line.Random || targets.Length > 1 }).Note($"Philippa random {amount}"),
                            random.InstanceId, amount, depth));
                    })).ToArray();
                return lines;
            }));
        }

        if (rule.Effect == PlayEffect.GeraltAard)
        {
            var enemies = Board(state.Position, Other(side)).Where(card => CanTarget(state.Position, card, side, TargetSide.Enemy)).ToArray();
            var count = Math.Min(3, enemies.Length);
            if (count == 0) return [state];
            var choices = count < 3 ? new[] { enemies } :
                (from first in Enumerable.Range(0, enemies.Length - 2)
                 from second in Enumerable.Range(first + 1, enemies.Length - first - 1)
                 from third in Enumerable.Range(second + 1, enemies.Length - second - 1)
                 select new[] { enemies[first], enemies[second], enemies[third] }).ToArray();
            return Bound(choices.SelectMany(choice =>
            {
                IEnumerable<LineState> lines = [state.Note("Geralt: Aard selects " + string.Join(", ", choice.Select(card => Rule(card.CardId)?.Card.Name)))];
                foreach (var target in choice)
                    lines = Bound(lines.SelectMany(line => Damage(line, target.InstanceId, 2, depth))).ToArray();
                foreach (var target in choice)
                    lines = Bound(lines.SelectMany(line => Locate(line.Position, target.InstanceId) is
                        { Zone: CardZone.Board, Row: BoardRow.Melee }
                        ? Move(line, target.InstanceId, depth, side) : [line])).ToArray();
                return lines;
            }));
        }

        return [state.Unknown("Unsupported reach closure")];
    }
}
