using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed partial class TacticalPlayEngine
{
    private IEnumerable<PlayRule> CreatePool(PositionCard source, string recipe, BoardRow row)
    {
        var parts = recipe.Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<PlayRule> pool = _rules.Values.Where(rule => rule.Card.CanBeInStartingDeck &&
            rule.Card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact);
        if (parts.Contains("runestone"))
            return pool.Where(rule => rule.Card.Name.EndsWith(" Runestone", StringComparison.Ordinal) &&
                PlayRules.Normalize(rule.Card.AbilityText).StartsWith("Create and play", StringComparison.Ordinal));
        var faction = parts.FirstOrDefault(part => part.StartsWith("faction:", StringComparison.OrdinalIgnoreCase))?[8..];
        if (faction is not null) pool = pool.Where(rule => rule.Card.Faction.Equals(faction, StringComparison.OrdinalIgnoreCase) ||
            rule.Card.SecondaryFactions.Contains(faction));
        if (parts.Contains("bronze")) pool = pool.Where(rule => !rule.Card.IsGold);
        if (parts.Contains("unit")) pool = pool.Where(rule => rule.Card.Kind == CardKind.Unit);
        if (parts.Contains("special")) pool = pool.Where(rule => rule.Card.Kind == CardKind.Special);
        var category = parts.FirstOrDefault(part => part.StartsWith("category:", StringComparison.OrdinalIgnoreCase))?[9..];
        if (category is not null) pool = pool.Where(rule => rule.Card.HasCategory(category));
        var power = source.Power ?? -1;
        if (parts.Contains("row-power"))
            pool = row == BoardRow.Melee ? pool.Where(rule => rule.Card.Provision == power) : pool.Where(rule => rule.Card.Provision <= power);
        if (parts.Contains("up-to-power")) pool = pool.Where(rule => rule.Card.Provision <= power);
        return pool.DistinctBy(rule => rule.Card.Id);
    }

    private IEnumerable<LineState> CreateAndPlay(LineState state, PositionCard source, string recipe,
        PlayerSide side, BoardRow row, int depth)
    {
        var pool = CreatePool(source, recipe, row).ToArray();
        if (pool.Length == 0) return [state.Unknown(Rule(source.CardId)!.Card.Name + ": Create pool is empty or current power is unread")];
        return Bound(SelectOptions(source.InstanceId, "created card", pool, rule => rule.Card.Id,
            Selection(source.InstanceId)?.CreatedCardId).SelectMany(created =>
            SpawnAndPlay((state with { Random = pool.Length > 1 }).Note("Create " + created.Card.Name), created.Card.Name, side, depth,
                recursiveGeneratedChoice: true)
                .SelectMany(line => recipe.Contains("harvest", StringComparison.OrdinalIgnoreCase)
                    ? HarvestHandBoost(line, source.InstanceId, side) : [line])));
    }

    private IEnumerable<LineState> HarvestHandBoost(LineState state, string sourceId, PlayerSide side)
    {
        var hand = state.Position.Zone(side, CardZone.Hand);
        if (hand.Cards.Length == 0)
            return hand.Complete || hand.TotalCount == 0 ? [state] : [state.Note("Bountiful Harvest hand boost target is hidden; no current-board points")];
        return SelectOptions(sourceId, "Harvest hand boost", hand.Cards, card => card.InstanceId,
                Selection(sourceId)?.AlliedTargetId)
            .Select(card => state with { Position = Change(state.Position, card with { Power = card.Power + 2, BasePower = card.BasePower }) });
    }

    private int? StoredStartingCount(GamePosition position, PlayerSide side, string kind, string? cardId = null)
    {
        var values = position.CardValues;
        if (values is null) return null;
        var value = values.Value.FirstOrDefault(item => item.Side == side && item.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) &&
            (cardId is null || item.CardId == cardId));
        return value?.Minimum == value?.Maximum ? value?.Minimum : null;
    }

    private IEnumerable<LineState> RecursiveChoiceEffect(LineState state, PositionCard played, PlayRule rule,
        PlayerSide side, BoardRow row, GamePosition before, int depth)
    {
        if (rule.Effect == PlayEffect.CreatePlay)
            return CreateAndPlay(state, played, rule.Argument!, side, row, depth);
        if (rule.Effect == PlayEffect.SpawnPlayChoice)
        {
            var choices = rule.Argument!.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(name => _names.TryGetValue(name, out var id) ? Rule(id) : null).Where(item => item is not null).ToArray();
            return choices.Length == 0 ? [state.Unknown(rule.Card.Name + ": generated choice definitions unavailable")] :
                Bound(SelectOptions(played.InstanceId, "generated card", choices, item => item!.Card.Id,
                    Selection(played.InstanceId)?.CreatedCardId).SelectMany(item =>
                    SpawnAndPlay((state with { Random = choices.Length > 1 }).Note(rule.Card.Name + " chooses " + item!.Card.Name), item.Card.Name, side, depth,
                        recursiveGeneratedChoice: true)));
        }
        if (rule.Effect == PlayEffect.StartingDeckTacticSpawns)
        {
            var tactics = StoredStartingCount(state.Position, side, "starting-tactic-count");
            if (tactics is null) return [state.Unknown(rule.Card.Name + ": starting-deck Tactic count unavailable")];
            IEnumerable<LineState> states = [state];
            for (var index = 0; index < tactics.Value / 3; index++)
                states = Bound(states.SelectMany(line => SpawnAndPlay(line.Note("Stefan Ace " + (index + 1)), rule.Argument!, side, depth,
                    recursiveGeneratedChoice: true))).ToArray();
            return states;
        }
        if (rule.Effect == PlayEffect.HandDiscardBoost)
        {
            var hand = state.Position.Zone(side, CardZone.Hand);
            if (hand.Cards.Length == 0) return hand.Complete ? [state.Note("Imlerith: no card available to discard")] :
                [state.Unknown("Imlerith: hand identities unavailable")];
            return Bound(SelectOptions(played.InstanceId, "discard", hand.Cards, card => card.InstanceId,
                Selection(played.InstanceId)?.TargetId).Select(discarded =>
            {
                var p = Remove(state.Position, discarded.InstanceId); var grave = p.Zone(side, CardZone.Graveyard);
                p = Set(p, grave, grave.Cards.Add(discarded with { Power = discarded.BasePower, Armor = 0,
                    Statuses = ImmutableHashSet<CardStatus>.Empty, StatusTurns = null }));
                var line = state with { Position = p };
                return Rule(discarded.CardId)?.Card.Kind == CardKind.Unit
                    ? Boost(line.Note("Imlerith discards " + Rule(discarded.CardId)!.Card.Name), played.InstanceId, discarded.Power ?? discarded.BasePower ?? 0)
                    : line.Note("Imlerith discards a non-unit");
            }));
        }
        if (rule.Effect == PlayEffect.StartingDeckCategoryBoost)
        {
            var category = rule.Argument!;
            var total = StoredStartingCount(state.Position, side, "starting-" + category.ToLowerInvariant() + "-count");
            var copies = StoredStartingCount(state.Position, side, "starting-copy-count", played.CardId);
            if (total is null || copies is null) return [state.Unknown(rule.Card.Name + ": inferred starting-deck composition unavailable")];
            var amount = Math.Max(0, total.Value - copies.Value);
            var targets = Board(state.Position, side).Where(card => card.InstanceId != played.InstanceId && CanTarget(state.Position, card, side, TargetSide.Allied)).ToArray();
            return targets.Length == 0 ? [state] : SelectOptions(played.InstanceId, "target", targets, card => card.InstanceId,
                Selection(played.InstanceId)?.TargetId).Select(card => Boost(TargetedBySpecial(
                    state.Note(rule.Card.Name + " starting-deck boost " + amount), played, side, card.InstanceId), card.InstanceId, amount));
        }
        if (rule.Effect == PlayEffect.AdjacentTransform)
        {
            IEnumerable<LineState> states = [state];
            foreach (var adjacentId in Adjacent(state.Position, played.InstanceId).ToArray())
            {
                var key = adjacentId;
                states = Bound(states.SelectMany(line =>
                {
                    var target = Find(line.Position, key); var definition = target is null ? null : Rule(target.CardId)?.Card;
                    if (target is null || definition?.Kind != CardKind.Unit) return [line];
                    var pool = _rules.Values.Where(item => item.Card.CanBeInStartingDeck && item.Card.Kind == CardKind.Unit &&
                        item.Card.Provision == definition.Provision + rule.Amount).DistinctBy(item => item.Card.Id).ToArray();
                    return pool.Length == 0 ? [line.Unknown("Cosimo: no provision-plus-one transform pool")] :
                        SelectOptions(played.InstanceId, "transform", pool, item => key + ":" + item.Card.Id,
                            Selection(played.InstanceId)?.CreatedCardId).Select(item => TransformTo(line with { Random = pool.Length > 1 }, key, item));
                })).ToArray();
            }
            return states;
        }
        if (rule.Effect == PlayEffect.TreantBoar)
        {
            if (Board(before, side).Any(card => Rule(card.CardId)?.Card.HasCategory("Dryad") == true) && Find(state.Position, played.InstanceId) is { } boar)
                return [state with { Position = Change(state.Position, boar with { Cooldown = 0 }) }];
            return [state];
        }
        return [state.Unknown("Unsupported recursive choice effect")];
    }

    private LineState TransformTo(LineState state, string id, PlayRule target)
    {
        var card = Find(state.Position, id); if (card is null) return state;
        var transformed = state with { Position = Change(state.Position, card with
        {
            CardId = target.Card.Id, Power = target.Card.Power, BasePower = target.Card.Power,
            Armor = target.Card.PrintedArmor ?? 0, Statuses = target.PrintedStatuses,
            StatusTurns = ImmutableDictionary<CardStatus, int>.Empty, Charges = target.InitialCharges,
            Cooldown = target.Zeal ? 0 : target.Order is null ? 0 : 1, ExtraThrive = 0, Original = false
        }) };
        return target.Unmodeled is null ? transformed : transformed.Unknown("Transformed board ability unsupported: " + target.Card.Name);
    }
}
