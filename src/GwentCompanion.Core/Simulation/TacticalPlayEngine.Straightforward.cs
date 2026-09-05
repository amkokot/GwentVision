using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed partial class TacticalPlayEngine
{
    private IEnumerable<LineState> StraightforwardEffect(LineState state, PositionCard played, PlayRule rule,
        PlayerSide side, BoardRow row, GamePosition before, int depth)
    {
        if (rule.Effect == PlayEffect.EnemyRowCountBoost)
            return Enum.GetValues<BoardRow>().Select(enemyRow => Boost(state.Note($"{rule.Card.Name} counts enemy {enemyRow}"),
                played.InstanceId, state.Position.Zone(Other(side), CardZone.Board, enemyRow).Cards.Count(card =>
                    Rule(card.CardId)?.Card.Kind == CardKind.Unit) * rule.Amount));

        if (rule.Effect == PlayEffect.ChoiceBuff)
        {
            var targets = Board(state.Position, side).Where(card => card.InstanceId != played.InstanceId &&
                CanTarget(state.Position, card, side, TargetSide.Allied)).ToArray();
            if (targets.Length == 0) return [state];
            return SelectOptions(played.InstanceId, "target", targets, card => card.InstanceId, Selection(played.InstanceId)?.TargetId)
                .SelectMany(target =>
                {
                    var targeted = TargetedBySpecial(state, played, side, target.InstanceId);
                    return new[]
                    {
                        Status(Boost(targeted.Note("Shaping Nature: 7 and Veil"), target.InstanceId, 7), target.InstanceId, CardStatus.Veil),
                        Boost(targeted.Note("Shaping Nature: 9"), target.InstanceId, 9),
                        Duration(Boost(targeted.Note("Shaping Nature: 6 and Vitality"), target.InstanceId, 6), target.InstanceId, CardStatus.Vitality, 6)
                    };
                });
        }

        if (rule.Effect == PlayEffect.Ida)
        {
            var targetSide = row == BoardRow.Ranged ? TargetSide.Allied : TargetSide.Any;
            var targets = Board(state.Position).Where(card => card.InstanceId != played.InstanceId &&
                CanTarget(state.Position, card, side, targetSide)).ToArray();
            if (targets.Length == 0) return [state];
            return SelectOptions(played.InstanceId, "target", targets, card => card.InstanceId, Selection(played.InstanceId)?.TargetId)
                .Select(target =>
                {
                    var targeted = TargetedBySpecial(state, played, side, target.InstanceId);
                    return row == BoardRow.Ranged
                    ? Duration(targeted.Note("Ida gives Vitality 3"), target.InstanceId, CardStatus.Vitality, 3)
                    : targeted with { Position = Change(targeted.Position, target with
                    {
                        Statuses = ImmutableHashSet<CardStatus>.Empty,
                        StatusTurns = ImmutableDictionary<CardStatus, int>.Empty
                    }) };
                });
        }

        if (rule.Effect == PlayEffect.CaravanVanguard)
        {
            var bonded = Board(before, side).Any(card => card.CardId == played.CardId);
            IEnumerable<LineState> outputs = row == BoardRow.Melee || bonded
                ? [Boost(state.Note(bonded ? "Caravan Vanguard Bonded boost" : "Caravan Vanguard melee boost"), played.InstanceId, rule.Amount)]
                : [state];
            return row == BoardRow.Ranged || bonded
                ? outputs.SelectMany(line => Spawn(line.Note(bonded ? "Caravan Vanguard Bonded copy" : "Caravan Vanguard ranged copy"),
                    played.CardId, side, row, depth))
                : outputs;
        }

        if (rule.Effect == PlayEffect.OrchardMantrap)
        {
            var current = Find(state.Position, played.InstanceId)!;
            state = state with { Position = Change(state.Position, current with { ExtraThrive = current.ExtraThrive + 1 }) };
            var allies = Board(state.Position, side).Where(card => card.InstanceId != played.InstanceId &&
                Rule(card.CardId)?.Card.Kind == CardKind.Unit && CanTarget(state.Position, card, side, TargetSide.Allied)).ToArray();
            if (row == BoardRow.Melee)
                return allies.Length == 0 ? [state] : SelectOptions(played.InstanceId, "consume target", allies,
                    card => card.InstanceId, Selection(played.InstanceId)?.TargetId)
                    .SelectMany(target => Consume(state, played.InstanceId, target.InstanceId, depth));
            if (allies.Length == 0) return [state];
            var pairs = allies.Length == 1 ? allies.Select(card => new[] { card }) :
                allies.SelectMany((first, index) => allies.Skip(index + 1).Select(second => new[] { first, second }));
            return Bound(pairs.Select(pair => pair.Aggregate(state.Note("Orchard Mantrap infuses Thrive"), (line, target) =>
            {
                var latest = Find(line.Position, target.InstanceId)!;
                return line with { Position = Change(line.Position, latest with
                {
                    ExtraThrive = latest.ExtraThrive + 1,
                    Statuses = latest.Statuses!.Add(CardStatus.Infused)
                }) };
            })));
        }

        if (rule.Effect == PlayEffect.DrawTopUnitsShuffle)
        {
            var deck = state.Position.Zone(side, CardZone.Deck);
            if (!deck.Complete) return [state.Unknown(rule.Card.Name + ": ordered deck required for top-unit draw")];
            var hand = state.Position.Zone(side, CardZone.Hand);
            var handCount = hand.TotalCount ?? hand.Cards.Length;
            var count = handCount <= 4 ? 2 : 1;
            var drawn = deck.Cards.Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).Take(count).ToArray();
            var p = state.Position;
            foreach (var card in drawn)
            {
                p = Remove(p, card.InstanceId);
                var destination = p.Zone(side, CardZone.Hand);
                p = Set(p, destination, destination.Cards.Add(card with { Power = card.Power + 1 }));
            }
            IEnumerable<LineState> outputs = [(state with { Position = p }).Note($"{rule.Card.Name} draws {drawn.Length} top unit(s)")];
            for (var index = 0; index < drawn.Length; index++)
            {
                var choiceIndex = index;
                outputs = Bound(outputs.SelectMany(line =>
                {
                    var choices = line.Position.Zone(side, CardZone.Hand).Cards.ToArray();
                    return SelectOptions(played.InstanceId, "shuffle " + choiceIndex, choices, card => card.InstanceId, null).Select(card =>
                    {
                        var next = Remove(line.Position, card.InstanceId); var bottom = next.Zone(side, CardZone.Deck);
                        return line with { Position = Set(next, bottom, bottom.Cards.Add(card)) };
                    });
                })).ToArray();
            }
            return outputs;
        }

        if (rule.Effect == PlayEffect.VeteranBerserk)
            return Damage(state.Note(rule.Card.Name + " damages self"), played.InstanceId, rule.Amount, depth).Select(line =>
            {
                var self = Find(line.Position, played.InstanceId);
                return self?.Power == 3 ? Heal(line.Note(rule.Card.Name + " Berserk heals self"), played.InstanceId,
                    Math.Max(0, (self.BasePower ?? self.Power ?? 0) - (self.Power ?? 0))) : line;
            });

        return [state.Unknown("Unsupported straightforward effect")];
    }
}
