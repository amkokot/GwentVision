using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed partial class TacticalPlayEngine
{
    private bool IsCrewed(GamePosition position, string id)
    {
        var adjacent = Adjacent(position, id).Select(cardId => Find(position, cardId)).Where(card => card is not null).ToArray();
        return adjacent.Length == 2 && adjacent.All(card => Rule(card!.CardId)?.Card is { } definition &&
            (definition.HasCategory("Soldier") || definition.HasCategory("Mage")));
    }

    private IEnumerable<LineState> BaronReset(LineState state, string baronId, string targetId)
    {
        var baron = Find(state.Position, baronId); var target = Find(state.Position, targetId);
        if (baron is null || target is null) return [state];
        var inspired = baron.Power > baron.BasePower; var boostLost = Math.Max(0, target.Power!.Value - target.BasePower!.Value);
        var reset = Reset(state.Note("Bloody Baron reset"), targetId);
        return inspired && boostLost > 0 ? [Duration(reset.Note("Bloody Baron Inspired Bleeding"), targetId, CardStatus.Bleeding, boostLost)] : [reset];
    }

    private LineState TickOrderCooldowns(LineState state, PlayerSide side)
    {
        foreach (var card in Board(state.Position, side).Where(card => Rule(card.CardId)?.Order is not null && card.Cooldown is > 0).ToArray())
            state = state with { Position = Change(state.Position, card with { Cooldown = card.Cooldown - 1 }) };
        return state;
    }

    private IEnumerable<LineState> AfterOrderUsed(IEnumerable<LineState> states, LineState before, PlayerSide side, int depth)
    {
        foreach (var troll in Board(before.Position, side).Where(card => Rule(card.CardId)?.Reaction == "order-armor" &&
                     card.Statuses?.Contains(CardStatus.Locked) != true).Select(card => card.InstanceId))
        {
            var listenerId = troll;
            states = states.Select(state => Locate(state.Position, listenerId)?.Zone == CardZone.Board
                ? AddArmor(state.Note("Trollololo gains 1 Armor from allied Order"), listenerId, 1) : state).ToArray();
        }
        foreach (var onager in Board(before.Position, side).Where(card => Rule(card.CardId)?.Reaction == "order-damage" &&
                     !card.Statuses!.Contains(CardStatus.Locked)).Select(card => card.InstanceId))
        {
            var listenerId = onager;
            states = states.SelectMany(state =>
            {
                if (Locate(state.Position, listenerId)?.Zone != CardZone.Board) return [state];
                var enemies = Board(state.Position, Other(side)).Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                return enemies.Length == 0 ? [state] : SelectOptions(listenerId, "Onager random target", enemies,
                    card => card.InstanceId, Selection(listenerId)?.RandomTargetId).SelectMany(card =>
                        Damage((state with { Random = enemies.Length > 1 }).Note("Onager Order reaction"), card.InstanceId, 1, depth));
            }).ToArray();
        }
        return states;
    }

    private IEnumerable<LineState> GapClosureEffect(LineState state, PositionCard played, PlayRule rule,
        PlayerSide side, BoardRow row, int depth)
    {
        if (rule.Effect == PlayEffect.HandBaseCopy)
        {
            var hand = state.Position.Zone(side, CardZone.Hand);
            var choices = hand.Cards.Where(card => card.InstanceId != played.InstanceId && Rule(card.CardId)?.Card is { Kind: CardKind.Unit } definition &&
                definition.Faction != "Neutral" && definition.Provision <= 10).ToArray();
            if (choices.Length == 0) return [hand.Complete ? state.Note("Caranthir: no eligible unit in hand") : state.Unknown("Caranthir: hand identities incomplete")];
            return Bound(SelectOptions(played.InstanceId, "hand copy", choices, card => card.InstanceId, Selection(played.InstanceId)?.TutorId)
                .SelectMany(card => Spawn(state.Note("Caranthir copies " + Rule(card.CardId)?.Card.Name), card.CardId, side, row, depth, 1)));
        }
        if (rule.Effect is PlayEffect.EnemyBronzeBaseCopy or PlayEffect.AlliedBronzeBaseCopy)
        {
            var owner = rule.Effect == PlayEffect.EnemyBronzeBaseCopy ? Other(side) : side;
            var choices = Board(state.Position, owner).Where(card => Rule(card.CardId) is { Card.Kind: CardKind.Unit, Card.IsGold: false } target &&
                (rule.Effect != PlayEffect.EnemyBronzeBaseCopy || !target.Disloyal)).ToArray();
            if (choices.Length == 0) return [state.Note("No eligible bronze board copy")];
            return Bound(SelectOptions(played.InstanceId, "board copy", choices, card => card.InstanceId, Selection(played.InstanceId)?.TargetId)
                .SelectMany(card => SpawnAndPlay(state.Note("Play base copy of " + Rule(card.CardId)?.Card.Name),
                    Rule(card.CardId)!.Card.Name, side, depth)));
        }
        if (rule.Effect == PlayEffect.ControlledUnitDamage)
        {
            var amount = Board(state.Position, side).Count(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit);
            var targets = Board(state.Position, Other(side)).Where(card => CanTarget(state.Position, card, side, TargetSide.Enemy)).ToArray();
            return targets.Length == 0 ? [state] : Bound(SelectOptions(played.InstanceId, "target", targets,
                card => card.InstanceId, Selection(played.InstanceId)?.TargetId)
                .SelectMany(card => Damage(state.Note($"Saer Qu'an damage {amount}"), card.InstanceId, amount, depth)));
        }
        if (rule.Effect == PlayEffect.NatureRebuke)
        {
            var targets = Board(state.Position, Other(side)).Where(card => CanTarget(state.Position, card, side, TargetSide.Enemy)).ToArray();
            return targets.Length == 0 ? [state] : Bound(SelectOptions(played.InstanceId, "target", targets,
                card => card.InstanceId, Selection(played.InstanceId)?.TargetId).SelectMany(target =>
                Damage(TargetedBySpecial(state, played, side, target.InstanceId), target.InstanceId, 5, depth).SelectMany(damaged =>
                {
                    if (Locate(damaged.Position, target.InstanceId)?.Zone == CardZone.Board) return [damaged];
                    var treants = Board(damaged.Position, side).Where(card => Rule(card.CardId)?.Card.HasCategory("Treant") == true).ToArray();
                    return treants.Length == 0 ? [damaged] : SelectOptions(played.InstanceId, "random Treant", treants,
                        card => card.InstanceId, Selection(played.InstanceId)?.RandomTargetId)
                        .Select(card => Boost((damaged with { Random = treants.Length > 1 }).Note("Nature's Rebuke Deathblow"), card.InstanceId, 2));
                })));
        }
        if (rule.Effect == PlayEffect.MultiCategoryBoost)
        {
            IEnumerable<LineState> states = [state];
            foreach (var category in (rule.Argument ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                states = Bound(states.SelectMany(line =>
                {
                    var choices = Board(line.Position, side).Where(card => card.InstanceId != played.InstanceId &&
                        Rule(card.CardId)?.Card.HasCategory(category) == true && CanTarget(line.Position, card, side, TargetSide.Allied)).ToArray();
                    return choices.Length == 0 ? [line.Note("No allied " + category)] : SelectOptions(played.InstanceId,
                        category + " target", choices, card => card.InstanceId, null).Select(card =>
                            Boost(TargetedBySpecial(line, played, side, card.InstanceId), card.InstanceId, rule.Amount));
                })).ToArray();
            }
            return states;
        }
        if (rule.Effect == PlayEffect.StartingDeckBonded)
        {
            var resources = side == PlayerSide.User ? state.Position.User : state.Position.Opponent;
            if (resources.StartingDeckIds is null) return [state.Unknown("Mushy Truffle: starting-deck identities unknown")];
            var choices = resources.StartingDeckIds.Select(Rule).Where(candidate => candidate?.Card is { Kind: CardKind.Unit } definition &&
                definition.AbilityText?.Contains("Bonded:", StringComparison.OrdinalIgnoreCase) == true).ToArray();
            if (choices.Length == 0) return [state.Note("Mushy Truffle: no Bonded starting-deck unit")];
            return Bound(SelectOptions(played.InstanceId, "Bonded unit", choices, candidate => candidate!.Card.Id,
                Selection(played.InstanceId)?.CreatedCardId).SelectMany(candidate =>
                    SpawnAndPlay(state.Note("Mushy Truffle creates " + candidate!.Card.Name), candidate.Card.Name, side, depth)));
        }
        if (rule.Effect == PlayEffect.AdjacentTripleBoost)
        {
            var groups = state.Position.Zones.Where(zone => zone.Zone == CardZone.Board).SelectMany(zone =>
                Enumerable.Range(0, Math.Max(0, zone.Cards.Length - 2)).Select(index => zone.Cards.Skip(index).Take(3).ToArray())
                    .Where(cards => cards.Length == 3 && cards.All(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit &&
                        CanTarget(state.Position, card, side, TargetSide.Any)))).ToArray();
            if (groups.Length == 0) return [state.Note("Golden Froth: no three adjacent units")];
            return Bound(SelectOptions(played.InstanceId, "adjacent triple", groups,
                cards => string.Join(',', cards.Select(card => card.InstanceId)), null).Select(cards =>
                cards.Aggregate(state.Note("Golden Froth adjacent triple"), (line, card) =>
                    Boost(TargetedBySpecial(line, played, side, card.InstanceId), card.InstanceId, rule.Amount))));
        }
        return [state.Unknown("Unsupported gap-closure effect")];
    }
}
