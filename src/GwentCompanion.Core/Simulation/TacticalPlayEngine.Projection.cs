using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed partial class TacticalPlayEngine
{
    private IEnumerable<LineState> ProjectionSequence(LineState state, PositionCard source, PlayerSide side,
        BoardRow row, GamePosition before, IReadOnlyList<ReachProjectionStep> steps, int depth)
    {
        if (depth > 10) return [Approximate(state.Unknown("Projection recursion budget reached"), "nested chain truncated")];
        state = Approximate(state, Rule(source.CardId)!.Card.Name + ": extracted-action projection; excluded text remains in assumptions");
        IEnumerable<LineState> lines = [state];
        foreach (var step in steps)
        {
            if (step.Row is not null && step.Row != row) continue;
            var action = step;
            lines = lines.SelectMany(line =>
            {
                if (!Spend()) return new[] { line.Unknown("Projection action budget reached") };
                var condition = ProjectionCondition(line.Position, source, side, action.Condition);
                if (condition == false) return action.Otherwise is { } fallback ?
                    ProjectionSequence(line, source, side, row, before, [fallback], depth + 1) : [line];
                var current = Locate(line.Position, source.InstanceId)?.Zone == CardZone.Board ? Find(line.Position, source.InstanceId)! : source;
                var outputs = AgentChoose(line, ProjectionStep(line, current, side, row, before, action, depth + 1), side,
                    Rule(source.CardId)!.Card.Name + " " + action.Kind);
                IEnumerable<LineState> inactive = condition is null && action.Otherwise is { } alternative ?
                    ProjectionSequence(line, source, side, row, before, [alternative], depth + 1) : [line];
                return condition is null ? inactive.Select(output => Approximate(output with { Random = true }, "condition unread: " + action.Condition))
                    .Concat(outputs.Select(output => Approximate(output with { Random = true }, "condition assumed active: " + action.Condition))) : outputs;
            }).ToArray();
        }
        return lines;
    }

    private bool? ProjectionCondition(GamePosition position, PositionCard source, PlayerSide side, string? condition)
    {
        if (condition is null) return true;
        var self = Find(position, source.InstanceId) ?? source;
        var owner = side == PlayerSide.User ? position.User : position.Opponent;
        var parts = condition.ToLowerInvariant().Split(' ');
        var amount = parts.Length > 1 && int.TryParse(parts[1], out var number) ? number : 0;
        return parts[0] switch
        {
            "devotion" => owner.Devotion ?? true, // explicitly requested default assumption
            "dominance" => Board(position, side).Select(card => card.Power ?? 0).DefaultIfEmpty().Max() >=
                Board(position, Other(side)).Select(card => card.Power ?? 0).DefaultIfEmpty().Max(),
            "bonded" => Board(position, side).Count(card => card.CardId == source.CardId) >= 2,
            "crew" => Adjacent(position, source.InstanceId) is { Length: 2 } neighbors &&
                neighbors.All(id => Find(position, id) is { } unit && Rule(unit.CardId)?.Card.HasCategory("Soldier") == true),
            "barricade" => self.Armor is null ? null : self.Armor > 0,
            "inspired" or "boosted" => self.Power > self.BasePower,
            "damaged" => self.Power < self.BasePower,
            "vitality" => self.Statuses?.Contains(CardStatus.Vitality),
            "bleeding" => self.Statuses?.Contains(CardStatus.Bleeding),
            "poison" => self.Statuses?.Contains(CardStatus.Poison),
            "hoard" => owner.Coins is null ? null : owner.Coins >= amount,
            "bloodthirst" => Board(position, Other(side)).Count(card => card.Power < card.BasePower) >= amount,
            "adrenaline" => position.Zone(side, CardZone.Hand) is { } hand && (hand.TotalCount ?? (hand.Complete ? hand.Cards.Length : (int?)null)) is { } count ? count <= amount : null,
            _ => null,
        };
    }

    private IEnumerable<LineState> ProjectionStep(LineState state, PositionCard source, PlayerSide side,
        BoardRow row, GamePosition before, ReachProjectionStep step, int depth)
    {
        var argument = step.Argument ?? "";
        var allies = Board(state.Position, side).Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).ToArray();
        var enemies = Board(state.Position, Other(side)).Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).ToArray();
        if (ProjectionAdditional(state, source, side, row, before, step, depth) is { } additional) return additional;
        switch (step.Kind)
        {
            case "rule": return Effect(state, source, step.Rule!, side, row, before, depth);
            case "boost-self": return [Boost(state, source.InstanceId, step.Amount)];
            case "heal-self": return [Heal(state, source.InstanceId, step.Amount)];
            case "damage-self": return Damage(state, source.InstanceId, step.Amount, depth);
            case "armor-self": return [AddArmor(state, source.InstanceId, step.Amount)];
            case "poison-self":
                return ApplyPoison(state, source.InstanceId, depth);
            case "purify-self":
                return Find(state.Position, source.InstanceId) is { } purified ?
                    [state with { Position = Change(state.Position, purified with { Statuses = ImmutableHashSet<CardStatus>.Empty, StatusTurns = null }) }] : [state];
            case "zeal":
                return Find(state.Position, source.InstanceId) is { } ready ? [state with { Position = Change(state.Position, ready with { Cooldown = 0 }) }] : [state];
            case "duration-self": return [Duration(state, source.InstanceId, Enum.Parse<CardStatus>(argument, true), step.Amount)];
            case "count-boost":
                var countUnits = argument == "enemy" ? enemies : allies.Where(card => card.InstanceId != source.InstanceId &&
                    (argument != "row" || Locate(state.Position, card.InstanceId)?.Row == row));
                return [Boost(state, source.InstanceId, countUnits.Count() * step.Amount)];
            case "boost-right":
            {
                var zone = Locate(state.Position, source.InstanceId);
                if (zone?.Zone != CardZone.Board) return [state];
                var index = Array.FindIndex(zone.Cards.ToArray(), card => card.InstanceId == source.InstanceId) + 1;
                return index < zone.Cards.Length ? [Boost(state, zone.Cards[index].InstanceId, step.Amount)] : [state];
            }
            case "berserker-tick":
                return Damage(state, source.InstanceId, 1, depth).SelectMany(line => ProjectionStep(line, source, side, row, before,
                    new("random-damage", 1, "enemy"), depth));
            case "half-row":
                return state.Position.Zones.Where(zone => zone.Zone == CardZone.Board && zone.Side == (argument == "boost" ? side : Other(side)))
                    .SelectMany(zone => Sequential([state], zone.Cards.Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).Select(card => card.InstanceId),
                        (line, id) => ProjectionPrimitive(line, id, argument, (Find(line.Position, id)?.BasePower ?? 0) / 2, depth)));
            case "transform-right":
            {
                var zone = Locate(state.Position, source.InstanceId);
                if (zone?.Zone != CardZone.Board) return [state];
                var slot = Array.FindIndex(zone.Cards.ToArray(), card => card.InstanceId == source.InstanceId) + 1;
                return slot >= zone.Cards.Length || Rule(zone.Cards[slot].CardId)?.Card.Kind != CardKind.Unit ? [state] :
                    [Transform(state, zone.Cards[slot].InstanceId, Rule(source.CardId)!.Card.Name)];
            }
            case "damage-carryover":
            {
                var separator = argument.IndexOf(';');
                return Effect(state, source, new(Rule(source.CardId)!.Card, PlayEffect.Damage, step.Amount, TargetSide.Enemy), side, row, before, depth)
                    .SelectMany(line => ProjectionStep(line, source, side, row, before,
                        new("carryover", int.Parse(argument[..separator]), argument[(separator + 1)..]), depth));
            }
            case "multiple-damage":
            {
                var parts = argument.Split(';'); var count = int.Parse(parts[0]); var random = bool.Parse(parts[1]);
                var actorTargets = bool.Parse(parts[2]) ? TargetSide.Enemy : TargetSide.Any;
                var targets = Board(state.Position).Where(card => card.InstanceId != source.InstanceId &&
                    Rule(card.CardId)?.Card.Kind == CardKind.Unit &&
                    (random ? actorTargets != TargetSide.Enemy || Locate(state.Position, card.InstanceId)!.Side != side : CanTarget(state.Position, card, side, actorTargets))).ToArray();
                count = Math.Min(count, targets.Length);
                if (_selections is not null && count > 1)
                    state = state.Unknown("Multiple projected targets require a reviewed target sequence");
                IEnumerable<LineState> Combinations(LineState line, int start, int remaining)
                {
                    if (remaining == 0) return [line];
                    return Enumerable.Range(start, Math.Max(0, targets.Length - start - remaining + 1)).SelectMany(index =>
                        Damage(line with { Random = line.Random || random && targets.Length > count }, targets[index].InstanceId, step.Amount, depth)
                            .SelectMany(next => Combinations(next, index + 1, remaining - 1)));
                }
                return Combinations(state, 0, count);
            }
            case "coins": return [GainCoins(state, side, step.Amount)];
            case "draw":
                for (var draw = 0; draw < step.Amount; draw++) state = AgentDrawTop(state, side);
                return [state];
            case "spawn":
            {
                var parts = argument.Split('|'); var token = parts[0]; var where = parts[1];
                IEnumerable<LineState> lines = [state];
                if (where == "each side of this card")
                {
                    for (var repeat = 0; repeat < step.Amount; repeat++)
                        lines = lines.SelectMany(line =>
                        {
                            var zone = Locate(line.Position, source.InstanceId);
                            if (zone?.Zone != CardZone.Board) return [line];
                            var slot = Array.FindIndex(zone.Cards.ToArray(), card => card.InstanceId == source.InstanceId);
                            return SpawnAt(line, token, side, row, slot + 1, depth).SelectMany(right =>
                                SpawnAt(right, token, side, row, slot, depth));
                        }).ToArray();
                    return lines;
                }
                var rows = where == "each allied row" ? Enum.GetValues<BoardRow>() : [row];
                foreach (var targetRow in rows)
                for (var repeat = 0; repeat < step.Amount; repeat++)
                { var destination = targetRow; lines = lines.SelectMany(line => Spawn(line, token, side, destination, depth)).ToArray(); }
                return lines;
            }
            case "create":
            {
                var pool = argument.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(Rule).OfType<PlayRule>().ToArray();
                if (pool.Length > 36) state = Approximate(state.Unknown("Create pool sampled: 36 identities; envelope is not a guaranteed bound"), "large Create pool sampled");
                // Include both cheap and expensive identities; avoid a body-only sorting bias towards large units.
                pool = pool.OrderBy(card => card.Card.Provision).ThenBy(card => card.Card.Id).ToArray();
                var sampled = pool.Length <= 36 ? pool : Enumerable.Range(0, 36).Select(index => pool[index * (pool.Length - 1) / 35]).ToArray();
                return AgentCreatedPool(state, source, sampled, side, depth, "projected Create");
            }
            case "starting-create":
            case "faction-create":
            {
                var parts = argument.Split(';'); var ownerSide = parts[0] == "opponent" ? Other(side) : side;
                var resources = ownerSide == PlayerSide.User ? state.Position.User : state.Position.Opponent;
                IEnumerable<PlayRule> pool;
                if (step.Kind == "starting-create")
                {
                    if (resources.StartingDeckIds is null) return [Approximate(state.Unknown("Starting deck identities unavailable"), "Create has no inferred starting-deck pool")];
                    var allowedIds = parts[1] == "unit" ? null : parts[1].Split('|').ToHashSet(StringComparer.Ordinal);
                    pool = resources.StartingDeckIds.Select(Rule).OfType<PlayRule>().Where(rule => parts[1] == "unit" ?
                        rule.Card.Kind == CardKind.Unit : allowedIds!.Contains(rule.Card.Id));
                }
                else
                {
                    var factions = resources.CurrentLeaderId is { } leader && Rule(leader) is { } leaderRule ? new[] { leaderRule.Card.Faction } :
                        state.Position.Zones.Where(zone => zone.Side == ownerSide && zone.Zone is CardZone.Deck or CardZone.Hand or CardZone.Board)
                            .SelectMany(zone => zone.Cards).Select(card => Rule(card.CardId)?.Card.Faction).OfType<string>()
                            .Where(faction => faction != "Neutral").Distinct().ToArray();
                    if (factions.Length == 0) return [Approximate(state.Unknown("Opponent faction unavailable for Create"), "Create faction unknown")];
                    pool = _rules.Values.Where(rule => rule.Card.CanBeInStartingDeck && !rule.Card.IsGold && factions.Contains(rule.Card.Faction) &&
                        rule.Card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact);
                }
                return ProjectionStep(state, source, side, row, before, new("create", Argument: string.Join('|', pool.Select(rule => rule.Card.Id))), depth);
            }
            case "provision-tutor":
            {
                var ownerSide = argument == "their" ? Other(side) : side;
                var owner = ownerSide == PlayerSide.User ? state.Position.User : state.Position.Opponent;
                var deck = state.Position.Zone(ownerSide, CardZone.Deck);
                var pool = deck.Cards.Where(card => Rule(card.CardId)?.Card is { Kind: CardKind.Unit } definition && definition.Provision <= step.Amount &&
                    (ownerSide != side ? owner.StartingDeckIds?.Contains(card.CardId) != true : definition.Faction != "Neutral")).ToArray();
                if (pool.Length == 0) return [state.Note("No known provision-matched tutor target")];
                return SelectOptions(source.InstanceId, "projected tutor", pool, card => card.InstanceId, Selection(source.InstanceId)?.TutorId)
                    .SelectMany(card =>
                    {
                        var prepared = ownerSide == side ? state : state with { Position = Change(state.Position, card with { Original = false }) };
                        return Play(Approximate(prepared with { Random = prepared.Random || !deck.Complete },
                            "Tutor uses inferred pool, excluding known ineligible identities"), card.InstanceId, side, depth, explicitSequenceStep: true)
                            .Select(line => Boost(line, card.InstanceId, step.Amount - Rule(card.CardId)!.Card.Provision));
                    });
            }
            case "pool-play":
            case "top-play":
            {
                var parts = argument.Split(';');
                var owner = step.Kind == "top-play" ? argument == "opponent" ? Other(side) : side :
                    parts[0] == "your" ? side : Other(side);
                var zoneKind = step.Kind == "top-play" ? CardZone.Deck : Enum.Parse<CardZone>(parts[1], true);
                var zone = state.Position.Zone(owner, zoneKind);
                var allowedIds = step.Kind == "top-play" ? null : parts[3].Split('|').ToHashSet(StringComparer.Ordinal);
                var pool = step.Kind == "top-play" ? zone.Cards.Take(step.Amount) :
                    zone.Cards.Where(card => allowedIds!.Contains(card.CardId));
                var choices = pool.ToArray();
                if (!zone.Complete) state = Approximate(state, "pool uses known/inferred " + zoneKind + "; unknown identities excluded");
                if (choices.Length == 0) return [state.Note("No known eligible " + zoneKind + " card")];
                var origin = state;
                return SelectOptions(source.InstanceId, "projected pool card", choices, card => card.InstanceId, Selection(source.InstanceId)?.TutorId).SelectMany(card =>
                {
                    var prepared = step.Kind == "pool-play" && parts[2] == "doomed" ? Status(origin, card.InstanceId, CardStatus.Doomed) : origin;
                    if (owner != side) prepared = prepared with { Position = Change(prepared.Position, Find(prepared.Position, card.InstanceId)! with { Original = false }) };
                    return Play(prepared, card.InstanceId, side, depth, explicitSequenceStep: true);
                });
            }
            case "weather-both":
            {
                var effects = state.Position.RowEffects ?? [];
                foreach (var affectedRow in Enum.GetValues<BoardRow>())
                {
                    var prior = effects.FirstOrDefault(effect => effect.AffectedSide == Other(side) && effect.Row == affectedRow);
                    if (prior is not null) effects = effects.Remove(prior);
                    effects = effects.Add(new(Other(side), affectedRow, argument, step.Amount +
                        (prior?.Name.Equals(argument, StringComparison.OrdinalIgnoreCase) == true ? prior.RemainingTurns ?? 0 : 0)));
                }
                return [state with { Position = state.Position with { RowEffects = effects, RowEffectsKnownInactive = false } }];
            }
            case "carryover":
            {
                var parts = argument.Split(';'); var zone = state.Position.Zone(side, Enum.Parse<CardZone>(parts[1], true));
                var count = parts[0] == "all" ? 200 : int.TryParse(parts[0], out var n) ? n : 1;
                var ids = parts[2].Split('|').ToHashSet();
                var choices = zone.Cards.Where(card => ids.Contains(card.CardId)).OrderByDescending(card => Rule(card.CardId)?.Card.Provision)
                    .Take(count).Select(card => card.InstanceId).ToHashSet();
                var changed = state with { Position = Set(state.Position, zone, zone.Cards.Select(card => choices.Contains(card.InstanceId) ? card with { Power = card.Power + step.Amount } : card)) };
                return [AddStored(changed, side, source.CardId, "reach-carryover", choices.Count * step.Amount)];
            }
            case "predatory-dive":
                return ProjectionStep(state, source, side, row, before, new("extreme-destroy", Argument: "lowest;allied"), depth)
                    .SelectMany(line => ProjectionStep(line, source, side, row, before, new("extreme-destroy", Argument: "lowest;enemy"), depth));
            case "surrender":
                return state.Position.Zones.Where(zone => zone.Zone == CardZone.Board).SelectMany(zone =>
                    Sequential([state], zone.Cards.Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).Select(card => card.InstanceId),
                        (line, id) => Damage(AddArmor(line, id, -Math.Min(2, Find(line.Position, id)?.Armor ?? 0)), id, 2, depth)));
        }
        if (step.Kind.StartsWith("all-", StringComparison.Ordinal))
        {
            var parts = argument.Split(';');
            var targets = parts[0] == "allied" ? allies : parts[0] == "enemy" ? enemies : allies.Concat(enemies);
            targets = targets.Where(card => (parts[1].Length == 0 || Rule(card.CardId)!.Card.HasCategory(parts[1])) &&
                (parts[2].Length == 0 || card.InstanceId != source.InstanceId));
            return Sequential([state], targets.Select(card => card.InstanceId), (line, id) => ProjectionPrimitive(line, id, step.Kind[4..], step.Amount, depth));
        }
        if (step.Kind.StartsWith("random-", StringComparison.Ordinal) || step.Kind.StartsWith("extreme-", StringComparison.Ordinal))
        {
            var random = step.Kind.StartsWith("random-", StringComparison.Ordinal); var parts = argument.Split(';');
            var targetSide = random ? argument : parts[1];
            var targets = (targetSide == "allied" ? allies : targetSide == "enemy" ? enemies : allies.Concat(enemies)).ToArray();
            if (targets.Length == 0) return [state];
            if (!random)
            {
                var power = parts[0] == "highest" ? targets.Max(card => card.Power) : targets.Min(card => card.Power);
                targets = targets.Where(card => card.Power == power).ToArray();
            }
            return targets.SelectMany(card => ProjectionPrimitive(state with { Random = state.Random || targets.Length > 1 },
                card.InstanceId, step.Kind[(random ? 7 : 8)..], step.Amount, depth));
        }
        var allowed = step.Kind is "triangle" or "tourney-joust" or "destroy-filter" or "dark-mirror" ? TargetSide.Any :
            step.Kind is "dynamic-damage" or "ivar" or "drain" ? TargetSide.Enemy : TargetSide.Allied;
        var legal = Board(state.Position).Where(card => CanTarget(state.Position, card, side, allowed)).ToArray();
        if (step.Kind == "destroy-filter") legal = legal.Where(card => argument == "doomed" ? card.Statuses!.Contains(CardStatus.Doomed) : Rule(card.CardId)!.Card.Provision == step.Amount).ToArray();
        if (legal.Length == 0) return [state];
        return SelectOptions(source.InstanceId, "projected target", legal, target => target.InstanceId, Selection(source.InstanceId)?.TargetId).SelectMany(target =>
        {
            var line = TargetedBySpecial(state, source, side, target.InstanceId);
            return step.Kind switch
            {
                "purify-boost" => new[] { Boost(line with { Position = Change(line.Position, target with { Statuses = ImmutableHashSet<CardStatus>.Empty, StatusTurns = null }) }, target.InstanceId, step.Amount) },
                "armor" => [AddArmor(line, target.InstanceId, step.Amount)],
                "destroy-filter" => Destroy(line, target.InstanceId, false, depth),
                "heal-full" => [Heal(line, target.InstanceId, int.MaxValue)],
                "poison-boost" => ApplyPoison(line, target.InstanceId, depth).Select(output => Boost(output, target.InstanceId, step.Amount)),
                "dark-mirror" when target.Power > target.BasePower => Damage(line, target.InstanceId, 2 * (target.Power!.Value - target.BasePower!.Value), depth),
                "dark-mirror" => [Boost(line, target.InstanceId, 2 * Math.Max(0, (target.BasePower ?? 0) - (target.Power ?? 0)))],
                "drain" => Damage(line, target.InstanceId, step.Amount, depth).Select(output => Boost(output, source.InstanceId,
                    Math.Max(0, (target.Power ?? 0) - (Locate(output.Position, target.InstanceId)?.Zone == CardZone.Board ? Find(output.Position, target.InstanceId)?.Power ?? 0 : 0)))),
                "ivar" => ProjectionIvar(line, source, target, side, depth),
                "tourney-joust" when Locate(line.Position, target.InstanceId)!.Side == side => [Boost(Status(line, target.InstanceId, CardStatus.Shield), target.InstanceId, step.Amount)],
                "tourney-joust" => Damage(line with { Position = Change(line.Position, target with { Statuses = target.Statuses!.Remove(CardStatus.Shield) }) }, target.InstanceId, step.Amount, depth),
                "restore" => [Boost(line, target.InstanceId, 2 * Math.Max(0, (target.BasePower ?? 0) - (target.Power ?? 0)))],
                "match-highest" => enemies.Length == 0 ? [line] : [SetPower(line, target.InstanceId, enemies.Max(card => card.Power ?? 0))],
                "triangle" when Locate(line.Position, target.InstanceId)!.Side == side => [Boost(line, target.InstanceId, Rule(target.CardId)!.Card.Provision)],
                "triangle" => Damage(line, target.InstanceId, Rule(target.CardId)!.Card.Provision, depth),
                "dynamic-damage" => Damage(line, target.InstanceId, argument == "provision" ? Rule(target.CardId)!.Card.Provision : allies.Select(card => card.Power ?? 0).DefaultIfEmpty().Max(), depth),
                _ => [line.Unknown("Projection primitive unavailable: " + step.Kind)],
            };
        });
    }

    private IEnumerable<LineState> ProjectionIvar(LineState state, PositionCard source, PositionCard target, PlayerSide side, int depth)
    {
        var adrenaline = ProjectionCondition(state.Position, source, side, "Adrenaline 2");
        var power = Find(state.Position, source.InstanceId)?.Power ?? source.Power ?? 0;
        var swap = SetPower(SetPower(state, source.InstanceId, target.Power ?? 0), target.InstanceId, power);
        if (adrenaline == false) return [swap];
        var damage = Damage(state, target.InstanceId, 4, depth);
        return adrenaline == true ? damage : new[] { swap with { Random = true } }.Concat(damage.Select(line => line with { Random = true }));
    }

    private IEnumerable<LineState> ProjectionPrimitive(LineState state, string id, string operation, int amount, int depth) => operation switch
    {
        "damage" => Damage(state, id, amount, depth), "destroy" => Destroy(state, id, false, depth),
        "heal" => [Heal(state, id, amount)], "boost" => [Boost(state, id, amount)], _ => [state],
    };

    private IEnumerable<LineState> ProjectionPlayListeners(LineState state, PositionCard played, PlayerSide side, GamePosition before, int depth)
    {
        var definition = Rule(played.CardId)!.Card;
        return Sequential([state], Board(before).Where(card => Rule(card.CardId)?.Projection?.Triggers.Length > 0)
            .Select(card => card.InstanceId), (line, id) =>
        {
            var listener = Find(line.Position, id); var zone = Locate(line.Position, id);
            if (listener is null || zone?.Zone != CardZone.Board || listener.Statuses!.Contains(CardStatus.Locked)) return [line];
            var steps = Rule(listener.CardId)!.Projection!.Triggers.Where(trigger => trigger.Opponent == (side != zone.Side) &&
                (trigger.Package switch
                {
                    "card" => true, "unit" => definition.Kind == CardKind.Unit, "special card" => definition.Kind == CardKind.Special,
                    "gold card" => definition.IsGold, "bronze card" => !definition.IsGold,
                    _ => definition.HasCategory(trigger.Package.Replace(" card", "")),
                })).Select(trigger => trigger.Step).ToArray();
            return ProjectionSequence(line, listener, zone.Side, zone.Row!.Value, before, steps, depth);
        });
    }

    private IEnumerable<LineState> ProjectionAutomatic(LineState state, PlayerSide side, int depth) =>
        Sequential([state], Board(state.Position, side).Where(card => Rule(card.CardId)?.Projection?.Automatic.Length > 0)
            .Select(card => card.InstanceId), (line, id) =>
        {
            var card = Find(line.Position, id); var zone = Locate(line.Position, id);
            return card is null || zone?.Zone != CardZone.Board || card.Statuses!.Contains(CardStatus.Locked) ? [line] :
                ProjectionSequence(line, card, side, zone.Row!.Value, line.Position,
                    Rule(card.CardId)!.Projection!.Automatic.Where(step => step.Kind != "timer-tick").ToArray(), depth);
        });
}
