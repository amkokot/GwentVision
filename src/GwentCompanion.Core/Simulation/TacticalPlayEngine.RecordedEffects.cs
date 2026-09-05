using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed partial class TacticalPlayEngine
{
    private static string[] Adjacent(GamePosition p, string id)
    {
        var zone = Locate(p, id); if (zone?.Zone != CardZone.Board) return [];
        var index = Array.FindIndex(zone.Cards.ToArray(), card => card.InstanceId == id);
        return new[] { index - 1, index + 1 }.Where(i => i >= 0 && i < zone.Cards.Length).Select(i => zone.Cards[i].InstanceId).ToArray();
    }
    private LineState AddArmor(LineState s, string id, int armor)
    {
        var card = Find(s.Position, id); if (card is null) return s;
        var updated = card with { Armor = card.Armor + armor };
        if (Rule(card.CardId)?.PowerInvariant == "armor") updated = updated with { Power = updated.Armor, BasePower = updated.Armor };
        return s with { Position = Change(s.Position, updated) };
    }

    private IEnumerable<LineState> FeeOnce(LineState state, string id, PlayerSide side, int depth)
    {
        var card = Find(state.Position, id); var zone = Locate(state.Position, id);
        if (card is null || zone?.Side != side || zone.Zone != CardZone.Board || card.Statuses!.Contains(CardStatus.Locked) ||
            Rule(card.CardId) is not { FeeCost: > 0, FeeAction: not null } rule || rule.FeeRow is { } required && zone.Row != required) return [];
        var resources = side == PlayerSide.User ? state.Position.User : state.Position.Opponent;
        if (resources.Coins is null || resources.Coins < rule.FeeCost || rule.FeeCooldown > 0 && card.Cooldown != 0 || !Spend()) return [];
        var remaining = resources.Coins.Value - rule.FeeCost;
        var paidCard = rule.FeeCooldown > 0 ? card with { Cooldown = 1 } : card;
        var paid = SetCoins(state with { Position = Change(state.Position, paidCard) }, side, remaining)
            .Note($"Fee {rule.FeeCost} · {rule.Card.Name}");

        if (rule.FeeAction == "self") return [Boost(paid, id, rule.FeeAmount)];
        if (rule.FeeAction == "self-hoard")
        {
            var threshold = Math.Max(0, 7 - (resources.CurrentLeaderId == "202577" ? 2 : 0));
            return [Boost(paid, id, resources.Coins >= threshold ? 3 : rule.FeeAmount)];
        }
        if (rule.FeeAction == "guard")
        {
            var amount = remaining == 0 ? 2 : rule.FeeAmount;
            foreach (var neighbor in Adjacent(paid.Position, id).Select(target => Find(paid.Position, target)!).Where(target => Rule(target.CardId)?.Card.Kind == CardKind.Unit))
                paid = Boost(paid, neighbor.InstanceId, amount);
            return [paid];
        }
        if (rule.FeeAction.StartsWith("spawn:", StringComparison.Ordinal))
            return Spawn(paid, _names.GetValueOrDefault(rule.FeeAction[6..]), side, zone.Row!.Value, depth);

        var targetSide = rule.FeeAction is "boost" or "vitality" or "poison-boost" ? TargetSide.Allied : TargetSide.Enemy;
        var targets = Board(paid.Position, targetSide == TargetSide.Allied ? side : Other(side))
            .Where(target => target.InstanceId != id || rule.FeeAction == "poison-boost")
            .Where(target => CanTarget(paid.Position, target, side, targetSide)).ToArray();
        if (rule.FeeAction == "highest-damage" && targets.Length > 0)
        {
            var highest = targets.Max(target => target.Power);
            targets = targets.Where(target => target.Power == highest).ToArray();
        }
        if (targets.Length == 0) return [];
        if (rule.FeeAction == "boost") targets = targets.Take(1).ToArray(); // Same point result; avoid 9^9 equivalent branches.
        return SelectOptions(id, "fee target", targets, target => target.InstanceId, Selection(id)?.TargetId).SelectMany(target =>
        {
            var output = paid.Note("Fee target #" + target.InstanceId);
            return rule.FeeAction switch
            {
                "boost" => [Boost(output, target.InstanceId, rule.FeeAmount)],
                "damage" or "highest-damage" => Damage(output, target.InstanceId, rule.FeeAmount, depth),
                "damage-destroy-self" => Damage(output, target.InstanceId, rule.FeeAmount, depth)
                    .SelectMany(item => Destroy(item, id, false, depth)),
                "vitality" => [Duration(output, target.InstanceId, CardStatus.Vitality, rule.FeeAmount)],
                "bleeding" => [Duration(output, target.InstanceId, CardStatus.Bleeding, rule.FeeAmount)],
                "bounty-bleed" => target.Statuses!.Contains(CardStatus.Bounty)
                    ? Damage(output, target.InstanceId, 1, depth) : [Duration(output, target.InstanceId, CardStatus.Bleeding, 1)],
                "poison-boost" => ApplyPoison(output, target.InstanceId, depth).Select(line => Boost(line, target.InstanceId, rule.FeeAmount)),
                _ => [output.Unknown("Unsupported Fee action: " + rule.FeeAction)]
            };
        });
    }

    private IEnumerable<LineState> OrderOnce(LineState state, string id, PlayerSide side, int depth)
    {
        var card = Find(state.Position, id); var zone = Locate(state.Position, id);
        if (card is null || zone?.Side != side || zone.Zone != CardZone.Board || card.Statuses!.Contains(CardStatus.Locked) ||
            ruleRowMismatch() ||
            card.Charges is not > 0 || card.Cooldown != 0 || Rule(card.CardId) is not { Order: not null } rule) return [];
        bool ruleRowMismatch() => Rule(card.CardId)?.OrderRow is { } required && zone.Row != required;
        if (!Spend() || rule.Order == "shield-fee") return [];
        if (rule.Order == "barricade-damage" && card.Armor is not > 0) return [];
        if (rule.Order == "consume-dominance" && PlayConditions.Evaluate(PlayConditions.Find("dominance")!, state.Position, side, Rule) != ConditionTruth.Met) return [];
        var spentCard = rule.OrderCooldown > 0 ? card with { Cooldown = rule.OrderCooldown } : card with { Charges = card.Charges - 1 };
        var spent = (state with { Position = Change(state.Position, spentCard) }).Note("Order " + rule.Card.Name);
        if (rule.Order == "striga-token")
        {
            var tokens = Board(state.Position).Where(target => target.Power < card.Power &&
                Rule(target.CardId)?.Card.HasCategory("Token") == true && CanTarget(state.Position, target, side, TargetSide.Any)).ToArray();
            return AfterOrderUsed(SelectOptions(id, "Predator Token", tokens, target => target.InstanceId,
                Selection(id)?.TargetId).SelectMany(target =>
            {
                var doubled = Locate(state.Position, target.InstanceId)!.Side == side;
                return Consume(doubled ? Boost(spent, id, target.Power ?? 0) : spent, id, target.InstanceId, depth);
            }), state, side, depth);
        }
        if (rule.Order == "projection")
            return AfterOrderUsed(ProjectionSequence(spent, spentCard, side, zone.Row!.Value, state.Position,
                rule.Projection!.Orders, depth), state, side, depth);
        if (rule.Order == "snowdrop-draw")
            return AfterOrderUsed([DrawTriggers(spent.Note("Snowdrop draws and returns up to two cards"), side,
                Math.Min(2, KnownCount(spent.Position.Zone(side, CardZone.Deck))))], state, side, depth);
        if (rule.Order == "coral-discard")
        {
            var draws = Math.Min(1, KnownCount(spent.Position.Zone(side, CardZone.Deck)));
            var drawn = DrawTriggers(spent.Note("Coral draws then discards"), side, draws);
            return AfterOrderUsed(DiscardDamage(drawn, side, draws, depth), state, side, depth);
        }
        if (rule.Order == "tutor-boosted")
        {
            var deck = spent.Position.Zone(side, CardZone.Deck);
            var choices = deck.Cards.Where(candidate => Rule(candidate.CardId)?.Card.Kind == CardKind.Unit &&
                candidate.Power > candidate.BasePower).ToArray();
            IEnumerable<LineState> outputs = choices.Length == 0 ? [spent.Note("Sigismund Dijkstra: no inferred boosted unit in deck")] :
                Bound(BoundedPool(choices).SelectMany(candidate => Play(Approximate(spent,
                    "Sigismund Dijkstra tutor uses boosted units in the inferred draw pile"), candidate.InstanceId,
                    side, depth, explicitSequenceStep: true)));
            return AfterOrderUsed(outputs, state, side, depth);
        }
        if (rule.Order == "erland-cashout")
        {
            var carry = Stored(spent.Position, side, "reach-carryover", id);
            var realized = Boost(spent.Note($"Erland realizes {carry} deck boost"), id, carry);
            if (carry > 0) realized = AddStored(realized, side, id, "reach-carryover", -carry);
            return AfterOrderUsed([realized], state, side, depth);
        }
        if (rule.Order == "professional")
        {
            var enemies = Board(spent.Position, Other(side)).Where(target =>
                CanTarget(spent.Position, target, side, TargetSide.Enemy)).ToArray();
            IEnumerable<LineState> outputs = enemies.Length == 0 ? [spent] : SelectOptions(id, "Geralt Professional target",
                enemies, target => target.InstanceId, Selection(id)?.TargetId).SelectMany(target =>
                    target.Power % 3 == 0 ? Destroy(spent.Note("Geralt: Professional tall punish"), target.InstanceId, false, depth) :
                        Damage(spent, target.InstanceId, 3, depth));
            return AfterOrderUsed(outputs, state, side, depth);
        }
        if (rule.Order == "random-split-3")
        {
            IEnumerable<LineState> outputs = [spent];
            for (var hit = 0; hit < 3; hit++) outputs = Bound(outputs.SelectMany(line =>
            {
                var enemies = Board(line.Position, Other(side)).Where(target => Rule(target.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                return enemies.Length == 0 ? [line] : SelectOptions(id, "Herkja random target", enemies,
                    target => target.InstanceId, Selection(id)?.RandomTargetId).SelectMany(target =>
                        Damage((line with { Random = line.Random || enemies.Length > 1 }).Note("Herkja split damage"),
                            target.InstanceId, 1, depth));
            })).ToArray();
            return AfterOrderUsed(outputs, state, side, depth);
        }
        if (rule.Order == "armor-boost")
        {
            var armor = card.Armor ?? 0;
            var current = Find(spent.Position, id)!;
            var converted = Boost(spent with { Position = Change(spent.Position, current with { Armor = 0 }) }, id, armor);
            return AfterOrderUsed([converted.Note($"Trollololo converts {armor} Armor")], state, side, depth);
        }
        if (rule.Order == "duel")
        {
            var enemies = Board(spent.Position, Other(side)).Where(target =>
                CanTarget(spent.Position, target, side, TargetSide.Enemy)).ToArray();
            IEnumerable<LineState> outputs = enemies.Length == 0 ? [spent] : SelectOptions(id, "Duel target", enemies,
                target => target.InstanceId, Selection(id)?.TargetId).SelectMany(target => Damage(Approximate(spent,
                    $"Duel reach approximated as twice {rule.Card.Name}'s current power"), target.InstanceId,
                    2 * (card.Power ?? rule.Card.Power), depth));
            return AfterOrderUsed(outputs, state, side, depth);
        }
        if (rule.Order == "mammuna")
        {
            var grave = spent.Position.Zone(side, CardZone.Graveyard);
            var deck = spent.Position.Zone(side, CardZone.Deck);
            var choices = grave.Cards.Where(target => Rule(target.CardId) is { Card.Kind: CardKind.Unit, Card.IsGold: false } &&
                !Board(spent.Position, side).Any(board => board.CardId == target.CardId) &&
                deck.Cards.Any(copy => copy.CardId == target.CardId)).ToArray();
            IEnumerable<LineState> outputs = choices.Length == 0 ? [spent.Note("Mammuna: no graveyard bronze with an unused deck copy")] :
                SelectOptions(id, "Mammuna graveyard unit", choices, target => target.InstanceId,
                    Selection(id)?.TargetId).SelectMany(target =>
                {
                    var deckCopy = deck.Cards.First(copy => copy.CardId == target.CardId);
                    var boosted = Boost(BanishOffBoard(spent.Note("Mammuna banishes " + Rule(target.CardId)?.Card.Name),
                        target.InstanceId), id, target.Power ?? target.BasePower ?? Rule(target.CardId)!.Card.Power);
                    return Summon(boosted, deckCopy.InstanceId, side, zone.Row!.Value, depth);
                });
            return AfterOrderUsed(outputs, state, side, depth);
        }
        if (rule.Order == "emhyr-seize")
        {
            var emhyrTargets = Board(spent.Position, Other(side)).Where(target => target.Power == 1 &&
                target.Statuses?.Contains(CardStatus.Spying) == true && CanTarget(spent.Position, target, side, TargetSide.Enemy)).ToArray();
            IEnumerable<LineState> outputs = emhyrTargets.Length == 0 ? [spent] : SelectOptions(id, "Emhyr seize target", emhyrTargets,
                target => target.InstanceId, Selection(id)?.TargetId).Select(target =>
            {
                var source = Locate(spent.Position, target.InstanceId)!;
                var destination = spent.Position.Zone(side, CardZone.Board, source.Row);
                if (destination.Cards.Length >= 9) return spent.Note("Emhyr seize blocked by full row");
                var position = Remove(spent.Position, target.InstanceId);
                destination = position.Zone(side, CardZone.Board, source.Row);
                return spent with { Position = Set(position, destination, destination.Cards.Add(target)) };
            });
            return AfterOrderUsed(outputs, state, side, depth);
        }
        if (rule.Order == "raffard-hand")
        {
            var hand = spent.Position.Zone(side, CardZone.Hand);
            var choices = hand.Cards.Where(candidate => Rule(candidate.CardId)?.Card is { Kind: CardKind.Unit, IsGold: false }).ToArray();
            IEnumerable<LineState> outputs = choices.Length == 0
                ? hand.Complete ? [spent.Note("Raffard: no bronze unit in hand")] : [spent.Unknown("Raffard: hand identities incomplete")]
                : Bound(SelectOptions(id, "bronze hand unit", choices, candidate => candidate.InstanceId, Selection(id)?.TutorId)
                    .SelectMany(candidate => Play(spent.Note("Raffard plays " + Rule(candidate.CardId)?.Card.Name), candidate.InstanceId, side, depth, explicitSequenceStep: true)));
            return AfterOrderUsed(outputs, state, side, depth);
        }
        if (rule.Order.StartsWith("create-play:", StringComparison.Ordinal))
        {
            var location = Locate(spent.Position, id)!;
            return AfterOrderUsed(CreateAndPlay(spent, spentCard, rule.Order[12..], side, location.Row ?? BoardRow.Melee, depth), state, side, depth);
        }
        if (rule.Order == "boost-deck")
        {
            var deck = spent.Position.Zone(side, CardZone.Deck);
            var choices = deck.Cards.Where(candidate => Rule(candidate.CardId)?.Card.Kind == CardKind.Unit).ToArray();
            IEnumerable<LineState> outputs = choices.Length == 0
                ? deck.Complete ? [spent.Note(rule.Card.Name + ": no unit in deck")] : [spent.Unknown(rule.Card.Name + ": deck unit identities incomplete")]
                : SelectOptions(id, "deck target", choices, candidate => candidate.InstanceId, Selection(id)?.TargetId)
                    .Select(candidate => spent with { Position = Change(spent.Position, candidate with { Power = candidate.Power + (rule.OrderAmount ?? 0) }) });
            return AfterOrderUsed(outputs, state, side, depth);
        }
        if (rule.Order == "treant-boar")
        {
            var location = Locate(spent.Position, id)!;
            if (location.Row == BoardRow.Melee)
            {
                var moved = Move(spent, id, depth, side).Select(line => Find(line.Position, id) is { } self
                    ? Heal(line, id, Math.Max(0, (self.BasePower ?? self.Power ?? 0) - (self.Power ?? 0))) : line);
                return AfterOrderUsed(moved, state, side, depth);
            }
            var enemies = Board(spent.Position, Other(side)).Where(target => CanTarget(spent.Position, target, side, TargetSide.Enemy)).ToArray();
            var movedStates = Move(spent, id, depth, side);
            IEnumerable<LineState> outputs = enemies.Length == 0 ? movedStates : movedStates.SelectMany(line =>
                SelectOptions(id, "target", enemies.Where(target => Find(line.Position, target.InstanceId) is not null), target => target.InstanceId,
                    Selection(id)?.TargetId).SelectMany(target => Damage(line, target.InstanceId, rule.OrderAmount ?? 2, depth)));
            return AfterOrderUsed(outputs, state, side, depth);
        }
        if (rule.Order == "damsel")
        {
            var reducedPower = Math.Max(0, (card.Power ?? 0) / 2); var removed = (card.Power ?? 0) - reducedPower;
            var reduced = spent with { Position = Change(spent.Position, spentCard with { Power = reducedPower }) };
            var enemies = Board(reduced.Position, Other(side)).Where(target => CanTarget(reduced.Position, target, side, TargetSide.Enemy) && target.Power <= removed).ToArray();
            IEnumerable<LineState> outputs = enemies.Length == 0 ? [reduced] : SelectOptions(id, "target", enemies, target => target.InstanceId,
                Selection(id)?.TargetId).SelectMany(target => Destroy(reduced.Note("Cursed Damsel removes " + removed + " power"), target.InstanceId, false, depth));
            return AfterOrderUsed(outputs, state, side, depth);
        }
        if (rule.Order == "transfer-boost")
        {
            var amount = Math.Max(0, (card.Power ?? 0) - (card.BasePower ?? 0));
            var spentSelf = Find(spent.Position, id)!;
            var reset = spent with { Position = Change(spent.Position, spentSelf with { Power = spentSelf.BasePower }) };
            var transferTargets = Board(reset.Position, side).Where(target => target.InstanceId != id &&
                CanTarget(reset.Position, target, side, TargetSide.Allied)).ToArray();
            IEnumerable<LineState> outputs = amount == 0 || transferTargets.Length == 0 ? [spent] :
                SelectOptions(id, "target", transferTargets, target => target.InstanceId, Selection(id)?.TargetId)
                    .Select(target => Boost(reset.Note("Transfer " + amount + " boost"), target.InstanceId, amount));
            return AfterOrderUsed(outputs, state, side, depth);
        }
        if (rule.Order == "consume-vitality")
        {
            var vitalityTargets = Board(spent.Position, side).Where(target => target.Statuses!.Contains(CardStatus.Vitality) &&
                CanTarget(spent.Position, target, side, TargetSide.Allied)).ToArray();
            IEnumerable<LineState> outputs = vitalityTargets.Length == 0 ? [spent] : SelectOptions(id, "Vitality target", vitalityTargets,
                target => target.InstanceId, Selection(id)?.TargetId).Select(target =>
            {
                if (target.StatusTurns is null) return spent.Unknown("Naiad Fledgling: target Vitality duration unread");
                var turns = target.StatusTurns;
                var amount = turns.GetValueOrDefault(CardStatus.Vitality);
                var cleared = target with { Statuses = target.Statuses!.Remove(CardStatus.Vitality),
                    StatusTurns = turns.Remove(CardStatus.Vitality) };
                return Boost(spent with { Position = Change(spent.Position, cleared) }, id, amount);
            });
            return AfterOrderUsed(outputs, state, side, depth);
        }
        if (rule.Order == "transform-adept")
        {
            var adept = _names.TryGetValue("Griffin Witcher Adept", out var adeptId) ? Rule(adeptId) : null;
            if (adept is null) return [state.Unknown("Griffin Witcher Adept definition missing")];
            var witcherTargets = Board(spent.Position, side).Where(target => target.InstanceId != id &&
                Rule(target.CardId)?.Card.HasCategory("Witcher") == true && CanTarget(spent.Position, target, side, TargetSide.Allied)).ToArray();
            IEnumerable<LineState> outputs = witcherTargets.Length == 0 ? [spent] : SelectOptions(id, "Witcher target", witcherTargets,
                target => target.InstanceId, Selection(id)?.TargetId).Select(target => TransformTo(spent, target.InstanceId, adept));
            return AfterOrderUsed(outputs, state, side, depth);
        }
        if (rule.Order.StartsWith("summon-grave:", StringComparison.Ordinal))
        {
            var recipe = rule.Order[13..].Split(';').ToHashSet(StringComparer.OrdinalIgnoreCase);
            var grave = spent.Position.Zone(side, CardZone.Graveyard);
            var graveTargets = grave.Cards.Where(target => Rule(target.CardId) is { } candidate &&
                (!recipe.Contains("unit") || candidate.Card.Kind == CardKind.Unit) &&
                (!recipe.Contains("bronze") || !candidate.Card.IsGold) &&
                (!recipe.Any(value => value.StartsWith("category:", StringComparison.OrdinalIgnoreCase)) ||
                    candidate.Card.HasCategory(recipe.First(value => value.StartsWith("category:", StringComparison.OrdinalIgnoreCase))[9..]))).ToArray();
            IEnumerable<LineState> outputs = graveTargets.Length == 0 ?
                grave.Complete ? [spent] : [spent.Unknown(rule.Card.Name + ": graveyard identities incomplete")] :
                SelectOptions(id, "graveyard target", graveTargets, target => target.InstanceId, Selection(id)?.TargetId)
                    .SelectMany(target => Summon(spent, target.InstanceId, side, zone.Row!.Value, depth)
                        .Select(line => recipe.Contains("doomed") ? Status(line, target.InstanceId, CardStatus.Doomed) : line));
            return AfterOrderUsed(outputs, state, side, depth);
        }
        if (rule.Order == "pavko")
        {
            var amount = Board(spent.Position, side).All(unit => Rule(unit.CardId)?.Card.Faction != "Neutral") ? 2 : 1;
            var pavkoTargets = Board(spent.Position).Where(target => target.InstanceId != id &&
                CanTarget(spent.Position, target, side, TargetSide.Any)).ToArray();
            IEnumerable<LineState> outputs = pavkoTargets.Length == 0 ? [spent] : SelectOptions(id, "target", pavkoTargets,
                target => target.InstanceId, Selection(id)?.TargetId).SelectMany(target => Damage(spent, target.InstanceId, amount, depth));
            return AfterOrderUsed(outputs, state, side, depth);
        }
        if (rule.Order == "priscilla")
        {
            var inspired = (card.Power ?? 0) > (card.BasePower ?? 0);
            var priscillaTargets = Board(spent.Position, side).Where(target => target.InstanceId != id &&
                CanTarget(spent.Position, target, side, TargetSide.Allied)).ToArray();
            IEnumerable<LineState> outputs = priscillaTargets.Length == 0 ? [spent] : SelectOptions(id, "target", priscillaTargets,
                target => target.InstanceId, Selection(id)?.TargetId).Select(target =>
            {
                var affected = inspired ? Boost(spent.Note("Priscilla Inspired"), target.InstanceId, 4) :
                    Heal(spent.Note("Priscilla heals"), target.InstanceId, 4);
                var latest = Find(affected.Position, target.InstanceId)!;
                return affected with { Position = Change(affected.Position, latest with { Cooldown = 0 }) };
            });
            return AfterOrderUsed(outputs, state, side, depth);
        }
        if (rule.Order == "frigate")
        {
            var cooldown = IsSoldierCrewed(spent.Position, id) ? 1 : rule.OrderCooldown;
            var frigate = Find(spent.Position, id)!;
            var prepared = spent with { Position = Change(spent.Position, frigate with { Cooldown = cooldown }) };
            return AfterOrderUsed(Spawn(prepared.Note("Kerack Frigate spawns Volunteer"), _names.GetValueOrDefault("Volunteer"),
                side, zone.Row!.Value, depth), state, side, depth);
        }
        if (rule.Order.StartsWith("spawn-play:", StringComparison.Ordinal))
            return AfterOrderUsed(SpawnAndPlay(spent, rule.Order[11..], side, depth), state, side, depth);
        if (rule.Order == "move") return AfterOrderUsed(Move(spent, id, depth, side), state, side, depth);
        if (rule.Order == "schirru") return AfterOrderUsed(Sequential([spent], Board(spent.Position).Where(unit => unit.InstanceId != id &&
                Rule(unit.CardId)?.Card.Kind == CardKind.Unit && unit.Power == card.Power).Select(unit => unit.InstanceId), (s, victim) => Destroy(s, victim, false, depth)), state, side, depth);
        if (rule.Order == "tempering") return AfterOrderUsed(SpawnAndPlay(spent, "Tempering", side, depth), state, side, depth);
        if (rule.Order.StartsWith("transform:", StringComparison.Ordinal)) return AfterOrderUsed([Transform(spent, id, rule.Order[10..])], state, side, depth);
        var targets = Board(state.Position).Where(target => target.InstanceId != id && CanTarget(state.Position, target, side,
            rule.Order is "consume" or "consume-dominance" or "deathwish" ? TargetSide.Allied : rule.Target))
            .Where(target => rule.Order != "deathwish" || Rule(target.CardId) is { Card.IsGold: false, Deathwish: not null }).ToArray();
        var resolved = SelectOptions(id, "target", targets, target => target.InstanceId, Selection(id)?.TargetId).SelectMany(target =>
        {
            var s = spent.Note("Order target #" + target.InstanceId);
            return rule.Order switch
            {
                "damage" => Damage(s, target.InstanceId, rule.OrderAmount ?? rule.Amount, depth),
                "barricade-damage" => Damage(s, target.InstanceId, 3, depth),
                "boost" => [Boost(s, target.InstanceId, rule.OrderAmount ?? rule.Amount)],
                "lock" => [Status(s, target.InstanceId, CardStatus.Locked)],
                "purify" => [s with { Position = Change(s.Position, target with { Statuses = ImmutableHashSet<CardStatus>.Empty,
                    StatusTurns = ImmutableDictionary<CardStatus, int>.Empty }) }],
                "poison" => ApplyPoison(s, target.InstanceId, depth),
                "baron-reset" => BaronReset(s, id, target.InstanceId),
                "miner" => [AddArmor(Boost(s, target.InstanceId, 2), target.InstanceId, Rule(target.CardId)?.Card.HasCategory("Dwarf") == true ? 4 : 0)],
                "consume" or "consume-dominance" => Consume(s, id, target.InstanceId, depth),
                "deathwish" => Deathwish(s, target, side, Locate(s.Position, target.InstanceId)!.Row!.Value, depth),
                _ => [s.Unknown("Unmodeled Order")]
            };
        });
        return AfterOrderUsed(resolved, state, side, depth);
    }

    private IEnumerable<LineState> RecordedEffect(LineState state, PositionCard played, PlayRule rule, PlayerSide side, BoardRow row, GamePosition before, int depth)
    {
        if (rule.Effect == PlayEffect.Artorius)
        {
            var own = side == PlayerSide.User ? before.User : before.Opponent;
            if (own.StartingDeckIds is null) return [state.Unknown("Artorius starting-deck identities unknown")];
            var eligible = own.StartingDeckIds.Select(Rule).Where(r => r is { Card.Kind: CardKind.Unit, Card.IsGold: false }).ToArray();
            return eligible.Length == 0 ? [state] : SelectOptions(played.InstanceId, "created starting-deck unit", eligible,
                r => r!.Card.Id, Selection(played.InstanceId)?.CreatedCardId).SelectMany(r => SpawnAndPlay(state with { Random = true }, r!.Card.Name, side, depth, basePower: 1));
        }
        if (rule.Effect == PlayEffect.Buhurt)
        {
            var enemies = Board(state.Position, Other(side)).Where(c => CanTarget(state.Position, c, side, TargetSide.Enemy)).ToArray();
            var allies = Board(state.Position, side).Where(c => CanTarget(state.Position, c, side, TargetSide.Allied)).ToArray();
            if (enemies.Length == 0 || allies.Length == 0) return [state.Unknown("Buhurt missing-target resolution not validated")];
            var first = enemies.Length == 0 ? new[] { state } : SelectOptions(played.InstanceId, "enemy boost", enemies,
                c => c.InstanceId, Selection(played.InstanceId)?.TargetId).Select(c =>
                    Boost(TargetedBySpecial(state, played, side, c.InstanceId), c.InstanceId, 3));
            return allies.Length == 0 ? first : first.SelectMany(s => SelectOptions(played.InstanceId, "allied boost", allies,
                c => c.InstanceId, Selection(played.InstanceId)?.AlliedTargetId).Select(c =>
                    Boost(TargetedBySpecial(s, played, side, c.InstanceId), c.InstanceId, 9)));
        }
        if (rule.Effect == PlayEffect.Braenn)
            return [state with { Position = Change(state.Position, played with { Cooldown = Board(before, side).Count(card => Rule(card.CardId)?.Card.HasCategory("Dryad") == true) >= 2 ? 0 : 1 }) }];
        if (rule.Effect == PlayEffect.Agitator)
        {
            var targets = state.Position.Zone(side, CardZone.Hand).Cards.Where(card => Rule(card.CardId)?.Card.HasCategory("Dwarf") == true).ToArray();
            if (targets.Length > 2) return [state.Unknown("Agitator hand armor targets not supplied")];
            foreach (var target in targets) state = AddArmor(state, target.InstanceId, 1);
            return [state];
        }
        if (rule.Effect == PlayEffect.NekkerWarrior)
        {
            if (Board(before, side).Any(card => !card.Statuses!.Contains(CardStatus.Locked) && (Rule(card.CardId)!.Thrive + card.ExtraThrive) > 0 && card.Power < played.Power)) return [state];
            return Damage(state, played.InstanceId, 3, depth).Select(output => Find(output.Position, played.InstanceId) is { } card && Locate(output.Position, played.InstanceId)?.Zone == CardZone.Board
                ? output with { Position = Change(output.Position, card with { ExtraThrive = card.ExtraThrive + 1, Statuses = card.Statuses!.Add(CardStatus.Infused) }) } : output);
        }
        if (rule.Effect == PlayEffect.Skirmisher)
        {
            if (row != BoardRow.Melee) return [state];
            var targets = Board(state.Position, Other(side)).Where(card => CanTarget(state.Position, card, side, TargetSide.Enemy)).ToArray();
            return targets.Length == 0 ? [state] : SelectOptions(played.InstanceId, "target", targets, card => card.InstanceId, Selection(played.InstanceId)?.TargetId)
                .SelectMany(target => Damage(state, target.InstanceId, 3, depth).Select(output => Locate(output.Position, target.InstanceId)?.Zone == CardZone.Board ? AddArmor(output, played.InstanceId, 1) : output));
        }
        if (rule.Effect == PlayEffect.Oakcritters)
        {
            var devotion = (side == PlayerSide.User ? state.Position.User : state.Position.Opponent).Devotion;
            if (devotion is null) return [state.Unknown("Oakcritters Devotion state unknown")];
            IEnumerable<LineState> states = [state];
            if (row == BoardRow.Melee || devotion == true)
            {
                var targets = Board(state.Position, Other(side)).Where(card => CanTarget(state.Position, card, side, TargetSide.Enemy)).ToArray();
                if (targets.Length > 0) states = SelectOptions(played.InstanceId, "target", targets, card => card.InstanceId, Selection(played.InstanceId)?.TargetId)
                    .Select(target => Duration(state, target.InstanceId, CardStatus.Bleeding, 4));
            }
            return row == BoardRow.Ranged || devotion == true ? states.SelectMany(s => Spawn(s, played.CardId, side, row, depth)) : states;
        }
        if (rule.Effect == PlayEffect.BackupPlan)
        {
            var enemy = side == PlayerSide.User ? before.Opponent : before.User;
            if (enemy.LastPlayedUnitId is null) return [state.Unknown("Last opponent-played unit unknown")];
            var own = side == PlayerSide.User ? before.User : before.Opponent;
            var eligible = _rules.Values.Where(item => item.Card is { Kind: CardKind.Unit, IsGold: false, Faction: "Scoia'tael" } &&
                item.Card.HasCategory("Elf") && item.Card.AbilityText?.Contains("Deploy") == true && own.StartingDeckIds?.Contains(item.Card.Id) != true).ToArray();
            var selected = SelectOptions(played.InstanceId, "created card", eligible, item => item.Card.Id, Selection(played.InstanceId)?.CreatedCardId);
            IEnumerable<LineState> states = Damage(state, enemy.LastPlayedUnitId, 2, depth);
            if (own.StartingDeckIds is null && _selections is null) states = states.Select(s => s.Unknown("Backup Plan starting-deck exclusion set unknown"));
            return states.SelectMany(s => selected.SelectMany(item => SpawnAndPlay(s with { Random = true }, item.Card.Name, side, depth)));
        }
        return [state.Unknown("Unsupported recorded effect")];
    }
}
