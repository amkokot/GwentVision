using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed partial class TacticalPlayEngine
{
    private IEnumerable<LineState>? ProjectionAdditional(LineState state, PositionCard source, PlayerSide side,
        BoardRow row, GamePosition before, ReachProjectionStep step, int depth)
    {
        LineState Purify(LineState line, string id) => Find(line.Position, id) is { } unit ?
            line with { Position = Change(line.Position, unit with { Statuses = ImmutableHashSet<CardStatus>.Empty, StatusTurns = null }) } : line;
        switch (step.Kind)
        {
            case "timer-arm":
            {
                LineState Arm(LineState line, string? target) => line with { Position = SetCardValue(line.Position,
                    new(side, source.CardId, "projection-timer-" + source.InstanceId, step.Amount, step.Amount, target)) };
                if (step.Argument != "copy") return [Arm(state, null)];
                var choices = Board(state.Position, side).Where(card => Rule(card.CardId)?.Card is { Kind: CardKind.Unit, IsGold: false } &&
                    CanTarget(state.Position, card, side, TargetSide.Allied)).ToArray();
                // The delayed body's printed value is the greedy objective, not zero current points.
                if (_selections is null) choices = choices.OrderByDescending(card => Rule(card.CardId)!.Card.Power).Take(1).ToArray();
                return choices.Length == 0 ? [Arm(state, null)] : SelectOptions(source.InstanceId, "timer target", choices,
                    card => card.InstanceId, Selection(source.InstanceId)?.TargetId).Select(card => Arm(state, card.CardId));
            }
            case "timer-tick":
            {
                var value = CardValue(state.Position, side, source.CardId, "projection-timer-" + source.InstanceId);
                if (value?.Minimum is not { } remaining) return [state.Unknown("Timer countdown/target not captured for " + Rule(source.CardId)!.Card.Name)];
                if (remaining == 0) return [state];
                var ticked = state with { Position = SetCardValue(state.Position, value with { Minimum = remaining - 1, Maximum = remaining - 1 }) };
                if (remaining > 1) return [ticked];
                if (step.Delayed is { } delayed) return ProjectionStep(ticked, source, side, row, before, delayed, depth + 1);
                if (value.StoredCardId is not { } copy) return [ticked];
                var zone = Locate(ticked.Position, source.InstanceId)!;
                var slot = Array.FindIndex(zone.Cards.ToArray(), card => card.InstanceId == source.InstanceId) + 1;
                return SpawnAt(ticked.Note("Megascope timer spawns before automatic engines"), copy, side, row, slot, depth);
            }
            case "purify-adjacent": return [Adjacent(state.Position, source.InstanceId).Aggregate(state, Purify)];
            case "purify-all": return [Board(state.Position).Where(card => card.InstanceId != source.InstanceId).Select(card => card.InstanceId).Aggregate(state, Purify)];
            case "purify-row": return [state.Position.Zone(side, CardZone.Board, row).Cards.Select(card => card.InstanceId).Aggregate(state, Purify)];
            case "move-self": return Move(state, source.InstanceId, depth, side);
            case "transform-self": return [Transform(state, source.InstanceId, step.Argument!)];
            case "summon-self-copies": return SummonAll(state, side, source.CardId, row, depth);
            case "damage-boost-self": return [Boost(state, source.InstanceId, step.Amount * Math.Max(0, (source.BasePower ?? 0) - (source.Power ?? 0)))];
            case "draw-discard":
            {
                var count = Math.Min(1, KnownCount(state.Position.Zone(side, CardZone.Deck)));
                return DiscardDamage(DrawTriggers(state, side, count), side, count, depth);
            }
            case "draw-play":
            {
                var deck = state.Position.Zone(side, CardZone.Deck);
                var drawn = DrawTriggers(state, side, Math.Min(1, KnownCount(deck)));
                if (deck.Cards.FirstOrDefault() is { } top)
                {
                    var p = Remove(drawn.Position, top.InstanceId); var hand = p.Zone(side, CardZone.Hand);
                    drawn = drawn with { Position = Set(p, hand, hand.Cards.Add(top)) };
                }
                return AgentChoose(drawn, drawn.Position.Zone(side, CardZone.Hand).Cards
                    .SelectMany(card => Play(drawn, card.InstanceId, side, depth, explicitSequenceStep: true)), side, "draw then hand play");
            }
            case "category-count":
            {
                var parts = step.Argument!.Split(';');
                var cards = parts[1] == "in your hand" ? state.Position.Zone(side, CardZone.Hand).Cards : Board(state.Position, side);
                return [Boost(state, source.InstanceId, cards.Count(card => Rule(card.CardId)?.Card.HasCategory(parts[0]) == true) * step.Amount)];
            }
            case "boosted-count":
            {
                var cards = step.Argument == "enemy unit" ? Board(state.Position, Other(side)) : state.Position.Zone(side, CardZone.Board, row).Cards;
                return [Boost(state, source.InstanceId, cards.Count(card => card.Power > card.BasePower) * step.Amount)];
            }
            case "row-ends":
                return Enum.GetValues<BoardRow>().SelectMany(targetRow =>
                {
                    var units = state.Position.Zone(Other(side), CardZone.Board, targetRow).Cards.Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                    var ids = units.Length == 0 ? [] : new[] { units[0].InstanceId, units[^1].InstanceId }.Distinct();
                    return Sequential([state], ids, (line, id) => Damage(line, id, step.Amount, depth));
                });
            case "highest-base-destroy":
            {
                var units = Board(state.Position).Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                if (units.Length == 0) return [state];
                var highest = units.Max(card => card.BasePower);
                return units.Where(card => card.BasePower == highest).SelectMany(card => Destroy(state with { Random = units.Count(unit => unit.BasePower == highest) > 1 }, card.InstanceId, false, depth));
            }
            case "mutagens": return ProjectionMutagens(state, source, side, row, before, depth);
            case "mirror-copies": return ProjectionMirror(state, source, side, depth);
            case "heal-boost": case "swap-armor": case "set-provision": case "take-armor":
            {
                var targetSide = step.Kind == "heal-boost" ? TargetSide.Allied : TargetSide.Any;
                var targets = Board(state.Position).Where(card => CanTarget(state.Position, card, side, targetSide)).ToArray();
                return SelectOptions(source.InstanceId, "projected target", targets, card => card.InstanceId, Selection(source.InstanceId)?.TargetId).SelectMany(target =>
                {
                    var line = TargetedBySpecial(state, source, side, target.InstanceId);
                    var changed = step.Kind switch
                    {
                        "heal-boost" => Boost(Heal(line, target.InstanceId, step.Amount), target.InstanceId, int.Parse(step.Argument!)),
                        "set-provision" => SetPower(line, target.InstanceId, Rule(target.CardId)!.Card.Provision),
                        "take-armor" => Boost(AddArmor(line, target.InstanceId, -(target.Armor ?? 0)), source.InstanceId, target.Armor ?? 0),
                        _ => SetPower(AddArmor(line, target.InstanceId, (target.Power ?? 0) - (target.Armor ?? 0)), target.InstanceId, target.Armor ?? 0),
                    };
                    return Find(changed.Position, target.InstanceId)?.Power <= 0 ? Destroy(changed, target.InstanceId, false, depth) : [changed];
                });
            }
            default: return null;
        }
    }

    private IEnumerable<LineState> ProjectionTimers(LineState state, PlayerSide side, int depth) =>
        Sequential([state], Board(state.Position, side).Where(card => Rule(card.CardId)?.Projection?.Automatic.Any(step => step.Kind == "timer-tick") == true)
            .Select(card => card.InstanceId), (line, id) =>
        {
            var card = Find(line.Position, id); var zone = Locate(line.Position, id);
            if (card is null || zone?.Zone != CardZone.Board || card.Statuses!.Contains(CardStatus.Locked)) return [line];
            return ProjectionSequence(line, card, side, zone.Row!.Value, line.Position,
                Rule(card.CardId)!.Projection!.Automatic.Where(step => step.Kind == "timer-tick").ToArray(), depth);
        });

    private IEnumerable<LineState> ProjectionMutagens(LineState state, PositionCard source, PlayerSide side,
        BoardRow row, GamePosition before, int depth)
    {
        var count = Board(state.Position, side).Count(card => Rule(card.CardId)?.Card.HasCategory("Salamandra") == true) >= 2 ? 2 : 1;
        IEnumerable<LineState> Color(LineState line, int color) => color switch
        {
            0 => Effect(line, source, new(Rule(source.CardId)!.Card, PlayEffect.Damage, 4, TargetSide.Enemy), side, row, before, depth),
            _ => Board(line.Position, side).Where(card => CanTarget(line.Position, card, side, TargetSide.Allied)).SelectMany(target =>
            {
                var targeted = TargetedBySpecial(line, source, side, target.InstanceId);
                return color == 1 ? ApplyPoison(targeted, target.InstanceId, depth).Select(output => GainCoins(output, side, 5)) :
                    [Status(Boost(targeted, target.InstanceId, 4), target.InstanceId, CardStatus.Veil)];
            }),
        };
        // Six ordered two-color choices, each with streamed greedy target selection.
        return Enumerable.Range(0, 3).SelectMany(first =>
        {
            var outputs = AgentChoose(state, Color(state, first), side, "first mutagen");
            return count == 1 ? outputs : outputs.SelectMany(line => Enumerable.Range(0, 3).Where(second => second != first)
                .SelectMany(second => AgentChoose(line, Color(line, second), side, "second mutagen")));
        });
    }

    private IEnumerable<LineState> ProjectionMirror(LineState state, PositionCard source, PlayerSide side, int depth)
    {
        // Only three identities and their resulting endpoint survive between decisions.
        var chosen = new HashSet<string>(StringComparer.Ordinal); var line = state;
        if (_selections is not null) return [state.Unknown("Obsidian Mirror replay requires three recorded target selections")];
        for (var index = 0; index < 3; index++)
        {
            LineState? best = null; string? bestId = null;
            foreach (var target in Board(line.Position, Other(side)).Where(card => !chosen.Contains(card.InstanceId) &&
                         Rule(card.CardId)?.Card is { Kind: CardKind.Unit, IsGold: false } && CanTarget(line.Position, card, side, TargetSide.Enemy)))
            {
                var row = Locate(line.Position, target.InstanceId)!.Row!.Value;
                if (line.Position.Zone(side, CardZone.Board, row).Cards.Length >= 9) continue;
                foreach (var output in Bound(Spawn(line, target.CardId, side, row, depth, 1)))
                    if (best is null || ReachSwing(state.Position, output.Position, side) > ReachSwing(state.Position, best.Position, side))
                    { best = output; bestId = target.InstanceId; }
            }
            if (best is null) break;
            chosen.Add(bestId!); line = best;
        }
        return [Approximate(line, "Obsidian Mirror uses 1-power printed copies; copied infusions/statuses are excluded")];
    }
}
