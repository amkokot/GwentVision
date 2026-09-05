using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed partial class TacticalPlayEngine
{
    private LineState Boost(LineState state, string id, int amount)
    {
        var card = Find(state.Position, id); if (card is null || Locate(state.Position, id)?.Zone != CardZone.Board) return state;
        if (Rule(card.CardId)?.PowerInvariant is "fixed" or "armor") return state;
        var boosted = state with { Position = Change(state.Position, card with { Power = card.Power + Math.Max(0, amount) }) };
        return Rule(card.CardId)?.Reaction == "false-ciri" && Find(boosted.Position, id)?.Power >= 8
            ? ResolveFalseCiriGrace(boosted, id) : boosted;
    }
    private LineState Heal(LineState state, string id, int amount)
    { var card = Find(state.Position, id); return card is null ? state : Boost(state, id, Math.Min(amount, Math.Max(0, (card.BasePower ?? 0) - (card.Power ?? 0)))); }
    private LineState Reset(LineState state, string id)
    {
        var card = Find(state.Position, id); if (card is null || Rule(card.CardId)?.PowerInvariant == "fixed") return state;
        var power = Rule(card.CardId)?.PowerInvariant == "armor" ? card.Armor : card.BasePower;
        return state with { Position = Change(state.Position, card with { Power = power }) };
    }
    private LineState SetPower(LineState state, string id, int amount)
    {
        var card = Find(state.Position, id); if (card is null || Rule(card.CardId)?.PowerInvariant is "fixed" or "armor") return state;
        return state with { Position = Change(state.Position, card with { Power = Math.Max(0, amount) }) };
    }
    private LineState BoostArmor(LineState state, string id, int amount)
    {
        state = Boost(state, id, amount); var card = Find(state.Position, id);
        return card is null || Rule(card.CardId)?.Card.HasCategory("Dwarf") != true ? state : state with { Position = Change(state.Position, card with { Armor = card.Armor + 2 }) };
    }
    private LineState Preparation(LineState state, string id)
    {
        var card = Find(state.Position, id); if (card is null) return state;
        state = Boost(state, id, Rule(card.CardId)?.Card.HasCategory("Soldier") == true ? 6 : 4);
        card = Find(state.Position, id)!; return state with { Position = Change(state.Position, card with { Armor = card.Armor + 2 }) };
    }
    private LineState Status(LineState state, string id, CardStatus status)
    {
        var card = Find(state.Position, id); if (card is null) return state;
        if (card.Statuses!.Contains(CardStatus.Veil)) return state;
        var changed = state with { Position = Change(state.Position, card with { Statuses = card.Statuses!.Add(status) }) };
        changed = ReceivedStatus(changed, id);
        if (status == CardStatus.Poison && !card.Statuses.Contains(CardStatus.Poison)) changed = PoisonCoinListeners(changed);
        if (status != CardStatus.Poison || Rule(card.CardId)?.Reaction != "mutant" || card.Statuses.Contains(CardStatus.Locked) ||
            Locate(changed.Position, id) is not { Zone: CardZone.Board, Row: { } row } location)
            return changed;
        if (card.Charges is null) return changed.Unknown("Mutant: Counter value unread");
        if (card.Charges <= 0) return changed;
        var target = changed.Position.Zone(location.Side, CardZone.Board, row);
        if (target.Cards.Length >= 9) return changed.Note("Mutant copy blocked by full row");
        var rule = Rule(card.CardId)!; var source = Find(changed.Position, id)!;
        var next = changed.NextId + 1; while (Find(changed.Position, "mutant-copy-" + next) is not null) next++;
        var spent = changed with { Position = Change(changed.Position, source with { Charges = source.Charges - 1 }), NextId = next };
        target = spent.Position.Zone(location.Side, CardZone.Board, row);
        var copy = new PositionCard("mutant-copy-" + next, card.CardId, rule.Card.Power, rule.Card.Power,
            rule.Card.PrintedArmor ?? 0, rule.PrintedStatuses, rule.InitialCharges, 0, false,
            ImmutableDictionary<CardStatus, int>.Empty);
        return (spent with { Position = Set(spent.Position, target, target.Cards.Add(copy)) }).Note("Mutant Poison Counter spawns base copy");
    }
    private IEnumerable<LineState> Damage(LineState state, string id, int amount, int depth, bool ignoreArmor = false)
    {
        var card = Find(state.Position, id); if (card is null || Locate(state.Position, id)?.Zone != CardZone.Board) return [state];
        var targetSide = Locate(state.Position, id)!.Side;
        var result = GwentRules.Damage(new(card.Power!.Value, card.BasePower!.Value, card.Armor!.Value, card.Statuses!), amount, ignoreArmor);
        var invariant = Rule(card.CardId)?.PowerInvariant;
        if (invariant == "fixed") result = result with { Unit = result.Unit with { Power = card.Power.Value }, Destroyed = false };
        else if (invariant == "armor") result = result with { Unit = result.Unit with { Power = result.Unit.Armor }, Destroyed = result.Unit.Armor <= 0 };
        if (card.Power >= card.BasePower && result.Unit.Power < card.BasePower)
        {
            var controller = Locate(state.Position, id)!.Side;
            var recipient = controller == PlayerSide.User ? state.Position.Opponent : state.Position.User;
            if (recipient.CurrentLeaderId == "200160")
            {
                var hand = state.Position.Zone(recipient.Side, CardZone.Hand);
                state = state with { Position = Set(state.Position, hand, hand.Cards.Select(item =>
                    Rule(item.CardId)?.Card is { } definition && (definition.HasCategory("Pirate") || definition.HasCategory("Ship")) ? item with { Armor = item.Armor + 1 } : item)) };
            }
        }
        var powerDamage = Math.Max(0, card.Power.Value - result.Unit.Power);
        if (result.Destroyed) return Destroy(ReactEnemyDamage(state, targetSide, powerDamage), id, false, depth);
        var changed = state with { Position = Change(state.Position, card with { Power = result.Unit.Power, Armor = result.Unit.Armor, Statuses = result.Unit.Statuses }) };
        changed = ReactEnemyDamage(changed, targetSide, powerDamage);
        if (powerDamage > 0 && Rule(card.CardId)?.Reaction == "shieldmaiden" && !card.Statuses!.Contains(CardStatus.Locked))
            return SummonAll((changed.Position.Zone(targetSide, CardZone.Deck).Complete ? changed :
                changed.Unknown("Shieldmaiden: deck copies not fully observed")).Note("Drummond Shieldmaiden damage summons deck copies"), targetSide, card.CardId,
                Locate(changed.Position, id)!.Row!.Value, depth).Select(output => TrackReaction(changed, output));
        return [changed];
    }

    private LineState PoisonCoinListeners(LineState state)
    {
        var origin = state;
        foreach (var listener in Board(state.Position).Where(card => Rule(card.CardId)?.Reaction == "roland-poison" &&
                     card.Statuses?.Contains(CardStatus.Locked) != true).ToArray())
        {
            var side = Locate(state.Position, listener.InstanceId)!.Side;
            var coins = (side == PlayerSide.User ? state.Position.User : state.Position.Opponent).Coins;
            state = coins is null ? state.Unknown("Roland: Coins unread") :
                GainCoins(state.Note("Roland gains 2 Coins on Poison application"), side, 2);
        }
        return TrackReaction(origin, state);
    }

    private IEnumerable<LineState> ApplyPoison(LineState state, string id, int depth)
    {
        var card = Find(state.Position, id);
        if (card is null || Locate(state.Position, id)?.Zone != CardZone.Board || card.Statuses!.Contains(CardStatus.Veil)) return [state];
        // The lethal second application still supplies a Poison event before removal.
        return card.Statuses.Contains(CardStatus.Poison) ? Destroy(PoisonCoinListeners(ReceivedStatus(state, id)), id, false, depth) :
            [Status(state, id, CardStatus.Poison)];
    }

    private LineState ReceivedStatus(LineState state, string id)
    {
        if (Locate(state.Position, id) is not { Zone: CardZone.Board } zone) return state;
        var origin = state;
        foreach (var dame in Board(state.Position, Other(zone.Side)).Where(card => Rule(card.CardId)?.Reaction == "thirsty-dame" &&
                     card.Statuses?.Contains(CardStatus.Locked) != true).ToArray()) state = Boost(state, dame.InstanceId, 1);
        return TrackReaction(origin, state);
    }

    private LineState TrackReaction(LineState origin, LineState result) => result with
    { ReactivePoints = origin.ReactivePoints + ReachSwing(origin.Position, result.Position, _reachActor) };

    private LineState ReactEnemyDamage(LineState state, PlayerSide damagedSide, int powerDamage)
    {
        if (powerDamage <= 0) return state;
        var origin = state;
        var owner = Other(damagedSide);
        foreach (var greatsword in Board(state.Position, owner).Where(card => Rule(card.CardId)?.Reaction == "greatsword" &&
                     card.Statuses?.Contains(CardStatus.Locked) != true).ToArray())
            state = Heal(state.Note("An Craite Greatsword reacts to enemy damage"), greatsword.InstanceId, 1);
        return TrackReaction(origin, state);
    }
    private IEnumerable<LineState> Consume(LineState state, string eater, string target, int depth)
    {
        var card = Find(state.Position, target); if (card is null || Locate(state.Position, eater)?.Zone != CardZone.Board) return [state];
        if (Find(state.Position, eater) is { } consumer && Rule(consumer.CardId)?.Deathwish == "consumed-copy")
        {
            var owner = Locate(state.Position, eater)!.Side;
            state = state with { Position = SetCardValue(state.Position,
                new(owner, consumer.CardId, "consumed-" + eater, StoredCardId: card.CardId)) };
        }
        return Destroy(Boost(state, eater, card.Power ?? 0).Note("Consume " + Rule(card.CardId)?.Card.Name), target, false, depth);
    }
    private IEnumerable<LineState> Destroy(LineState state, string id, bool banish, int depth)
    {
        var zone = Locate(state.Position, id); var card = Find(state.Position, id);
        if (zone is null || card is null || zone.Zone != CardZone.Board) return [state];
        var removed = banish || card.Statuses!.Contains(CardStatus.Doomed);
        var neighbors = Adjacent(state.Position, card.InstanceId);
        var p = Remove(state.Position, id); var destination = p.Zone(zone.Side, removed ? CardZone.Banished : CardZone.Graveyard);
        p = Set(p, destination, destination.Cards.Add(card with { Power = card.BasePower, Armor = 0, Statuses = ImmutableHashSet<CardStatus>.Empty, StatusTurns = null, Charges = 0, Cooldown = 0, ExtraThrive = 0 }));
        state = (state with { Position = p }).Note((banish ? "Banish " : removed ? "Destroy (Doomed) " : "Destroy ") + Rule(card.CardId)?.Card.Name);
        if (banish || card.Statuses!.Contains(CardStatus.Locked)) return [state];
        if (Rule(card.CardId)?.Reaction == "grave-summon-doomed")
            return Summon(state.Note("Morkvarg returns from graveyard"), id, zone.Side, BoardRow.Melee, depth)
                .Select(line => Locate(line.Position, id)?.Zone == CardZone.Board ? Status(line, id, CardStatus.Doomed) : line);
        return Deathwish(state, card, zone.Side, zone.Row!.Value, depth + 1, neighbors);
    }
    private IEnumerable<LineState> Deathwish(LineState state, PositionCard card, PlayerSide side, BoardRow row, int depth, IReadOnlyList<string>? formerNeighbors = null)
    {
        if (depth > 12) return [state.Unknown("Deathwish chain limit")];
        var rule = Rule(card.CardId)!;
        if (rule.Unmodeled is not null) return [state.Unknown("Unmodeled death/removal reaction: " + rule.Card.Name)];
        switch (rule.Deathwish)
        {
            case "projection":
                return ProjectionSequence(state, card, side, row, state.Position, rule.Projection!.Deathwish, depth);
            case "random-damage":
                var randomEnemies = Board(state.Position, Other(side)).Where(unit => Rule(unit.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                // Random damage can hit Immune/Defender-protected units: it is not manual targeting.
                return randomEnemies.Length == 0 ? [state] : Bound(SelectOptions(card.InstanceId, "random outcome", randomEnemies,
                    unit => unit.InstanceId, Selection(card.InstanceId)?.RandomTargetId).SelectMany(unit =>
                        Damage((state with { Random = true }).Note("Random damage target #" + unit.InstanceId), unit.InstanceId, rule.Amount, depth)));
            case "adjacent2":
                foreach (var id in formerNeighbors ?? Adjacent(state.Position, card.InstanceId))
                    if (Find(state.Position, id) is { } adjacent && Rule(adjacent.CardId)?.Card.Kind == CardKind.Unit) state = Boost(state, id, 2);
                return [state];
            case "harpy": return Spawn(state, _names.GetValueOrDefault("Harpy"), side, row, depth);
            case "drones3":
                return Sequential([state], Enumerable.Range(0, 3).Select(index => index.ToString()),
                    (line, _) => Spawn(line, _names.GetValueOrDefault("Drone"), side, row, depth));
            case "consumed-copy":
                var consumedMemory = CardValue(state.Position, side, card.CardId, "consumed-" + card.InstanceId);
                if (consumedMemory is null) return [state.Unknown("Arachas Queen consumed identity is not in snapshot memory")];
                return consumedMemory.StoredCardId is not { } consumed ? [state.Note("Arachas Queen did not consume a unit")] :
                    Spawn(state.Note("Arachas Queen Deathwish copies " + Rule(consumed)?.Card.Name), consumed, side, row, depth);
            case "copies": return SummonAll(state, side, card.CardId, row, depth);
            case "rats": return Spawn(state, _names.GetValueOrDefault("Rat"), side, row, depth).SelectMany(s => Spawn(s, _names.GetValueOrDefault("Rat"), side, row, depth));
            case "manticore":
                var enemies = Board(state.Position, Other(side)).Where(item => Rule(item.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                return enemies.Length == 0 ? [state] : Bound(enemies.Where(item => item.Power == enemies.Min(unit => unit.Power))
                    .SelectMany(item => Destroy(state with { Random = true }, item.InstanceId, false, depth)));
            case "lowest4":
                var units = Board(state.Position, side).Where(item => Rule(item.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                return units.Length == 0 ? [state] : units.Where(item => item.Power == units.Min(unit => unit.Power)).Select(item => Boost(state with { Random = true }, item.InstanceId, 4));
            case "brewess":
                IEnumerable<LineState> states = [state];
                for (var i = 0; i < 2; i++) states = Bound(states.SelectMany(s =>
                {
                    var eligible = s.Position.Zone(side, CardZone.Deck).Cards.Where(item => Rule(item.CardId) is { Card.IsGold: false, Card.Kind: CardKind.Unit } candidate &&
                        (candidate.Deathwish is not null || candidate.Card.AbilityText?.Contains("Deathwish:") == true)).ToArray();
                    return eligible.Length == 0 ? [s] : eligible.SelectMany(item => Summon(s with { Random = true }, item.InstanceId, side, row, depth));
                })).ToArray();
                return states;
            case "golyat":
                var deck = state.Position.Zone(Other(side), CardZone.Deck).Cards.Where(item => Rule(item.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                return deck.Length == 0 ? [state] : deck.Where(item => item.Power == deck.Max(unit => unit.Power)).SelectMany(item => Summon(state with { Random = true }, item.InstanceId, Other(side), row, depth));
            default: return [state];
        }
    }
    private static LineState BanishOffBoard(LineState state, string id)
    {
        var zone = Locate(state.Position, id); var card = Find(state.Position, id); if (zone is null || card is null) return state;
        var p = Remove(state.Position, id); var to = p.Zone(zone.Side, CardZone.Banished);
        return state with { Position = Set(p, to, to.Cards.Add(card)) };
    }
    private IEnumerable<LineState> Summon(LineState state, string id, PlayerSide side, BoardRow row, int depth)
    {
        if (Locate(state.Position, id)?.Zone is not CardZone.Deck and not CardZone.Graveyard) return [state];
        if (state.Position.Zone(side, CardZone.Board, row).Cards.Length >= 9) return [state.Note("Summon blocked: full row")];
        return Enter(state, id, side, row, state.Position.Zone(side, CardZone.Board, row).Cards.Length, false, depth + 1);
    }
    private IEnumerable<LineState> SummonRows(LineState state, string id, PlayerSide side, int depth, bool random = false)
    {
        var rows = Enum.GetValues<BoardRow>().Where(row => state.Position.Zone(side, CardZone.Board, row).Cards.Length < 9).ToArray();
        return rows.Length == 0 ? [state.Note("Summon blocked: full board")] : Bound(rows.SelectMany(row => Summon(state with { Random = state.Random || random }, id, side, row, depth)));
    }
    private IEnumerable<LineState> SummonAll(LineState state, PlayerSide side, string cardId, BoardRow row, int depth) =>
        Sequential([state], state.Position.Zone(side, CardZone.Deck).Cards.Where(card => card.CardId == cardId).Select(card => card.InstanceId), (s, id) => Summon(s, id, side, row, depth));
    private IEnumerable<LineState> Spawn(LineState state, string? cardId, PlayerSide side, BoardRow row, int depth, int? power = null)
        => SpawnAt(state, cardId, side, row, state.Position.Zone(side, CardZone.Board, row).Cards.Length, depth, power);

    private IEnumerable<LineState> SpawnAt(LineState state, string? cardId, PlayerSide side, BoardRow row, int slot, int depth, int? power = null)
    {
        if (cardId is null || Rule(cardId) is not { } rule) return [state.Unknown("Missing spawned-card definition")];
        var zone = state.Position.Zone(side, CardZone.Board, row); if (zone.Cards.Length >= 9) return [state.Note("Spawn blocked: full row")];
        var next = state.NextId + 1; while (Find(state.Position, "generated-" + next) is not null) next++;
        var card = new PositionCard("generated-" + next, cardId, power ?? rule.Card.Power, rule.Card.Power, rule.Card.PrintedArmor ?? 0,
            rule.PrintedStatuses, rule.InitialCharges, rule.Zeal ? 0 : 1, false, ImmutableDictionary<CardStatus, int>.Empty);
        var result = (state with { Position = Set(state.Position, zone, zone.Cards.Insert(Math.Clamp(slot, 0, zone.Cards.Length), card)), NextId = next }).Note("Spawn " + rule.Card.Name);
        foreach (var status in card.Statuses ?? [])
        {
            result = ReceivedStatus(result, card.InstanceId);
            if (status == CardStatus.Poison) result = PoisonCoinListeners(result);
        }
        if (rule.Deathwish == "consumed-copy")
            result = result with { Position = SetCardValue(result.Position,
                new(side, card.CardId, "consumed-" + card.InstanceId)) };
        if (rule.Card.Name == "Wandering Treant") result = AucwennTreant(result, card.InstanceId, side);
        if (rule.Unmodeled is not null) result = result.Unknown("Unmodeled spawned ability: " + rule.Card.Name);
        // Spawning is not playing: no Deploy or play listeners.
        return [result];
    }
    private IEnumerable<LineState> Move(LineState state, string id, int depth, PlayerSide? actor = null)
    {
        var zone = Locate(state.Position, id); var card = Find(state.Position, id); if (zone?.Zone != CardZone.Board || card is null) return [state];
        var row = zone.Row == BoardRow.Melee ? BoardRow.Ranged : BoardRow.Melee;
        var target = state.Position.Zone(zone.Side, CardZone.Board, row); if (target.Cards.Length >= 9) return [state.Note("Move blocked: destination row full")];
        var p = Remove(state.Position, id); p = Set(p, target, target.Cards.Add(card));
        var moved = Sequential([state with { Position = p }], Board(p).Where(item => Rule(item.CardId)?.Reaction == "sentry" && !item.Statuses!.Contains(CardStatus.Locked)).Select(item => item.InstanceId), (s, listener) =>
        {
            var location = Locate(s.Position, listener);
            if (location?.Side == zone.Side && location.Row == BoardRow.Ranged) return [Boost(s, id, 1)];
            return location?.Side != zone.Side && location?.Row == BoardRow.Melee ? Damage(s, id, 1, depth) : [s];
        });
        if (actor is null || zone.Side == actor) return moved;
        var milvas = p.Zone(actor.Value, CardZone.Deck).Cards.Count(card => Rule(card.CardId)?.Reaction == "milva");
        return milvas == 0 ? moved : moved.Select(line => AddStored(Approximate(line,
            $"Milva movement interaction cached at +{2 * milvas}"), actor.Value, "203097", "transient-reach", 2 * milvas));
    }

    private IEnumerable<LineState> Reactions(LineState state, PositionCard played, PlayerSide side, BoardRow row, GamePosition before, int depth)
        => ReactionsCore(state, played, side, row, before, depth).Select(output => output with
        { ReactivePoints = state.ReactivePoints + ReachSwing(state.Position, output.Position, _reachActor) });

    private IEnumerable<LineState> ReactionsCore(LineState state, PositionCard played, PlayerSide side, BoardRow row, GamePosition before, int depth)
    {
        var definition = Rule(played.CardId)!.Card;
        state = ActiveScenarioPlayListeners(state, played, side, before);
        // Subscribe only existing cards interested in THIS event. No Soldier/Deathwish work without a listener.
        var listeners = Board(before).Where(card => Rule(card.CardId) is { } rule &&
            ((rule.Thrive + card.ExtraThrive > 0 || rule.Harmony > 0) && definition.Kind == CardKind.Unit ||
             rule.Reaction == "agitator" && definition.HasCategory("Dwarf") ||
             rule.Assimilate > 0 && played.Original != true ||
             rule.Intimidate > 0 && definition.HasCategory("Crime") ||
             rule.Reaction == "preacher" && definition.HasCategory("Alchemy") ||
             rule.Reaction == "whisperer" && definition.Kind == CardKind.Special ||
             rule.Reaction == "tactic-charge" && definition.HasCategory("Tactic") ||
             rule.Reaction == "order-charge" && Rule(played.CardId)?.Order is not null ||
             (rule.Reaction is "resupply" or "resupply-carro" or "resupply-frigate") && definition.HasCategory("Warfare") ||
             rule.Reaction == "beast-cooldown" && definition.HasCategory("Beast") ||
             rule.Reaction == "elf-cooldown" && definition.HasCategory("Elf") ||
             rule.Reaction == "emhyr" && definition.Kind == CardKind.Unit ||
             rule.Reaction == "seductress"))
            .Select(card => card.InstanceId).ToArray();
        state = listeners.Aggregate(state, (s, id) =>
        {
            var unit = Find(s.Position, id); if (unit is null || Locate(s.Position, id)?.Zone != CardZone.Board || unit.Statuses!.Contains(CardStatus.Locked)) return s;
            var rule = Rule(unit.CardId)!; var owner = Locate(s.Position, id)!.Side;
            if (owner != side)
            {
                if (rule.Reaction == "emhyr" && definition.Kind == CardKind.Unit)
                    return GrantSpying(s.Note("Emhyr marks the opponent's played unit"), played.InstanceId, owner);
                if (rule.Reaction == "seductress" && (definition.Kind == CardKind.Unit || Board(s.Position, owner).Count(other => other.CardId == unit.CardId) >= 2))
                    return Boost(s.Note(rule.Card.Name + " reacts +1"), id, 1);
                return s;
            }
            var amount = 0;
            if (definition.Kind == CardKind.Unit && (Find(s.Position, played.InstanceId)?.Power ?? played.Power) > unit.Power) amount += rule.Thrive + unit.ExtraThrive;
            if (rule.Reaction == "agitator" && definition.HasCategory("Dwarf")) s = AddArmor(s, played.InstanceId, 1);
            if (rule.Reaction == "tactic-charge" && definition.HasCategory("Tactic") ||
                rule.Reaction == "order-charge" && Rule(played.CardId)?.Order is not null)
                s = s with { Position = Change(s.Position, unit with { Charges = (unit.Charges ?? 0) + 1 }) };
            if ((rule.Reaction is "resupply" or "resupply-carro" or "resupply-frigate") && definition.HasCategory("Warfare") && unit.Cooldown is > 0)
                s = s with { Position = Change(s.Position, unit with { Cooldown = unit.Cooldown - 1 }) };
            if ((rule.Reaction == "beast-cooldown" && definition.HasCategory("Beast") ||
                 rule.Reaction == "elf-cooldown" && definition.HasCategory("Elf")) && unit.Cooldown is > 0)
                s = s with { Position = Change(s.Position, unit with { Cooldown = unit.Cooldown - 1 }) };
            if (definition.HasCategory("Crime")) amount += rule.Intimidate;
            if (definition.HasCategory("Alchemy") && rule.Reaction == "preacher")
                amount += Board(s.Position, side).Count(other => other.CardId == unit.CardId) >= 2 ? 2 : 1;
            if (rule.Assimilate > 0 && played.Original is null) s = s.Unknown("Original/generated origin needed for Assimilate");
            if (played.Original == false) amount += rule.Assimilate;
            if (rule.Harmony > 0 && definition.Kind == CardKind.Unit && definition.Faction == "Scoia'tael" && definition.Categories.Any(category =>
                !Board(before, side).Any(other => Rule(other.CardId)?.Card is { Faction: "Scoia'tael" } card && card.HasCategory(category)))) amount += rule.Harmony;
            return amount == 0 ? s : Boost(s.Note(rule.Card.Name + " reacts +" + amount), id, amount);
        });
        IEnumerable<LineState> states = [state];
        foreach (var koshchey in Board(before, side).Where(card => Rule(card.CardId)?.Reaction == "koshchey" &&
                     definition.Kind == CardKind.Unit && !card.Statuses!.Contains(CardStatus.Locked) && played.Power > card.Power).Select(card => card.InstanceId))
        {
            var listenerId = koshchey;
            states = states.SelectMany(s =>
            {
                var location = Locate(s.Position, listenerId); var hand = s.Position.Zone(side, CardZone.Hand);
                var count = hand.TotalCount ?? (hand.Complete ? hand.Cards.Length : (int?)null);
                if (location?.Zone != CardZone.Board) return [s];
                if (count is null) return [s.Unknown("Koshchey: hand count needed for Adrenaline 4")];
                var token = count <= 4 ? "Endrega Larva" : "Drone";
                return Spawn(s.Note("Koshchey Thrive spawns " + token), _names.GetValueOrDefault(token), side, location.Row!.Value, depth);
            }).ToArray();
        }
        foreach (var raffard in Board(before, side).Where(card => Rule(card.CardId)?.Reaction == "raffard-crew").Select(card => card.InstanceId))
        {
            var listenerId = raffard;
            states = states.SelectMany(s =>
            {
                if (Locate(s.Position, listenerId)?.Zone != CardZone.Board || Find(s.Position, listenerId)?.Statuses!.Contains(CardStatus.Locked) == true ||
                    !Adjacent(s.Position, listenerId).Contains(played.InstanceId) || !IsCrewed(s.Position, listenerId)) return [s];
                var enemies = Board(s.Position, Other(side)).Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                return enemies.Length == 0 ? [s] : SelectOptions(listenerId, "Crew random target", enemies, card => card.InstanceId,
                    Selection(listenerId)?.RandomTargetId).SelectMany(card => Damage((s with { Random = enemies.Length > 1 }).Note("Raffard Crew damage"), card.InstanceId, 2, depth));
            }).ToArray();
        }
        foreach (var whisperer in Board(before, side).Where(card => Rule(card.CardId)?.Reaction == "whisperer" &&
                     definition.Kind == CardKind.Special).Select(card => card.InstanceId))
        {
            var listenerId = whisperer;
            states = states.SelectMany(s =>
            {
                var listener = Find(s.Position, listenerId); var location = Locate(s.Position, listenerId);
                if (listener is null || location?.Zone != CardZone.Board || listener.Statuses!.Contains(CardStatus.Locked) || listener.Charges is not > 0)
                    return [s];
                var spent = s with { Position = Change(s.Position, listener with { Charges = listener.Charges - 1 }) };
                return Spawn(spent.Note("Whisperer special-card reaction"), listener.CardId, side, location.Row!.Value, depth);
            }).ToArray();
        }
        if (_projectionMode) states = states.SelectMany(line => ProjectionPlayListeners(line, played, side, before, depth)).ToArray();
        states = states.SelectMany(line => SapperBombReaction(line, played, side, before, depth)).ToArray();
        if (definition.HasCategory("Crime") && (side == PlayerSide.User ? state.Position.User : state.Position.Opponent).CurrentLeaderId == "122105")
            states = states.Select(s => GainCoins(s, side, 1));
        // Board listeners precede graveyard listeners such as Giant Toad.
        foreach (var tome in Board(before, side).Where(card => Rule(card.CardId)?.Reaction == "tome").Select(card => card.InstanceId))
        {
            var key = tome;
            if (definition.Kind != CardKind.Unit || definition.IsGold) continue;
            states = states.SelectMany(s =>
            {
                if (Locate(s.Position, key)?.Zone != CardZone.Board || Find(s.Position, key)?.Statuses?.Contains(CardStatus.Locked) == true) return [s];
                var copies = s.Position.Zone(side, CardZone.Graveyard).Cards.Where(card => card.CardId == played.CardId).ToArray();
                return copies.Length == 0 ? [s] : copies.SelectMany(card => Summon(s with { Random = true }, card.InstanceId, side, row, depth)
                    .Select(output => Locate(output.Position, card.InstanceId)?.Zone == CardZone.Board ? Status(output, card.InstanceId, CardStatus.Doomed) : output));
            }).ToArray();
        }
        foreach (var scenario in Board(before, side).Where(card => Rule(card.CardId)?.Reaction == "scenario" &&
                     ScenarioRules.Matches(Rule(card.CardId)!.Card, Rule(played.CardId)!)).Select(card => card.InstanceId))
        {
            var scenarioId = scenario;
            states = states.SelectMany(s => ScenarioChapter(s, scenarioId, side, depth)).ToArray();
        }
        if (definition.HasCategory("Siege Engine"))
        {
            foreach (var master in before.Zone(side, CardZone.Hand).Cards.Where(card => Rule(card.CardId)?.Reaction == "siege-master").ToArray())
            {
                var masterId = master.InstanceId;
                states = states.SelectMany(line =>
                {
                    var playedZone = Locate(line.Position, played.InstanceId);
                    if (playedZone?.Zone != CardZone.Board || Locate(line.Position, masterId)?.Zone != CardZone.Hand) return [line];
                    var slot = Array.FindIndex(playedZone.Cards.ToArray(), card => card.InstanceId == played.InstanceId);
                    return Enter(line.Note("Siege Master thins from hand"), masterId, side, playedZone.Row!.Value,
                        Math.Max(0, slot), false, depth).Select(output => DrawTriggers(output, side, 1));
                }).ToArray();
            }
        }
        var isNature = definition.HasCategory("Nature") || CardValue(state.Position, side, definition.Id, "aucwenn-nature") is not null;
        var symbiosis = !isNature ? 0 : Board(state.Position, side).Where(card => !card.Statuses!.Contains(CardStatus.Locked)).Sum(card => Rule(card.CardId)?.Symbiosis ?? 0) +
            ((side == PlayerSide.User ? state.Position.User : state.Position.Opponent).CurrentLeaderId == "200165" ? 1 : 0);
        if (isNature && symbiosis > 0)
        {
            states = states.SelectMany(s => SpawnRandomRow(s, "Wandering Treant", side, depth, symbiosis));
            foreach (var fledglingId in Board(before, side).Where(card => Rule(card.CardId)?.Reaction == "symbiosis-left" &&
                         !card.Statuses!.Contains(CardStatus.Locked)).Select(card => card.InstanceId))
            {
                states = states.Select(line =>
                {
                    var zone = Locate(line.Position, fledglingId);
                    if (zone?.Zone != CardZone.Board) return line;
                    var index = Array.FindIndex(zone.Cards.ToArray(), card => card.InstanceId == fledglingId);
                    if (index <= 0 || Rule(zone.Cards[index - 1].CardId)?.Card.Kind != CardKind.Unit) return line;
                    return Duration(line.Note("Naiad Fledgling gives left unit Vitality 2"), zone.Cards[index - 1].InstanceId,
                        CardStatus.Vitality, 2);
                }).ToArray();
            }
        }
        if (definition.HasCategory("Organic") && (side == PlayerSide.User ? state.Position.User : state.Position.Opponent).CurrentLeaderId == "201743")
            states = states.SelectMany(s => SpawnRandomRow(s, "Drone", side, depth));
        var automatic = state.Position.Zones.Where(zone => zone.Side == side && zone.Zone is CardZone.Deck or CardZone.Graveyard)
            .SelectMany(zone => zone.Cards.Where(card => Rule(card.CardId)?.Reaction is { } reaction && PlayConditions.Find(reaction) is { } condition &&
                PlayConditions.MatchesEvent(condition, TriggerMoment.Play, zone.Zone, Rule(played.CardId), row))).Select(card => card.InstanceId).ToArray();
        return Sequential(states, automatic, (s, id) =>
        {
            var card = Find(s.Position, id); var zone = Locate(s.Position, id); if (card is null) return [s];
            if (Rule(card.CardId)?.Reaction is { } key && PlayConditions.Find(key) is { } requirement)
            {
                var eligibility = PlayConditions.Evaluate(requirement, s.Position, side, Rule);
                if (eligibility == ConditionTruth.NotMet) return [s];
                if (eligibility == ConditionTruth.Unknown) return [s.Unknown(Rule(card.CardId)!.Card.Name + ": required condition unread (" + key + ")")];
            }
            switch (Rule(card.CardId)?.Reaction)
            {
                case "roach" when zone?.Zone == CardZone.Deck && definition.IsGold:
                    return SummonRows(s, id, side, depth, true);
                case "ronvid" when zone?.Zone == CardZone.Graveyard && definition.HasCategory("Soldier"):
                    return SummonRows(s, id, side, depth, true);
                case "brigade" when zone?.Zone == CardZone.Deck:
                    return SummonRows(s, id, side, depth, true);
                case "wyvern-shield" when zone?.Zone == CardZone.Graveyard && definition.Kind == CardKind.Unit:
                    return [BanishOffBoard(Status(s, played.InstanceId, CardStatus.Shield), id)];
                case "toad" when zone?.Zone == CardZone.Graveyard && row == BoardRow.Ranged && Rule(played.CardId)?.Deathwish is not null:
                    if (Locate(s.Position, played.InstanceId)?.Zone != CardZone.Board) return [s];
                    return Summon(s, id, side, row, depth).SelectMany(output => Locate(output.Position, id)?.Zone == CardZone.Board
                        ? Consume(output, id, played.InstanceId, depth).Select(line => Status(line, id, CardStatus.Doomed)) : [output]);
                default: return [s];
            }
        });
    }
    private IEnumerable<LineState> Orders(LineState state, PlayerSide side, int depth, IReadOnlySet<string>? eligible = null)
    {
        if (!_orderVisits.Add(PositionNotation.Write(state.Position) + "|" + side + "|" + string.Join('|', state.Missing.Order()))) yield break;
        if (depth > 16) { yield return state.Unknown("Order chain budget"); yield break; }
        var ready = Board(state.Position, side).Where(card => (eligible is null || eligible.Contains(card.InstanceId)) &&
            Rule(card.CardId)?.Order is not null && !card.Statuses!.Contains(CardStatus.Locked)).ToArray();
        var fees = Board(state.Position, side).Where(card => (eligible is null || eligible.Contains(card.InstanceId)) &&
            Rule(card.CardId) is { FeeCost: > 0, FeeAction: not null } && !card.Statuses!.Contains(CardStatus.Locked)).ToArray();
        foreach (var card in ready)
        {
            var rule = Rule(card.CardId)!;
            if (rule.Order == "shield-fee") continue; // Shield without a reacting engine has no same-turn points; no coin conversion assumed.
            if (card.Charges != 0 && (card.Charges is null || card.Cooldown is null)) state = state.Unknown(rule.Card.Name + ": Order readiness unread");
        }
        yield return state; // Spending existing Orders is optional.
        foreach (var card in ready.Where(card => card.Charges > 0 && card.Cooldown == 0))
        {
            foreach (var output in OrderOnce(state, card.InstanceId, side, depth).SelectMany(s => Orders(s, side, depth + 1, eligible))) yield return output;
            if (_truncated) yield break;
        }
        foreach (var card in fees)
        {
            var rule = Rule(card.CardId)!;
            var resources = side == PlayerSide.User ? state.Position.User : state.Position.Opponent;
            if (resources.Coins is null)
            {
                yield return state.Unknown(rule.Card.Name + ": Coin count needed for Fee maximum");
                continue;
            }
            if (resources.Coins < rule.FeeCost || rule.FeeCooldown > 0 && card.Cooldown != 0) continue;
            foreach (var output in FeeOnce(state, card.InstanceId, side, depth).SelectMany(s => Orders(s, side, depth + 1, eligible))) yield return output;
            if (_truncated) yield break;
        }
    }
    private IEnumerable<LineState> SpawnRandomRow(LineState state, string name, PlayerSide side, int depth, int? power = null)
    {
        var rows = Enum.GetValues<BoardRow>().Where(row => state.Position.Zone(side, CardZone.Board, row).Cards.Length < 9).ToArray();
        return rows.Length == 0 ? [state] : Bound(rows.SelectMany(row => Spawn(state with { Random = true }, _names.GetValueOrDefault(name), side, row, depth, power)));
    }
    private IEnumerable<LineState> EndTurn(LineState state, PlayerSide side, int depth)
        => _projectionMode ? ProjectionTimers(state, side, depth).SelectMany(line => EndTurnAfterTimers(line, side, depth)) :
            EndTurnAfterTimers(state, side, depth);

    private IEnumerable<LineState> EndTurnAfterTimers(LineState state, PlayerSide side, int depth)
    {
        var timed = Board(state.Position, side).Where(card => card.Statuses!.Contains(CardStatus.Bleeding) || card.Statuses.Contains(CardStatus.Vitality))
            .Select(card => card.InstanceId).ToArray();
        var states = Sequential([state], timed, (s, id) =>
        {
            var card = Find(s.Position, id); if (card is null || Locate(s.Position, id)?.Zone != CardZone.Board) return [s];
            var status = card.Statuses!.Contains(CardStatus.Bleeding) ? CardStatus.Bleeding : card.Statuses.Contains(CardStatus.Vitality) ? CardStatus.Vitality : (CardStatus?)null;
            if (status is null) return [s];
            if (card.StatusTurns is null || !card.StatusTurns.TryGetValue(status.Value, out var turns) || turns <= 0) return [s.Unknown("Unread bleeding/vitality duration")];
            IEnumerable<LineState> outputs = status == CardStatus.Bleeding ? Damage(s, id, 1, depth, true) : [Boost(s, id, 1)];
            return outputs.Select(output =>
            {
                var updated = Find(output.Position, id); if (updated is null || Locate(output.Position, id)?.Zone != CardZone.Board) return output;
                return output with { Position = Change(output.Position, updated with { StatusTurns = updated.StatusTurns!.SetItem(status.Value, turns - 1),
                    Statuses = turns == 1 ? updated.Statuses!.Remove(status.Value) : updated.Statuses }) };
            });
        });
        var automatic = Sequential(states, state.Position.Zones.Where(zone => zone.Side == side && zone.Zone is CardZone.Deck or CardZone.Graveyard or CardZone.Board)
            .SelectMany(zone => zone.Cards.Where(card => Rule(card.CardId)?.Reaction is { } reaction &&
                ((reaction is "unseen-elder" or "dunca" or "patience-damage-1" or "bloody-mistress" or "gernichora" or "poisoned2") && zone.Zone == CardZone.Board ||
                 PlayConditions.Find(reaction) is { } condition && PlayConditions.MatchesEvent(condition, TriggerMoment.EndTurn, zone.Zone))))
            .Select(card => card.InstanceId), (s, id) =>
        {
            var card = Find(s.Position, id); var zone = Locate(s.Position, id); if (card is null) return [s];
            if (zone?.Zone == CardZone.Board && card.Statuses!.Contains(CardStatus.Locked)) return [s];
            var reaction = Rule(card.CardId)?.Reaction;
            if (reaction == "unseen-elder" && zone?.Zone == CardZone.Board)
                return UnseenElderEndTurn(s, id, side, depth);
            if (reaction == "dunca" && zone?.Zone == CardZone.Board &&
                (Rule(card.CardId)?.RequiredRow is null || Rule(card.CardId)?.RequiredRow == zone.Row) &&
                (Rule(card.CardId)?.Order is null || card.Charges > 0))
                return [AddStored(s.Note("Dunca banks +1 hand carryover"), side, card.CardId, "reach-carryover", 1)];
            if (reaction == "patience-damage-1" && zone?.Zone == CardZone.Board)
                return PatienceDamage(s, id, side, depth);
            if (reaction is "bloody-mistress" or "gernichora" && zone?.Zone == CardZone.Board)
                return MistressEndTurn(s, id, side, depth);
            if (reaction == "poisoned2" && zone?.Zone == CardZone.Board)
                return [card.Statuses!.Contains(CardStatus.Poison) ? Boost(s, id, 2) : s];
            if (reaction == "tugo" && zone?.Zone == CardZone.Board)
            {
                var ownRows = s.Position.Zones.Where(z => z.Side == side && z.Zone == CardZone.Board).ToArray();
                if (ownRows.Any(z => !z.Complete || z.Cards.Any(c => c.Power is null))) return [s.Unknown("Tugo lowest-unit/Might board incomplete")];
                var units = ownRows.SelectMany(z => z.Cards).Where(c => Rule(c.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                var lowest = units.Where(c => c.Power == units.Min(u => u.Power)).ToArray();
                // Might: a unit with at least 10 power on EACH allied row (CDPR 11.7 keyword definition).
                var might = ownRows.All(z => z.Cards.Any(c => Rule(c.CardId)?.Card.Kind == CardKind.Unit && c.Power >= 10));
                if (might) { foreach (var unit in lowest) s = Boost(s, unit.InstanceId, 1); return [s]; }
                return SelectOptions(id, "random lowest boost", lowest, c => c.InstanceId, Selection(id)?.RandomTargetId)
                    .Select(unit => Boost(s with { Random = s.Random || lowest.Length > 1 }, unit.InstanceId, 1));
            }
            if (reaction is { } key && PlayConditions.Find(key) is { } requirement)
            {
                var eligibility = PlayConditions.Evaluate(requirement, s.Position, side, Rule);
                if (eligibility == ConditionTruth.NotMet) return [s];
                if (eligibility == ConditionTruth.Unknown) return [s.Unknown(Rule(card.CardId)!.Card.Name + ": required condition unread (" + key + ")")];
            }
            if (reaction == "aelirenn" && zone?.Zone == CardZone.Deck && Board(s.Position, side).Count(item => Rule(item.CardId)?.Card.HasCategory("Elf") == true) >= 5)
                return Summon(s, id, side, BoardRow.Melee, depth);
            if (reaction == "portal-timer" && zone?.Zone == CardZone.Board && card.Charges is > 0 && card.Cooldown is > 0)
            {
                var reduced = card with { Cooldown = card.Cooldown - 1 };
                var ticked = s with { Position = Change(s.Position, reduced) };
                if (reduced.Cooldown > 0) return [ticked];
                var deck = ticked.Position.Zone(side, CardZone.Deck);
                var choices = deck.Cards.Where(item => Rule(item.CardId)?.Card is { Kind: CardKind.Unit, Provision: 4 }).ToArray();
                var exhausted = ticked with { Position = Change(ticked.Position, reduced with { Charges = 0 }) };
                if (choices.Length == 0) return [deck.Complete ? exhausted.Note("Portal timer: no eligible target") : exhausted.Unknown("Portal timer: unknown 4-provision target")];
                return Bound(choices.SelectMany(target => Summon((exhausted with { Random = true }).Note("Portal timer summon " + Rule(target.CardId)?.Card.Name),
                    target.InstanceId, side, zone.Row!.Value, depth)));
            }
            if (reaction is "hound" or "warcrier" or "peaches" && zone?.Zone == CardZone.Board) return [Boost(s, id, 1)];
            if (reaction == "hamadryad" && zone?.Zone == CardZone.Board && card.Statuses!.Contains(CardStatus.Vitality))
                return [Boost(s, id, 1)];
            if (reaction == "frog" && zone?.Zone == CardZone.Board)
            {
                var adjacent = Adjacent(s.Position, id).ToArray();
                return [adjacent.Aggregate(s, (line, targetId) => TriggerVitality(line, targetId))];
            }
            if (reaction == "redanian" && zone?.Zone is CardZone.Deck or CardZone.Graveyard)
            {
                var resources = side == PlayerSide.User ? s.Position.User : s.Position.Opponent;
                if (resources.Coins is null) return [s.Unknown("Coin count needed for Flying Redanian")];
                if (resources.Coins >= (resources.CurrentLeaderId == "202577" ? 7 : 9)) return SummonRows(s, id, side, depth, true);
            }
            if (reaction == "winter-queen" && zone?.Zone == CardZone.Deck)
            {
                var weather = WeatherOnBothEnemyRows(s.Position, side, ["Frost"]);
                if (weather == ConditionTruth.Unknown) return [s.Unknown("Winter Queen: enemy-row Frost state unread")];
                if (weather == ConditionTruth.Met) return Summon(s, id, side, BoardRow.Ranged, depth);
            }
            if (reaction == "anglerfish")
            {
                var both = WeatherOnBothEnemyRows(s.Position, side, ["Rain", "Storm"]);
                if (both == ConditionTruth.Unknown) return [s.Unknown("Anglerfish: enemy-row Rain/Storm state unread")];
                if (zone?.Zone == CardZone.Deck && both == ConditionTruth.Met) return Summon(s, id, side, BoardRow.Ranged, depth);
                if (zone?.Zone == CardZone.Board)
                {
                    var either = WeatherOnAnyEnemyRow(s.Position, side, ["Rain", "Storm"]);
                    if (either == ConditionTruth.Unknown) return [s.Unknown("Anglerfish: enemy-row Rain/Storm state unread")];
                    if (either == ConditionTruth.NotMet) return [MoveToBottomDeck(s, id, side).Note("Anglerfish returned to the bottom of the deck")];
                }
            }
            if (reaction == "drummond-berserker" && zone?.Zone == CardZone.Board)
            {
                var enemies = Board(s.Position, Other(side)).Where(target => Rule(target.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                IEnumerable<LineState> damagedSelf = Damage(s, id, 1, depth);
                damagedSelf = damagedSelf.Select(line => Find(line.Position, id) is { Power: <= 3 }
                    ? Transform(line, id, "Bear Abomination").Note("Drummond Berserker reached Berserk 3") : line);
                if (enemies.Length == 0) return damagedSelf;
                return Bound(damagedSelf.SelectMany(line => SelectOptions(id, "random enemy damage", enemies.Where(target => Find(line.Position, target.InstanceId) is not null),
                    target => target.InstanceId, Selection(id)?.RandomTargetId).SelectMany(target => Damage(line with { Random = true }, target.InstanceId, 1, depth))));
            }
            return [s];
        });
        if (_projectionMode) automatic = automatic.SelectMany(line => ProjectionAutomatic(line, side, depth));
        return automatic.SelectMany(line => SpecialEndTurn(line, side, depth))
            .Select(line => ActiveScenarioEndTurn(GrowAerondight(TickOrderCooldowns(line, side), side), side));
    }

    private LineState TriggerVitality(LineState state, string id)
    {
        var card = Find(state.Position, id);
        if (card is null || Locate(state.Position, id)?.Zone != CardZone.Board ||
            card.Statuses?.Contains(CardStatus.Vitality) != true) return state;
        if (card.StatusTurns is null || !card.StatusTurns.TryGetValue(CardStatus.Vitality, out var turns) || turns <= 0)
            return state.Unknown("Frog adjacent Vitality duration unread");
        state = Boost(state.Note("Frog triggers adjacent Vitality"), id, 1);
        card = Find(state.Position, id)!;
        return state with { Position = Change(state.Position, card with
        {
            StatusTurns = card.StatusTurns!.SetItem(CardStatus.Vitality, turns - 1),
            Statuses = turns == 1 ? card.Statuses!.Remove(CardStatus.Vitality) : card.Statuses
        }) };
    }
    private LineState Transform(LineState state, string id, string name)
    {
        var card = Find(state.Position, id); var targetId = _names.GetValueOrDefault(name); var target = targetId is null ? null : Rule(targetId);
        if (card is null || target is null) return state.Unknown("Unknown transform target: " + name);
        return state with { Position = Change(state.Position, card with
        {
            CardId = target.Card.Id, Power = target.Card.Power, BasePower = target.Card.Power,
            Armor = target.Card.PrintedArmor ?? 0, Statuses = target.PrintedStatuses,
            StatusTurns = ImmutableDictionary<CardStatus, int>.Empty, Charges = target.InitialCharges,
            Cooldown = target.Zeal ? 0 : target.Order is null ? 0 : 1, ExtraThrive = 0
        }) };
    }
    private static ConditionTruth WeatherOnBothEnemyRows(GamePosition position, PlayerSide owner, string[] names)
    {
        if (position.RowEffects is null) return position.RowEffectsKnownInactive ? ConditionTruth.NotMet : ConditionTruth.Unknown;
        var enemy = Other(owner);
        return Enum.GetValues<BoardRow>().All(row => position.RowEffects.Value.Any(effect => effect.AffectedSide == enemy && effect.Row == row &&
            names.Any(name => name.Equals(effect.Name, StringComparison.OrdinalIgnoreCase)))) ? ConditionTruth.Met : ConditionTruth.NotMet;
    }
    private static ConditionTruth WeatherOnAnyEnemyRow(GamePosition position, PlayerSide owner, string[] names)
    {
        if (position.RowEffects is null) return position.RowEffectsKnownInactive ? ConditionTruth.NotMet : ConditionTruth.Unknown;
        var enemy = Other(owner);
        return position.RowEffects.Value.Any(effect => effect.AffectedSide == enemy && names.Any(name => name.Equals(effect.Name, StringComparison.OrdinalIgnoreCase)))
            ? ConditionTruth.Met : ConditionTruth.NotMet;
    }
    private static LineState MoveToBottomDeck(LineState state, string id, PlayerSide side)
    {
        var card = Find(state.Position, id); var source = Locate(state.Position, id);
        if (card is null || source?.Zone != CardZone.Board) return state;
        var without = Set(state.Position, source, source.Cards.Where(item => item.InstanceId != id));
        var deck = without.Zone(side, CardZone.Deck);
        return state with { Position = Set(without, deck, deck.Cards.Append(card)) };
    }
}
