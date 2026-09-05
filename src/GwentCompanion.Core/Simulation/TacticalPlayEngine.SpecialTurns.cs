using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed partial class TacticalPlayEngine
{
    private bool IsSoldierCrewed(GamePosition position, string id)
    {
        var adjacent = Adjacent(position, id).Select(cardId => Find(position, cardId)).Where(card => card is not null).ToArray();
        return adjacent.Length == 2 && adjacent.All(card => Rule(card!.CardId)?.Card.HasCategory("Soldier") == true);
    }

    private LineState ResolveFalseCiriGrace(LineState state, string id)
    {
        var source = Locate(state.Position, id); var card = Find(state.Position, id);
        if (source?.Zone != CardZone.Board || source.Row is null || card is null || card.Power < 8) return state;
        var target = state.Position.Zone(Other(source.Side), CardZone.Board, source.Row);
        if (target.Cards.Length >= 9) return state.Note("False Ciri Grace blocked by a full opposite row");
        var p = Remove(state.Position, id);
        card = card with { Statuses = ImmutableHashSet<CardStatus>.Empty, StatusTurns = ImmutableDictionary<CardStatus, int>.Empty };
        return (state with { Position = Set(p, target, target.Cards.Add(card)) }).Note("False Ciri Grace moves and Purifies");
    }

    private IEnumerable<LineState> SpecialEndTurn(LineState state, PlayerSide side, int depth)
    {
        IEnumerable<LineState> states = [state];
        foreach (var id in Board(state.Position, side).Where(card => Rule(card.CardId)?.Reaction == "false-ciri" &&
                     !card.Statuses!.Contains(CardStatus.Locked)).Select(card => card.InstanceId).ToArray())
            states = states.Select(line => Boost(line.Note("False Ciri end-turn boost"), id, 1)).ToArray();

        foreach (var id in Board(state.Position, side).Where(card => Rule(card.CardId)?.Reaction == "resupply-carro" &&
                     !card.Statuses!.Contains(CardStatus.Locked)).Select(card => card.InstanceId).ToArray())
            states = states.Select(line =>
            {
                var card = Find(line.Position, id);
                return card is not null && IsSoldierCrewed(line.Position, id)
                    ? AddArmor(line.Note("Carroballista Crew Armor"), id, 1) : line;
            }).ToArray();

        foreach (var id in Board(state.Position, side).Where(card => Rule(card.CardId)?.Reaction == "griffin-witcher" &&
                     !card.Statuses!.Contains(CardStatus.Locked)).Select(card => card.InstanceId).ToArray())
        {
            states = states.SelectMany(line =>
            {
                var hand = line.Position.Zone(side, CardZone.Hand); var count = hand.TotalCount ?? (hand.Complete ? hand.Cards.Length : (int?)null);
                if (count is null) return [line.Unknown("Griffin Witcher: hand count needed for Adrenaline 3")];
                if (count > 3) return [line];
                var self = Find(line.Position, id); if (self is null) return [line];
                var locked = Status(line.Note("Griffin Witcher Adrenaline"), id, CardStatus.Locked);
                var enemies = Board(locked.Position, Other(side)).Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                return enemies.Length == 0 ? [locked] : SelectOptions(id, "random target", enemies, card => card.InstanceId,
                    Selection(id)?.RandomTargetId).SelectMany(card => Damage(locked with { Random = enemies.Length > 1 }, card.InstanceId, 3, depth));
            }).ToArray();
        }

        foreach (var id in Board(state.Position, side).Where(card => Rule(card.CardId)?.Reaction == "cat-witcher" &&
                     !card.Statuses!.Contains(CardStatus.Locked)).Select(card => card.InstanceId).ToArray())
        {
            states = states.SelectMany(line => Move(line.Note("Cat Witcher end-turn move"), id, depth, side).SelectMany(moved =>
            {
                var location = Locate(moved.Position, id); var hand = moved.Position.Zone(side, CardZone.Hand);
                var count = hand.TotalCount ?? (hand.Complete ? hand.Cards.Length : (int?)null);
                if (location?.Zone != CardZone.Board || location.Row is null) return [moved];
                if (count is null) return [moved.Unknown("Cat Witcher: hand count needed for Adrenaline 3")];
                var targets = moved.Position.Zone(Other(side), CardZone.Board, location.Row).Cards
                    .Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                if (targets.Length == 0) return [moved];
                var amount = count <= 3 ? 2 : 1;
                return SelectOptions(id, "random target", targets, card => card.InstanceId, Selection(id)?.RandomTargetId)
                    .SelectMany(target => Damage(moved with { Random = targets.Length > 1 }, target.InstanceId, amount, depth));
            })).ToArray();
        }

        var vyppers = state.Position.Zone(side, CardZone.Graveyard).Cards.Where(card => Rule(card.CardId)?.Reaction == "vypper").Select(card => card.InstanceId).ToArray();
        foreach (var id in vyppers)
            states = states.SelectMany(line => ResolveVypperEndTurn(line, id, side, depth)).ToArray();
        return states;
    }

    private IEnumerable<LineState> ResolveVypperEndTurn(LineState state, string id, PlayerSide graveOwner, int depth)
    {
        var grave = state.Position.Zone(graveOwner, CardZone.Graveyard); var vypper = grave.Cards.FirstOrDefault(card => card.InstanceId == id);
        if (vypper is null) return [state];
        var other = grave.Cards.Where(card => card.InstanceId != id).ToArray();
        if (other.Length > 0)
        {
            var known = other.Select(card => (Card: card, Provision: Rule(card.CardId)?.Card.Provision)).ToArray();
            if (known.Any(item => item.Provision is null)) return [state.Unknown("Vypper: lowest graveyard provision is unknown")];
            var lowest = known.Where(item => item.Provision == known.Min(value => value.Provision)).Select(item => item.Card).ToArray();
            return SelectOptions(id, "lowest graveyard banish", lowest, card => card.InstanceId, Selection(id)?.RandomTargetId)
                .Select(card => BanishOffBoard((state with { Random = lowest.Length > 1 }).Note("Vypper banishes " + Rule(card.CardId)?.Card.Name), card.InstanceId));
        }
        if (!grave.Complete && grave.TotalCount is not 1) return [state.Unknown("Vypper: graveyard completeness is needed before self-summon")];
        var targetSide = Other(graveOwner);
        var rows = Enum.GetValues<BoardRow>().Where(row => state.Position.Zone(targetSide, CardZone.Board, row).Cards.Length < 9).ToArray();
        if (rows.Length == 0) return [state.Note("Vypper self-summon blocked by full enemy board")];
        return SelectOptions(id, "random enemy row", rows, row => row.ToString(), Selection(id)?.Row?.ToString()).Select(row =>
        {
            var p = Remove(state.Position, id); var target = p.Zone(targetSide, CardZone.Board, row);
            var summoned = vypper with { Power = vypper.BasePower ?? Rule(vypper.CardId)!.Card.Power, Armor = 0,
                Statuses = (vypper.Statuses ?? ImmutableHashSet<CardStatus>.Empty).Add(CardStatus.Doomed).Add(CardStatus.Spying),
                StatusTurns = ImmutableDictionary<CardStatus, int>.Empty };
            return (state with { Position = Set(p, target, target.Cards.Add(summoned)), Random = rows.Length > 1 }).Note("Vypper summons with Doomed and Spying");
        });
    }
}
