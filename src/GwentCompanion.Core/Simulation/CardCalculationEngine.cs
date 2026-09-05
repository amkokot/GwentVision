using System.Collections.Immutable;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public enum DirectEffect { None, Damage, Boost }
public enum TargetSide { Any, Allied, Enemy }
public sealed record SimpleCardRule(CardDefinition Card, DirectEffect Effect, int Amount, TargetSide Target, bool Deploy);
public sealed record CalculationResult(bool Supported, GamePosition? After, int? PointSwing,
    ImmutableArray<string> Events, ImmutableArray<string> MissingRules,
    string Scope = "Immediate resolution before end-of-turn effects; only the explicitly supported rule subset.");

/// <summary>
/// First executable rules subset. Whole-text matching is intentionally strict. Any unmodeled
/// card/passive/row effect makes a full-play estimate unsupported, even in deck/hand/graveyard.
/// Future card handlers must explicitly implement triggers rather than treating unknown text as zero.
/// </summary>
public sealed class CardCalculationEngine(IEnumerable<CardDefinition> catalog)
{
    private readonly Dictionary<string, CardDefinition> _catalog = catalog.DistinctBy(card => card.Id).ToDictionary(card => card.Id);

    public static SimpleCardRule? CompileSimple(CardDefinition card)
    {
        if (card.AbilityText is null) return null;
        var text = Regex.Replace(card.AbilityText.Trim(), @"\s+", " ");
        if (card.Kind == CardKind.Unit && text.Length == 0) return new(card, DirectEffect.None, 0, TargetSide.Any, false);
        if (card.Kind is not CardKind.Unit and not CardKind.Special) return null;
        var match = Regex.Match(text, @"^(?<deploy>Deploy: )?(?<effect>Damage|Boost) (?:a|an) (?<side>enemy |allied )?unit by (?<amount>[1-9][0-9]?)\.$",
            RegexOptions.CultureInvariant);
        if (!match.Success || match.Groups["deploy"].Success != (card.Kind == CardKind.Unit)) return null;
        return new(card, match.Groups["effect"].Value == "Damage" ? DirectEffect.Damage : DirectEffect.Boost,
            int.Parse(match.Groups["amount"].Value), match.Groups["side"].Value switch { "enemy " => TargetSide.Enemy, "allied " => TargetSide.Allied, _ => TargetSide.Any },
            match.Groups["deploy"].Success);
    }

    public CalculationResult Resolve(GamePosition position, string instanceId, PlayerSide side, GameActionKind action,
        BoardRow row = BoardRow.Melee, int? insertAt = null, string? targetId = null)
    {
        CalculationResult Unsupported(params string[] reasons) => new(false, null, null, [], reasons.ToImmutableArray());
        if (action is not GameActionKind.Play and not GameActionKind.Summon) return Unsupported("Only Play and Summon entry resolution are modeled.");
        if (!position.RowEffectsKnownInactive || !position.User.PassiveEffectsKnownInactive || !position.Opponent.PassiveEffectsKnownInactive)
            return Unsupported("Leader/row passives are unknown or require a rule handler.");
        var keys = position.Zones.Select(zone => (zone.Side, zone.Zone, zone.Row)).ToArray();
        if (keys.Distinct().Count() != 12 || GamePosition.EmptyKnown().Zones.Any(zone => !keys.Contains((zone.Side, zone.Zone, zone.Row))))
            return Unsupported("Position must contain each side/zone exactly once.");
        if (position.Zones.Length != 12 || position.Zones.Any(zone => !zone.Complete || zone.TotalCount is { } count && count != zone.Cards.Length))
            return Unsupported("A complete supplied position is required; recognition currently supplies only partial zones.");
        var cards = position.Zones.SelectMany(zone => zone.Cards).ToArray();
        if (cards.Select(card => card.InstanceId).Distinct().Count() != cards.Length) return Unsupported("Duplicate physical instance IDs.");
        var rules = new Dictionary<string, SimpleCardRule>();
        foreach (var card in cards)
        {
            if (!_catalog.TryGetValue(card.CardId, out var definition) || CompileSimple(definition) is not { } rule)
                return Unsupported($"Unmodeled card {card.CardId}; its passive/trigger cannot be assumed absent.");
            rules[card.InstanceId] = rule;
            if (card.Power is null || card.BasePower is null || card.Armor is null || card.Statuses is null)
                return Unsupported($"Unknown stats/statuses on {card.InstanceId}.");
            if (card.Power < 0 || card.BasePower < 0 || card.Armor < 0 ||
                rule.Card.Kind == CardKind.Unit && (card.Power == 0 || card.BasePower == 0)) return Unsupported("Invalid supplied unit stats.");
            // These statuses need ongoing/trigger-specific handlers, not a generic point approximation.
            if (card.Statuses.Except([CardStatus.Shield, CardStatus.Locked, CardStatus.Doomed, CardStatus.Defender, CardStatus.Immune]).Any())
                return Unsupported($"Unmodeled active status on {card.InstanceId}.");
        }
        var source = position.Zones.SingleOrDefault(zone => zone.Cards.Any(card => card.InstanceId == instanceId));
        if (source is null || source.Side != side || source.Zone is not CardZone.Hand and not CardZone.Deck and not CardZone.Graveyard)
            return Unsupported("Entry source must be this side's known hand, deck or graveyard.");
        if (action == GameActionKind.Summon && source.Zone == CardZone.Hand) return Unsupported("This subset does not model hand-summoning.");
        var cardToPlay = source.Cards.Single(card => card.InstanceId == instanceId); var behavior = rules[instanceId];
        if (source.Zone == CardZone.Graveyard && behavior.Card.Kind == CardKind.Unit)
        {
            if (behavior.Card.PrintedArmor is null) return Unsupported("Printed armor is needed to model this graveyard return.");
            cardToPlay = cardToPlay with { Armor = behavior.Card.PrintedArmor };
        }
        if (action == GameActionKind.Summon && behavior.Card.Kind != CardKind.Unit) return Unsupported("Only unit summons are modeled.");
        if (position.Zones.Where(zone => zone.Zone == CardZone.Board).Any(zone => zone.Cards.Length > GwentRules.MaximumRowSize)) return Unsupported("Overfull row.");
        var destination = position.Zone(side, CardZone.Board, row);
        var insertion = insertAt ?? destination.Cards.Length;
        if (behavior.Card.Kind == CardKind.Unit && (destination.Cards.Length >= GwentRules.MaximumRowSize || insertion < 0 || insertion > destination.Cards.Length))
            return Unsupported("No valid insertion slot in the chosen row.");
        var targetZone = position.Zones.FirstOrDefault(zone => zone.Zone == CardZone.Board && zone.Cards.Any(card => card.InstanceId == targetId));
        var target = targetZone?.Cards.FirstOrDefault(card => card.InstanceId == targetId);
        var resolvesEffect = action == GameActionKind.Play && behavior.Effect != DirectEffect.None &&
            !(behavior.Deploy && cardToPlay.Statuses!.Contains(CardStatus.Locked));
        if (resolvesEffect)
        {
            if (target is null || rules[target.InstanceId].Card.Kind != CardKind.Unit) return Unsupported("A known board unit target must be supplied.");
            if (behavior.Target == TargetSide.Allied && targetZone!.Side != side || behavior.Target == TargetSide.Enemy && targetZone!.Side == side)
                return Unsupported("Target is on the wrong side.");
            if (target.Statuses!.Contains(CardStatus.Immune)) return Unsupported("Immune unit cannot be manually targeted.");
            if (targetZone!.Side != side && !target.Statuses.Contains(CardStatus.Defender) &&
                targetZone.Cards.Any(card => card.Statuses!.Contains(CardStatus.Defender))) return Unsupported("Target is protected by a row Defender.");
        }
        var zones = position.Zones.ToDictionary(zone => (zone.Side, zone.Zone, zone.Row));
        void Set(PositionZone zone, IEnumerable<PositionCard> values)
        { var items = values.ToImmutableArray(); zones[(zone.Side, zone.Zone, zone.Row)] = zone with { Cards = items, TotalCount = items.Length }; }
        void MoveOut(PositionZone from, PositionCard card, CardZone to)
        {
            Set(from, from.Cards.Where(item => item.InstanceId != card.InstanceId));
            var targetZone = zones[(from.Side, to, null)];
            Set(targetZone, targetZone.Cards.Append(card with { Power = card.BasePower, Armor = 0, Statuses = ImmutableHashSet<CardStatus>.Empty, Charges = 0, Cooldown = 0 }));
        }
        var events = ImmutableArray.CreateBuilder<string>();
        int Score(GamePosition state, PlayerSide player) => state.Zones.Where(zone => zone.Zone == CardZone.Board && zone.Side == player)
            .SelectMany(zone => zone.Cards).Where(card => rules[card.InstanceId].Card.Kind == CardKind.Unit).Sum(card => card.Power!.Value);
        var other = side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User;
        var beforeGap = Score(position, side) - Score(position, other);
        Set(source, source.Cards.Where(card => card.InstanceId != instanceId));
        if (behavior.Card.Kind == CardKind.Unit)
        {
            Set(destination, destination.Cards.Insert(insertion, cardToPlay));
            events.Add(action == GameActionKind.Play ? "UnitPlayed" : "UnitSummoned (no Deploy)");
        }
        else events.Add("SpecialPlayed");
        if (resolvesEffect)
        {
            var input = new UnitPrimitiveState(target!.Power!.Value, target.BasePower!.Value, target.Armor!.Value, target.Statuses!);
            var effect = behavior.Effect == DirectEffect.Damage ? GwentRules.Damage(input, behavior.Amount) : GwentRules.Boost(input, behavior.Amount);
            events.AddRange(effect.TriggerHooks);
            var updatedTarget = target with { Power = effect.Unit.Power, Armor = effect.Unit.Armor, Statuses = effect.Unit.Statuses };
            var liveZone = zones[(targetZone!.Side, CardZone.Board, targetZone.Row)];
            if (effect.Destroyed) MoveOut(liveZone, target, target.Statuses!.Contains(CardStatus.Doomed) ? CardZone.Banished : CardZone.Graveyard);
            else Set(liveZone, liveZone.Cards.Select(card => card.InstanceId == target.InstanceId ? updatedTarget : card));
            if (behavior.Deploy) events.Add("DeployResolved");
        }
        if (behavior.Card.Kind == CardKind.Special)
        {
            var graveyard = zones[(side, cardToPlay.Statuses!.Contains(CardStatus.Doomed) ? CardZone.Banished : CardZone.Graveyard, null)];
            Set(graveyard, graveyard.Cards.Append(cardToPlay with { Statuses = ImmutableHashSet<CardStatus>.Empty }));
        }
        var after = position with { Zones = position.Zones.Select(zone => zones[(zone.Side, zone.Zone, zone.Row)]).ToImmutableArray() };
        return new(true, after, Score(after, side) - Score(after, other) - beforeGap, events.ToImmutable(), []);
    }
}
