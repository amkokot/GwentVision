using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed partial class TacticalPlayEngine
{
    private static PositionCardValue? CardValue(GamePosition position, PlayerSide side, string cardId, string kind) =>
        position.CardValues?.FirstOrDefault(value => value.Side == side && value.CardId == cardId &&
            value.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));

    private static GamePosition SetCardValue(GamePosition position, PositionCardValue value)
    {
        var values = position.CardValues ?? [];
        var current = values.FirstOrDefault(item => item.Side == value.Side && item.CardId == value.CardId &&
            item.Kind.Equals(value.Kind, StringComparison.OrdinalIgnoreCase));
        return position with { CardValues = current is null ? values.Add(value) : values.Replace(current, value) };
    }

    private IEnumerable<LineState> HenGaidth(LineState state, PositionCard played, PlayRule rule, PlayerSide side, int depth)
    {
        var carried = CardValue(state.Position, side, played.CardId, "stored-soul")?.StoredCardId;
        var targets = Board(state.Position, Other(side)).Where(card => CanTarget(state.Position, card, side, TargetSide.Enemy)).ToArray();
        if (targets.Length == 0) return carried is null ? [state.Note("Hen Gaidth Sword has no damage target")] :
            SpawnStored(state.Note("Hen Gaidth Sword replays its stored soul"), carried, side, depth);
        return Bound(SelectOptions(played.InstanceId, "target", targets, card => card.InstanceId, Selection(played.InstanceId)?.TargetId)
            .SelectMany(target => Damage(state.Note("Hen Gaidth target #" + target.InstanceId), target.InstanceId, rule.Amount, depth)
                .SelectMany(damaged =>
                {
                    var killed = Locate(damaged.Position, target.InstanceId)?.Zone != CardZone.Board;
                    if (killed)
                    {
                        damaged = damaged with { Position = SetCardValue(damaged.Position,
                            new(side, played.CardId, "stored-soul", StoredCardId: target.CardId)) };
                        damaged = BanishOffBoard(damaged, target.InstanceId).Note("Hen Gaidth stores " + Rule(target.CardId)?.Card.Name);
                    }
                    return carried is null ? [damaged] : SpawnStored(damaged.Note("Hen Gaidth replays its prior stored soul"), carried, side, depth);
                })));
    }

    private IEnumerable<LineState> SpawnStored(LineState state, string cardId, PlayerSide side, int depth)
    {
        var definition = Rule(cardId)?.Card;
        return definition is null ? [state.Unknown("Hen Gaidth stored identity is missing from the catalog")] :
            SpawnAndPlay(state, definition.Name, side, depth, definition.Power, doomed: true);
    }

    private IEnumerable<LineState> FrogMatingSeason(LineState state, PositionCard played, PlayerSide side, int depth)
    {
        var targets = Board(state.Position, side).Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit &&
            CanTarget(state.Position, card, side, TargetSide.Allied)).ToArray();
        if (targets.Length < 2) return [state.Note("Frog Mating Season requires 2 allied units")];
        var firstOptions = SelectOptions(played.InstanceId, "first allied target", targets, card => card.InstanceId,
            Selection(played.InstanceId)?.TargetId);
        return Bound(firstOptions.SelectMany(first => SelectOptions(played.InstanceId, "second allied target",
            targets.Where(card => card.InstanceId != first.InstanceId), card => card.InstanceId,
            Selection(played.InstanceId)?.AlliedTargetId).SelectMany(second =>
        {
            var line = TargetedBySpecial(TargetedBySpecial(state.Note($"Frog Mating Season targets #{first.InstanceId} and #{second.InstanceId}"),
                played, side, first.InstanceId), played, side, second.InstanceId);
            line = Duration(Duration(line, first.InstanceId, CardStatus.Vitality, 4), second.InstanceId, CardStatus.Vitality, 4);
            IEnumerable<LineState> outputs = [line];
            foreach (var targetId in new[] { first.InstanceId, second.InstanceId })
                outputs = outputs.SelectMany(output => SpawnFrogsAround(output, targetId, side, depth)).ToArray();
            return outputs;
        })));
    }

    private IEnumerable<LineState> SpawnFrogsAround(LineState state, string targetId, PlayerSide side, int depth)
    {
        var zone = Locate(state.Position, targetId);
        if (zone?.Zone != CardZone.Board || zone.Row is null) return [state];
        var cards = zone.Cards;
        var index = Array.FindIndex(cards.ToArray(), card => card.InstanceId == targetId);
        if (index < 0) return [state];
        // Insert the right Frog first so the second insertion leaves the selected unit between the two bodies.
        return SpawnAt(state, _names.GetValueOrDefault("Frog"), side, zone.Row.Value, index + 1, depth)
            .SelectMany(right =>
            {
                var current = Locate(right.Position, targetId)!;
                var currentIndex = Array.FindIndex(current.Cards.ToArray(), card => card.InstanceId == targetId);
                return SpawnAt(right, _names.GetValueOrDefault("Frog"), side, current.Row!.Value, currentIndex, depth);
            });
    }

    private IEnumerable<LineState> Aerondight(LineState state, PositionCard played, PlayerSide side, int depth)
    {
        var value = CardValue(state.Position, side, played.CardId, "damage");
        if (value is null) value = new(side, played.CardId, "damage", 0, 0);
        if (value.Minimum is null || value.Maximum is null || value.Minimum != value.Maximum)
            return [state.Unknown("Aerondight current damage is a range; an exact hover read is needed for deterministic targeting")];
        var amount = value.Maximum.Value;
        var targets = Board(state.Position, Other(side)).Where(card => CanTarget(state.Position, card, side, TargetSide.Enemy)).ToArray();
        if (targets.Length == 0 || amount == 0) return [state.Note($"Aerondight current damage {amount}")];
        return Bound(SelectOptions(played.InstanceId, "target", targets, card => card.InstanceId, Selection(played.InstanceId)?.TargetId)
            .SelectMany(target =>
            {
                var shielded = target.Statuses?.Contains(CardStatus.Shield) == true;
                var excess = shielded ? 0 : Math.Max(0, amount - (target.Power ?? 0) - (target.Armor ?? 0));
                return Damage(state.Note($"Aerondight {amount} damage"), target.InstanceId, amount, depth).SelectMany(damaged =>
                {
                    if (excess <= 0) return [damaged];
                    var allies = Board(damaged.Position, side).Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                    if (allies.Length == 0) return [damaged.Note("Aerondight excess has no allied boost target")];
                    var lowest = allies.Min(card => card.Power);
                    return allies.Where(card => card.Power == lowest).Select(card =>
                        Boost(damaged.Note($"Aerondight excess +{excess} to #{card.InstanceId}"), card.InstanceId, excess));
                });
            }));
    }

    private LineState GrowAerondight(LineState state, PlayerSide side)
    {
        if (Gap(state.Position, side) <= 0) return state;
        var present = state.Position.Zones.Where(zone => zone.Side == side && zone.Zone != CardZone.Banished)
            .SelectMany(zone => zone.Cards).Any(card => card.CardId == "203102");
        var current = CardValue(state.Position, side, "203102", "damage");
        if (!present && current is null) return state;
        var minimum = (current?.Minimum ?? 0) + 1;
        int? maximum = current?.Maximum is { } known ? known + 1 : null;
        return (state with { Position = SetCardValue(state.Position, new(side, "203102", "damage", minimum, maximum)) })
            .Note("Aerondight grows while ahead" + ((side == PlayerSide.User ? state.Position.User : state.Position.Opponent).Passed == true ? " after passing" : ""));
    }
}
