using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

/// <summary>
/// A deliberately small internal player for card-controlled choices.  It does not estimate a child
/// card from printed text: every candidate is played through the normal engine, including Deploy,
/// Assimilate, Symbiosis, scenarios, Deathblow, graveyard state and board listeners.  Player choices
/// are collapsed to the highest supported reach endpoint; hidden information and random outcomes stay
/// branched so the caller can expose a range.
/// </summary>
public sealed partial class TacticalPlayEngine
{
    private IEnumerable<LineState> GreedyAgentEffect(LineState state, PositionCard played, PlayRule rule,
        PlayerSide side, BoardRow row, GamePosition before, int depth) => rule.Argument switch
    {
        "braathens" => AgentBraathens(state, played, side, depth),
        "vivaldi-bank" => AgentVivaldiBank(state, played, side, depth),
        "war-of-clans" => AgentWarOfClans(state, played, rule, side, depth),
        "bride-of-the-sea" => AgentBrideOfTheSea(state, played, side, depth),
        "crow-messenger" => AgentCrowMessenger(state, played, side, row, depth),
        "brokilon-sentinel" => AgentBrokilonSentinel(state, played, rule, side, row, depth),
        "lydia" => AgentLydia(state, played, side, row, depth),
        "tears-of-siren" => AgentTearsOfSiren(state, played, side, depth),
        "hawker-healer" => Effect(state, played, rule with
            { Effect = row == BoardRow.Melee ? PlayEffect.Boost : PlayEffect.Heal, Amount = row == BoardRow.Melee ? 2 : 4 },
            side, row, before, depth),
        "damage-veil" or "move-unit" or "lone-row-damage" => AgentPrimitiveTargets(state, played, rule, side, depth),
        { } value when value.StartsWith("hand-play-draw:", StringComparison.Ordinal) =>
            AgentHandPlayDraw(state, played, side, value[15..], depth),
        _ => [state.Unknown(rule.Card.Name + ": missing greedy-agent recipe")]
    };

    private IEnumerable<LineState> AgentChoose(LineState origin, IEnumerable<LineState> candidates,
        PlayerSide side, string choice)
    {
        // Replay uses SelectOptions at each decision below. Never optimize a recorded player's choice.
        if (_selections is not null) return Bound(candidates);
        // Compare against the root snapshot so spending newly gained Profit cannot be mistaken for
        // free access: remaining new Coins still contribute to the caller's total reach objective.
        int Objective(LineState option) => ReachSwing(_agentObjectivePosition ?? origin.Position, option.Position, side);
        // Stream endpoints. Retain at most four boards, not every recursive target/placement board.
        LineState? bestAny = null, lowAny = null, bestSupported = null, lowSupported = null;
        var anyRandom = false; var supportedRandom = false; var excluded = false;
        void Include(LineState option, ref LineState? best, ref LineState? low)
        {
            if (best is null || Objective(option) > Objective(best) ||
                Objective(option) == Objective(best) && option.Missing.Count < best.Missing.Count) best = option;
            if (low is null || Objective(option) < Objective(low)) low = option;
        }
        foreach (var option in Bound(candidates))
        {
            Include(option, ref bestAny, ref lowAny); anyRandom |= option.Random;
            if (option.Missing.SetEquals(origin.Missing))
            { Include(option, ref bestSupported, ref lowSupported); supportedRandom |= option.Random; }
            else excluded = true;
        }
        if (bestAny is null) return [origin.Note("Agent found no legal " + choice)];
        var best = bestSupported ?? bestAny;
        var low = bestSupported is not null ? lowSupported! : lowAny!;
        var swing = ReachSwing(origin.Position, best.Position, side);
        if (bestSupported is not null && excluded)
            best = Approximate(best, $"{choice}: unsupported alternatives excluded from greedy policy");
        best = best.Note($"[agent] {choice}: greedy reach {swing:+#;-#;0}");
        if (!(bestSupported is not null ? supportedRandom : anyRandom)) return [best];
        if (Objective(low) == Objective(best)) return [best];
        // Random branches can occur inside another player-controlled choice. Retain a conservative
        // envelope instead of presenting favorable randomness as guaranteed points.
        return [Approximate(low, choice + ": conservative random lower endpoint"),
            Approximate(best, choice + ": favorable random upper endpoint")];
    }

    private IEnumerable<LineState> AgentPrimitiveTargets(LineState state, PositionCard played, PlayRule rule,
        PlayerSide side, int depth)
    {
        var targets = Board(state.Position).Where(card => card.InstanceId != played.InstanceId &&
            CanTarget(state.Position, card, side, rule.Target)).Where(card => rule.Argument != "lone-row-damage" ||
                Locate(state.Position, card.InstanceId)!.Cards.Count(unit => Rule(unit.CardId)?.Card.Kind == CardKind.Unit) == 1).ToArray();
        if (targets.Length == 0) return [state.Note(rule.Card.Name + ": no legal target")];
        return Bound(SelectOptions(played.InstanceId, "target", targets, card => card.InstanceId,
            Selection(played.InstanceId)?.TargetId).SelectMany(target =>
        {
            var line = TargetedBySpecial(state.Note(rule.Card.Name + " targets #" + target.InstanceId), played, side, target.InstanceId);
            if (rule.Argument == "move-unit") return Move(line, target.InstanceId, depth, side);
            return Damage(line, target.InstanceId, rule.Amount, depth).Select(damaged =>
                rule.Argument == "damage-veil" && Locate(damaged.Position, target.InstanceId)?.Zone == CardZone.Board
                    ? Status(damaged, target.InstanceId, CardStatus.Veil) : damaged);
        }));
    }

    private IEnumerable<LineState> AgentTearsOfSiren(LineState state, PositionCard played, PlayerSide side, int depth)
    {
        var origin = state.Position.RowEffects is null ? Approximate(state, "Tears of Siren assumes unobserved weather is inactive") : state;
        return Bound(SelectOptions(played.InstanceId, "weather row", Enum.GetValues<BoardRow>(), value => value.ToString(),
            Selection(played.InstanceId)?.Row?.ToString()).SelectMany(row =>
        {
            var effects = origin.Position.RowEffects ?? [];
            var existing = effects.FirstOrDefault(effect => effect.AffectedSide == Other(side) && effect.Row == row);
            var duration = 2 + (existing?.Name == "Rain" ? existing.RemainingTurns ?? 0 : 0);
            var line = existing is { Name: "Rain", RemainingTurns: null }
                ? Approximate(origin, "Tears of Siren existing Rain duration unread") : origin;
            if (existing is not null) effects = effects.Remove(existing);
            effects = effects.Add(new(Other(side), row, "Rain", duration));
            line = line with { Position = line.Position with { RowEffects = effects, RowEffectsKnownInactive = false } };
            return Spawn(line.Note("Tears of Siren adds Rain and opposite-row Siren"),
                _names.GetValueOrDefault("Deafening Siren"), side, row, depth);
        }));
    }

    private IEnumerable<LineState> AgentCreatedPool(LineState state, PositionCard played,
        IEnumerable<PlayRule> choices, PlayerSide side, int depth, string label)
    {
        var pool = SelectOptions(played.InstanceId, "created card", choices, candidate => candidate.Card.Id,
            Selection(played.InstanceId)?.CreatedCardId).ToArray();
        return Bound(pool.SelectMany(candidate => AgentChoose(state,
            SpawnAndPlay(state.Note(label + " considers " + candidate.Card.Name), candidate.Card.Name, side, depth,
                recursiveGeneratedChoice: true), side, label + " line for " + candidate.Card.Name))
            .Select(line => pool.Length > 1 ? Approximate(line with { Random = true },
                label + ": envelope of possible created-card choices") : line));
    }

    private IEnumerable<LineState> AgentBraathens(LineState state, PositionCard played,
        PlayerSide side, int depth)
    {
        var resources = side == PlayerSide.User ? state.Position.User : state.Position.Opponent;
        if (resources.StartingDeckIds is null)
            return [Approximate(state, "Braathens creation pool unavailable without inferred starting deck")];
        var choices = resources.StartingDeckIds.Select(Rule).Where(candidate => candidate is
            { Card.Kind: CardKind.Unit, Card.IsGold: false, Disloyal: true }).DistinctBy(candidate => candidate!.Card.Id).ToArray();
        if (choices.Length == 0) return [state.Note("Braathens: no bronze Disloyal unit in inferred starting deck")];
        return AgentCreatedPool(state, played, choices.Select(candidate => candidate!), side, depth, "Braathens");
    }

    private IEnumerable<LineState> AgentVivaldiBank(LineState state, PositionCard played,
        PlayerSide side, int depth)
    {
        var resources = side == PlayerSide.User ? state.Position.User : state.Position.Opponent;
        if (resources.Coins is null) return [state.Unknown("Vivaldi Bank: Coin count unread")];
        var deck = state.Position.Zone(side, CardZone.Deck);
        var visible = deck.Cards.Take(Math.Min(deck.Cards.Length, resources.Coins.Value + 1)).ToArray();
        if (visible.Length == 0)
            return [deck.Complete ? state.Note("Vivaldi Bank: empty deck") :
                Approximate(state, "Vivaldi Bank inferred top-card identities unavailable")];
        var choices = SelectOptions(played.InstanceId, "Bank card", visible, card => card.InstanceId,
            Selection(played.InstanceId)?.TutorId).SelectMany(card =>
        {
            var distance = Array.FindIndex(visible, item => item.InstanceId == card.InstanceId);
            var paid = SetCoins(state.Note($"Vivaldi Bank considers #{distance + 1}: {Rule(card.CardId)?.Card.Name}"),
                side, resources.Coins.Value - distance);
            return Play(paid, card.InstanceId, side, depth, explicitSequenceStep: true)
                .Select(line => deck.Complete ? line : Approximate(line, "Vivaldi Bank uses inferred deck order"));
        });
        return AgentChoose(state, choices, side, "Vivaldi Bank card");
    }

    private IEnumerable<LineState> AgentWarOfClans(LineState state, PositionCard played, PlayRule rule,
        PlayerSide side, int depth)
    {
        var enemies = Board(state.Position, Other(side)).Where(card => CanTarget(state.Position, card, side, TargetSide.Enemy)).ToArray();
        if (enemies.Length == 0) return [state.Note("War of Clans: no legal damage target")];
        var resources = side == PlayerSide.User ? state.Position.User : state.Position.Opponent;
        var lines = SelectOptions(played.InstanceId, "target", enemies, card => card.InstanceId,
            Selection(played.InstanceId)?.TargetId).SelectMany(target =>
        {
            var damage = RaidDamage(state, side, rule, rule.Amount);
            var targeted = TargetedBySpecial(state.Note("War of Clans considers " + Rule(target.CardId)?.Card.Name),
                played, side, target.InstanceId);
            return Damage(targeted, target.InstanceId, damage, depth)
                .SelectMany(damaged =>
                {
                    var deathblow = Locate(damaged.Position, target.InstanceId)?.Zone != CardZone.Board;
                    if (!deathblow && resources.Devotion == false) return [damaged];
                    var grave = damaged.Position.Zone(side, CardZone.Graveyard);
                    var warriors = grave.Cards.Where(card => Rule(card.CardId) is
                        { Card.Kind: CardKind.Unit, Card.IsGold: false, Card.Provision: 4 } candidate &&
                        candidate.Card.HasCategory("Warrior")).ToArray();
                    if (warriors.Length == 0)
                        return [grave.Complete ? damaged.Note("War of Clans: no 4-provision Warrior") :
                            Approximate(damaged, "War of Clans graveyard inventory incomplete")];
                    var origin = !deathblow && resources.Devotion is null
                        ? Approximate(damaged, "War of Clans assumes Devotion") : damaged;
                    return AgentChoose(origin, SelectOptions(played.InstanceId, "graveyard Warrior", warriors,
                        card => card.InstanceId, Selection(played.InstanceId)?.TutorId).SelectMany(warrior =>
                    {
                        var doomed = warrior with { Statuses = (warrior.Statuses ?? ImmutableHashSet<CardStatus>.Empty).Add(CardStatus.Doomed) };
                        var staged = origin with { Position = Change(origin.Position, doomed) };
                        return Play(staged.Note("War of Clans considers " + Rule(warrior.CardId)?.Card.Name),
                            warrior.InstanceId, side, depth, explicitSequenceStep: true);
                    }), side, "War of Clans graveyard Warrior");
                });
        });
        return AgentChoose(state, lines, side, "War of Clans target");
    }

    private IEnumerable<LineState> AgentBrideOfTheSea(LineState state, PositionCard played,
        PlayerSide side, int depth)
    {
        var weather = state.Position.RowEffects;
        var duration = weather?.Where(effect => effect.AffectedSide == Other(side) &&
                effect.Name is "Rain" or "Storm")
            .Sum(effect => effect.RemainingTurns ?? 0) ?? 0;
        var origin = weather is null && !state.Position.RowEffectsKnownInactive
            ? Approximate(state, "Bride of the Sea assumes unread Rain/Storm duration is zero")
            : weather?.Any(effect => effect.AffectedSide == Other(side) && effect.Name is "Rain" or "Storm" &&
                effect.RemainingTurns is null) == true
                ? Approximate(state, "Bride of the Sea excludes unread weather duration") : state;
        var maximumProvision = 4 + duration;
        var grave = origin.Position.Zone(side, CardZone.Graveyard);
        var choices = grave.Cards.Where(card => Rule(card.CardId) is { Card.Kind: CardKind.Special } candidate &&
            candidate.Card.HasCategory("Alchemy") && candidate.Card.Provision <= maximumProvision).ToArray();
        if (choices.Length == 0) return [grave.Complete ? origin.Note("Bride of the Sea: no eligible Alchemy") :
            Approximate(origin, "Bride of the Sea graveyard inventory incomplete")];
        return AgentChoose(origin, SelectOptions(played.InstanceId, "graveyard Alchemy", choices, card => card.InstanceId,
            Selection(played.InstanceId)?.TutorId).SelectMany(card => Play(origin.Note("Bride considers " + Rule(card.CardId)?.Card.Name),
            card.InstanceId, side, depth, explicitSequenceStep: true)), side, "Bride of the Sea Alchemy");
    }

    private IEnumerable<LineState> AgentCrowMessenger(LineState state, PositionCard played,
        PlayerSide side, BoardRow row, int depth)
    {
        var graveIds = state.Position.Zone(side, CardZone.Graveyard).Cards.Where(card => card.CardId == played.CardId)
            .Select(card => card.InstanceId).ToArray();
        var graveLines = Sequential([state], graveIds, (line, id) => Summon(line, id, side, row, depth));
        var hand = state.Position.Zone(side, CardZone.Hand);
        var alchemyKnown = hand.Cards.Any(card => Rule(card.CardId)?.Card.HasCategory("Alchemy") == true);
        if (alchemyKnown)
            return graveLines.SelectMany(line => SummonAll(line.Note("Crow Messenger: Alchemy in hand"), side, played.CardId, row, depth));
        if (hand.Complete) return graveLines;
        // The hand detector may know only a subset.  Keep both condition outcomes so reach exposes min/max.
        return Bound(graveLines.SelectMany(line => new[] { line.Note("Crow Messenger: no visible Alchemy"),
            Approximate(line, "Crow Messenger upper range assumes hidden Alchemy in hand") }
                .SelectMany((branch, index) => index == 0 ? [branch] : SummonAll(branch, side, played.CardId, row, depth))));
    }

    private IEnumerable<LineState> AgentHandPlayDraw(LineState state, PositionCard played,
        PlayerSide side, string category, int depth)
    {
        var hand = state.Position.Zone(side, CardZone.Hand);
        var choices = hand.Cards.Where(card => card.InstanceId != played.InstanceId &&
            Rule(card.CardId)?.Card.HasCategory(category) == true).ToArray();
        if (choices.Length == 0) return [hand.Complete ? state.Note($"No {category} in hand") :
            Approximate(state, $"No visible {category}; partial hand may contain one")];
        return AgentChoose(state, SelectOptions(played.InstanceId, "hand card", choices, card => card.InstanceId,
            Selection(played.InstanceId)?.TutorId).SelectMany(card =>
            Play(state.Note($"Agent considers {category} {Rule(card.CardId)?.Card.Name}"), card.InstanceId, side, depth,
                    explicitSequenceStep: true)
                .Select(line => AgentDrawTop(line, side))), side, $"{category} from hand");
    }

    private LineState AgentDrawTop(LineState state, PlayerSide side)
    {
        var deck = state.Position.Zone(side, CardZone.Deck);
        if (deck.Cards.Length == 0)
            return deck.Complete || deck.TotalCount == 0 ? state.Note("Draw from empty deck has no draw trigger") :
                DrawTriggers(Approximate(state, "Draw identity unavailable from inferred deck"), side, 1);
        var drawn = deck.Cards[0];
        var position = Remove(state.Position, drawn.InstanceId);
        var hand = position.Zone(side, CardZone.Hand);
        state = state with { Position = Set(position, hand, hand.Cards.Add(drawn)) };
        return DrawTriggers((deck.Complete ? state : Approximate(state, "Draw uses inferred top-deck identity"))
            .Note("Draw " + Rule(drawn.CardId)?.Card.Name), side, 1);
    }

    private IEnumerable<LineState> AgentBrokilonSentinel(LineState state, PositionCard played, PlayRule rule,
        PlayerSide side, BoardRow row, int depth)
    {
        var enemies = Board(state.Position, Other(side)).Where(card => CanTarget(state.Position, card, side, TargetSide.Enemy)).ToArray();
        if (enemies.Length == 0) return [state.Note("Brokilon Sentinel: no legal target")];
        var lines = SelectOptions(played.InstanceId, "target", enemies, card => card.InstanceId,
            Selection(played.InstanceId)?.TargetId).SelectMany(target => Damage(state.Note("Brokilon Sentinel considers " + Rule(target.CardId)?.Card.Name),
            target.InstanceId, rule.Amount, depth).SelectMany(damaged =>
                Locate(damaged.Position, target.InstanceId)?.Zone != CardZone.Board
                    ? SummonAll(damaged.Note("Brokilon Sentinel Deathblow"), side, played.CardId, row, depth)
                    : [damaged]));
        return AgentChoose(state, lines, side, "Brokilon Sentinel target");
    }

    private IEnumerable<LineState> AgentLydia(LineState state, PositionCard played,
        PlayerSide side, BoardRow row, int depth)
    {
        if (row == BoardRow.Ranged)
        {
            var grave = state.Position.Zone(Other(side), CardZone.Graveyard);
            var choices = grave.Cards.Where(card => Rule(card.CardId) is
                { Card.Kind: CardKind.Special, Card.IsGold: false }).ToArray();
            if (choices.Length == 0) return [grave.Complete ? state.Note("Lydia: no bronze special in opponent graveyard") :
                Approximate(state, "Lydia opponent graveyard inventory incomplete")];
            return AgentChoose(state, SelectOptions(played.InstanceId, "opponent graveyard special", choices,
                card => card.InstanceId, Selection(played.InstanceId)?.TutorId).SelectMany(card =>
            {
                // Original is relative to the acting player's starting deck. A borrowed enemy card
                // must feed this side's Assimilate, even if it was original for the previous owner.
                var borrowed = state with { Position = Change(state.Position, card with { Original = false }) };
                return Play(borrowed.Note("Lydia considers graveyard " + Rule(card.CardId)?.Card.Name),
                    card.InstanceId, side, depth, explicitSequenceStep: true);
            }), side, "Lydia graveyard special");
        }

        var faction = AgentFaction(state.Position, Other(side));
        if (faction is null) return [Approximate(state, "Lydia opponent faction unavailable")];
        var rules = _rules.Values.Where(candidate => candidate.Card is
            { Kind: CardKind.Special, IsGold: false, CanBeInStartingDeck: true } definition &&
            (definition.Faction.Equals(faction, StringComparison.OrdinalIgnoreCase) ||
             definition.SecondaryFactions.Contains(faction))).ToArray();
        if (rules.Length == 0) return [state.Note("Lydia: opponent faction has no bronze special")];
        var bounded = rules.OrderByDescending(candidate => candidate.Card.Provision).Take(32).ToArray();
        var origin = bounded.Length < rules.Length ? Approximate(state, "Lydia creation pool pruned to 32 provision-ranked cards") : state;
        return AgentCreatedPool(origin, played, bounded, side, depth, "Lydia");
    }

    private string? AgentFaction(GamePosition position, PlayerSide side)
    {
        var resources = side == PlayerSide.User ? position.User : position.Opponent;
        var observed = position.Zones.Where(zone => zone.Side == side).SelectMany(zone => zone.Cards.Select(card => Rule(card.CardId)?.Card.Faction));
        var inferred = (resources.StartingDeckIds ?? []).Select(id => Rule(id)?.Card.Faction);
        return observed.Concat(inferred).Where(faction => faction is not null && faction != "Neutral")
            .GroupBy(faction => faction!, StringComparer.OrdinalIgnoreCase).OrderByDescending(group => group.Count())
            .Select(group => group.Key).FirstOrDefault();
    }

    private IEnumerable<LineState> SapperBombReaction(LineState state, PositionCard played,
        PlayerSide side, GamePosition before, int depth)
    {
        if (Rule(played.CardId)?.Card.HasCategory("Bomb") != true) return [state];
        IEnumerable<LineState> states = [state];
        foreach (var sapperId in Board(before, side).Where(card => Rule(card.CardId)?.Reaction == "bomb-agent")
                     .Select(card => card.InstanceId).ToArray())
        {
            var listener = sapperId;
            states = Bound(states.SelectMany(line =>
            {
                var sapper = Find(line.Position, listener);
                if (sapper is null || Locate(line.Position, listener)?.Zone != CardZone.Board ||
                    sapper.Statuses?.Contains(CardStatus.Locked) == true || sapper.Armor is not > 0) return [line];
                var enemies = Board(line.Position, Other(side)).Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                if (enemies.Length == 0) return [line];
                return SelectOptions(listener, "Bomb random target", enemies, card => card.InstanceId,
                    Selection(listener)?.RandomTargetId).SelectMany(target => Damage((line with { Random = line.Random || enemies.Length > 1 })
                    .Note("Sapper Barricade Bomb damage"), target.InstanceId, 1, depth));
            })).ToArray();
        }
        return states;
    }

    private IEnumerable<LineState> MistressEndTurn(LineState state, string id, PlayerSide side, int depth)
    {
        var card = Find(state.Position, id); var zone = Locate(state.Position, id);
        if (card is null || zone is not { Zone: CardZone.Board, Row: { } row }) return [state];
        var sabbath = Board(state.Position, side).Where(unit => Rule(unit.CardId)?.Card.Kind == CardKind.Unit)
            .Sum(unit => unit.Power ?? 0) >= 25;
        var reaction = Rule(card.CardId)?.Reaction;
        if (reaction == "bloody-mistress")
        {
            if (!sabbath) return [state];
            var cards = state.Position.Zone(side, CardZone.Board, row).Cards;
            var slot = Array.FindIndex(cards.ToArray(), unit => unit.InstanceId == id);
            IEnumerable<LineState> lines = SpawnAt(state.Note("Bloody Mistress Sabbath"), _names.GetValueOrDefault("Gernichora's Fruit"),
                side, row, Math.Max(0, slot), depth);
            lines = lines.SelectMany(line =>
            {
                var current = line.Position.Zone(side, CardZone.Board, row).Cards;
                var currentSlot = Array.FindIndex(current.ToArray(), unit => unit.InstanceId == id);
                return SpawnAt(line, _names.GetValueOrDefault("Gernichora's Fruit"), side, row, currentSlot + 1, depth);
            });
            return lines.Select(line => TransformPreservingPower(line, id, "Gernichora"));
        }
        if (reaction != "gernichora") return [state];
        if (!sabbath) return [TransformPreservingPower(state.Note("Gernichora loses Sabbath"), id, "Bloody Mistress")];
        var fruits = Board(state.Position, side).Count(unit => Rule(unit.CardId)?.Card.Name == "Gernichora's Fruit");
        return [Boost(state.Note($"Gernichora Sabbath boosts for {fruits} Fruit"), id, fruits)];
    }

    private LineState TransformPreservingPower(LineState state, string id, string name)
    {
        var power = Find(state.Position, id)?.Power;
        var transformed = Transform(state, id, name);
        var card = Find(transformed.Position, id);
        return power is null || card is null ? transformed : transformed with
        {
            Position = Change(transformed.Position, card with { Power = power })
        };
    }
}
