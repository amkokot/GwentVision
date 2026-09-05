using System.Collections.Immutable;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

public sealed record LeaderProfile(string Id, string Effect, int Amount, int PrintedCharges, string Commitment, string? Token = null);
public sealed record LeaderSearchResult(PlaySearchResult Estimate, int ChargesUsed, int? ChargesAvailable, string Commitment);

public sealed partial class TacticalPlayEngine
{
    public LeaderProfile? Leader(string id)
    {
        var rule = Rule(id); if (rule is null) return null;
        var text = Regex.Replace(PlayRules.Normalize(rule.Card.AbilityText), @" This ability adds \d+ provisions to your deck's provisions? limit\.$", "");
        const string finite = "Finite match resource; save unused charges when a cheaper reply suffices.";
        return text switch
        {
            "Order: Move a unit to the other row. If it's an enemy, damage it by 1; if it's an ally, boost it by 3. Charges: 3" =>
                new(id, "guerilla", 3, 3, "Finite movement charges; boosts and any subsequent Order are evaluated separately."),
            "Order: Boost an allied unit by 3. If it's a non-Neutral unit, also give it Veil. Charges: 3" => new(id, "carapace", 3, 3, finite),
            "Order: Boost an allied unit by 2 and give it a Shield. Charges: 3" => new(id, "shieldwall", 2, 3, finite),
            "Order: Boost an allied unit by 2. If it's a non-Neutral unit, also give it Zeal. Charges: 3" => new(id, "zeal", 2, 3, "Charges also unlock orders; order value must be modeled separately."),
            "Order: Spawn an Elven Deadeye on an allied row. Charges: 3" => new(id, "spawn", 0, 3, finite, "Elven Deadeye"),
            "Order: Spawn a Drone on an allied row. Charges: 5 Whenever you play an Organic card, Spawn a Drone on a random allied row." => new(id, "spawn", 0, 5, finite, "Drone"),
            "Order: Spawn a Gernichora's Fruit on an allied row. At the start of your turn, if you do not control any Gernichora's Fruits, reset this ability." =>
                new(id, "spawn", 0, 1, "Renewable when no Fruit remains at your next turn start; lower commitment than a once-per-match leader.", "Gernichora's Fruit"),
            "Order: Spawn and play Woodland Spirit." => new(id, "play-token", 0, 1, "Whole once-per-match leader.", "Woodland Spirit"),
            "Order: Spawn and play Dana Méadbh." => new(id, "play-token", 0, 1, "Whole once-per-match leader; future Harmony growth is not immediate value.", "Dana Méadbh"),
            "Order: Spawn a Firesworn Zealot on an allied row and gain 1 Coin. Charges: 3" =>
                new(id, "spawn-coins", 1, 3, "Finite charges; each gained Coin is valued as +1 reach.", "Firesworn Zealot"),
            "Order: Give an enemy unit Bleeding (3). Charges: 3 Once all Charges are used up, Spawn an Ekimmara on a random allied row." =>
                new(id, "bleeding-token", 3, 3, "Final charge spawns Ekimmara. Bleeding on the enemy's later turn is not immediate damage.", "Ekimmara"),
            "Order: Lock an enemy unit and damage it by 3. Charges: 2" => new(id, "lock-damage", 3, 2, finite),
            "Order: Damage an enemy unit by 3. Charges: 2 Whenever an enemy unit becomes damaged, give all Pirates and Ships in your hand 1 Armor." =>
                new(id, "damage", 3, 2, "Finite charges; passive hand armor is not immediate scoreboard value."),
            "Symbiosis. Order: Give an allied unit Vitality (2). Charges: 3" =>
                new(id, "vitality", 2, 3, "Finite charges. Counts only this turn's Vitality tick, not all future ticks."),
            "Order: Gain 3 Coins. At the start of the round, refresh this ability. Your Hoards require 2 less Coins to trigger." =>
                new(id, "coins", 3, 1, "Refreshes each round; each gained Coin is valued as +1 reach."),
            "Order: Gain 1 Coin. Charges: 6 Whenever you play a Crime card, gain 1 Coin." =>
                new(id, "coins", 1, 6, "Finite charges; each gained Coin is valued as +1 reach."),
            "Order: Destroy an allied unit, then Spawn an Ekimmara on its row and boost it by the destroyed unit's power. Charges: 2" =>
                new(id, "hunger", 0, 2, "Finite consume charges; Deathwish chains and row space change their value.", "Ekimmara"),
            "Order: Damage an allied unit by 1. Charges: 5 Once all Charges are used up, Spawn a Bear Abomination on a random allied row." =>
                new(id, "ritual", 1, 5, "Final charge releases the bear; damaging your own units may cost points or trigger Deathwish.", "Bear Abomination"),
            "Order: Split 4 damage randomly between all enemy units, ignoring their Armor. Charges: 2" =>
                new(id, "flurry", 4, 2, "Finite charges; favorable random damage, not guaranteed damage."),
            _ => null
        };
    }
    private bool LeaderPassiveSupported(string id) => Leader(id) is not null;

    /// <summary>Standalone leader addition on this board; subtract ordinary end-turn points already in card estimates.
    /// Does not search card/leader combinations. Obvious supported leader reactions and new Vitality ticks are retained.</summary>
    public LeaderSearchResult LeaderContribution(GamePosition position, PlayerSide side, CancellationToken cancellationToken = default)
    {
        var full = MaximumLeader(position, side, cancellationToken: cancellationToken);
        if (full.Estimate.MaximumPoints is null) return full;
        var owner = side == PlayerSide.User ? position.User : position.Opponent;
        var baselinePosition = side == PlayerSide.User ? position with { User = owner with { LeaderCharges = 0 } }
            : position with { Opponent = owner with { LeaderCharges = 0 } };
        var baseline = MaximumLeader(baselinePosition, side, cancellationToken: cancellationToken).Estimate;
        if (baseline.MaximumPoints is not { } existing || baseline.FavorableRandomness)
            return full with { Estimate = full.Estimate with { MaximumPoints = null,
                Missing = full.Estimate.Missing.Concat(["Leader addition has an unresolved/random ordinary end-turn baseline"]).ToArray() } };
        return full with { Estimate = full.Estimate with { MaximumPoints = full.Estimate.MaximumPoints - existing,
            BestModeledPoints = full.Estimate.BestModeledPoints - existing,
            Assumptions = full.Estimate.Assumptions.Concat(["Standalone leader addition on the current reply board; card/leader synergies are not included."]).ToArray() } };
    }

    /// <summary>Searches zero through remaining charges, with target/row selection at each use.
    /// If a threshold is supplied, chooses the least charges that reach it. Not a complete interleaved order solver.</summary>
    public LeaderSearchResult MaximumLeader(GamePosition position, PlayerSide side, int? requiredSwing = null,
        int branchLimit = 2048, CancellationToken cancellationToken = default)
    {
        _selections = null; _selectionErrors.Clear();
        _playedCardLimit = int.MaxValue;
        _branches = 0; _limit = Math.Clamp(branchLimit, 32, 20000); _truncated = false; _cancellation = cancellationToken;
        var resources = side == PlayerSide.User ? position.User : position.Opponent;
        LeaderSearchResult Unknown(string reason) => new(new(null, null, null, [], [reason], [], true, false, 0), 0, resources.LeaderCharges,
            "Leader opportunity cost depends on the current ability and remaining charges.");
        if (resources.CurrentLeaderId is not { } leader || Leader(leader) is not { } profile) return Unknown("Current leader ability is unknown or unsupported");
        if (ActionPositionIssue(position) is { } invalid) return Unknown(invalid);
        var assumptions = new List<string>();
        if (resources.LeaderCharges is null) assumptions.Add("Remaining leader charges unknown; assumes all printed charges remain");
        if (!position.RowEffectsKnownInactive) assumptions.Add("No unobserved row effects assumed");
        if (position.Zones.Any(zone => !zone.Complete)) assumptions.Add("Partial position: unseen identities/effects excluded");
        var charges = Math.Clamp(resources.LeaderCharges ?? profile.PrintedCharges, 0, 10);
        var initial = new LineState(position, [], ImmutableHashSet<string>.Empty);
        foreach (var card in position.Zones.SelectMany(zone => zone.Cards))
            if (Rule(card.CardId) is not { } rule || UnmodeledInZone(rule, Locate(position, card.InstanceId)!.Zone, card))
                initial = initial.Unknown("Unmodeled reaction: " + (Rule(card.CardId)?.Card.Name ?? card.CardId));
        initial = CheckActionEffects(initial, assumptions);
        LineState? best = null; int? bestPoints = null; var bestCharges = 0;
        IEnumerable<LineState> frontier = [initial]; var missing = initial.Missing;
        for (var used = 0; used <= charges; used++)
        {
            var next = frontier.ToArray();
            foreach (var state in next)
            foreach (var ended in EndTurn(state, side, 0))
            {
                missing = missing.Union(ended.Missing);
                var swing = ReachSwing(position, ended.Position, side);
                if (bestPoints is null || swing > bestPoints)
                { best = ended; bestPoints = swing; bestCharges = used; }
            }
            if (requiredSwing is { } required && bestPoints >= required) break;
            if (_truncated || used == charges) break;
            frontier = Bound(next.SelectMany(state => UseLeader(state, side, profile, charges - used, 0)))
                .DistinctBy(state => PositionNotation.Write(state.Position)).ToArray();
        }
        if (_truncated) missing = missing.Add("Leader search budget reached; best found is not a proven maximum");
        return new(new(missing.Count == 0 ? bestPoints : null, bestPoints, best?.Position, best?.Log ?? [], missing.Order().ToArray(), assumptions,
            !_truncated, best?.Random ?? false, _branches), bestCharges, resources.LeaderCharges, profile.Commitment);
    }
    private IEnumerable<LineState> UseLeader(LineState state, PlayerSide side, LeaderProfile profile, int remaining, int depth)
    {
        var resources = side == PlayerSide.User ? state.Position.User : state.Position.Opponent;
        resources = resources with { LeaderCharges = remaining - 1 };
        state = (state with { Position = side == PlayerSide.User ? state.Position with { User = resources } : state.Position with { Opponent = resources } }).Note("Leader charge · " + Rule(profile.Id)?.Card.Name);
        if (profile.Effect == "coins") return [GainCoins(state, side, profile.Amount)];
        if (profile.Effect is "spawn" or "spawn-coins")
            return SelectOptions("leader", "row", Enum.GetValues<BoardRow>().Where(row => state.Position.Zone(side, CardZone.Board, row).Cards.Length < 9),
                    row => row.ToString(), Selection("leader")?.Row?.ToString())
                .SelectMany(row => Spawn(state, _names.GetValueOrDefault(profile.Token!), side, row, depth))
                .Select(output => profile.Effect == "spawn-coins" ? GainCoins(output, side, profile.Amount) : output);
        if (profile.Effect == "play-token")
            return SpawnAndPlay(state, profile.Token!, side, depth);
        if (profile.Effect == "flurry")
        {
            IEnumerable<LineState> states = [state with { Random = true }];
            for (var i = 0; i < profile.Amount; i++) states = Bound(states.SelectMany(s =>
            {
                var enemies = Board(s.Position, Other(side)).Where(card => Rule(card.CardId)?.Card.Kind == CardKind.Unit).ToArray();
                return enemies.Length == 0 ? [s] : enemies.SelectMany(card => Damage(s, card.InstanceId, 1, depth, true));
            })).ToArray();
            return states;
        }
        var enemy = profile.Effect is "damage" or "lock-damage" or "bleeding-token";
        var targets = Board(state.Position, profile.Effect == "guerilla" ? null : enemy ? Other(side) : side)
            .Where(card => CanTarget(state.Position, card, side, profile.Effect == "guerilla" ? TargetSide.Any : enemy ? TargetSide.Enemy : TargetSide.Allied)).ToArray();
        if (targets.Length == 0) return []; // Cannot spend a targeted charge with no legal target.
        return SelectOptions("leader", "target", targets, card => card.InstanceId, Selection("leader")?.TargetId).SelectMany(card =>
        {
            var s = state.Note("Leader target #" + card.InstanceId);
            switch (profile.Effect)
            {
                case "guerilla": return Move(s, card.InstanceId, depth, side).SelectMany(output => Locate(state.Position, card.InstanceId)?.Side == side
                    ? [Boost(output, card.InstanceId, 3)] : Damage(output, card.InstanceId, 1, depth));
                case "damage": return Damage(s, card.InstanceId, profile.Amount, depth);
                case "lock-damage": return Damage(Status(s, card.InstanceId, CardStatus.Locked), card.InstanceId, profile.Amount, depth);
                case "carapace": return [Rule(card.CardId)?.Card.Faction == "Neutral" ? Boost(s, card.InstanceId, profile.Amount) : Status(Boost(s, card.InstanceId, profile.Amount), card.InstanceId, CardStatus.Veil)];
                case "shieldwall": return [Status(Boost(s, card.InstanceId, profile.Amount), card.InstanceId, CardStatus.Shield)];
                case "zeal":
                {
                    var boosted = Boost(s, card.InstanceId, profile.Amount);
                    if (Rule(card.CardId) is not { Card.Faction: not "Neutral", Order: not null } ||
                        Find(boosted.Position, card.InstanceId) is not { } orderCard)
                        return [boosted];
                    var ready = boosted with { Position = Change(boosted.Position, orderCard with { Cooldown = 0 }) };
                    return new[] { ready.Note("Inspired Zeal readies " + Rule(card.CardId)!.Card.Name) }
                        .Concat(OrderOnce(ready, card.InstanceId, side, depth + 1));
                }
                case "vitality": return [Duration(s, card.InstanceId, CardStatus.Vitality, profile.Amount)];
                case "bleeding-token":
                    var bled = Duration(s, card.InstanceId, CardStatus.Bleeding, profile.Amount);
                    return remaining == 1 ? SpawnRandomRow(bled, profile.Token!, side, depth) : [bled];
                case "ritual": return Damage(s, card.InstanceId, 1, depth).SelectMany(output => remaining == 1 ? SpawnRandomRow(output, profile.Token!, side, depth) : [output]);
                case "hunger":
                    var row = Locate(s.Position, card.InstanceId)!.Row!.Value;
                    // Capture power before destruction; spawned Ekimmara is a new instance, not the consumed card.
                    return Destroy(s, card.InstanceId, false, depth).SelectMany(output => Spawn(output, _names.GetValueOrDefault(profile.Token!), side, row, depth,
                        (Rule(_names[profile.Token!])?.Card.Power ?? 0) + card.Power));
                default: return [s.Unknown("Unsupported leader action")];
            }
        });
    }
    private IEnumerable<LineState> SpawnAndPlay(LineState state, string name, PlayerSide side, int depth, int? basePower = null,
        bool doomed = false, bool recursiveGeneratedChoice = false, bool original = false)
    {
        if (!_names.TryGetValue(name, out var id)) return [state.Unknown("Missing spawned-play definition: " + name)];
        var rule = Rule(id)!; var index = state.NextId + 1; while (Find(state.Position, "spawn-play-" + index) is not null) index++;
        var statuses = doomed ? rule.PrintedStatuses!.Add(CardStatus.Doomed) : rule.PrintedStatuses;
        var card = new PositionCard("spawn-play-" + index, id, basePower ?? rule.Card.Power, basePower ?? rule.Card.Power, rule.Card.PrintedArmor ?? 0, statuses, 0, 0, original);
        // Staging only, not a draw/actual hand addition. FromStartingDeck=false feeds Assimilate.
        var hand = state.Position.Zone(side, CardZone.Hand);
        return Play(state with { Position = Set(state.Position, hand, hand.Cards.Add(card)), NextId = index }, card.InstanceId, side, depth,
            explicitSequenceStep: recursiveGeneratedChoice);
    }
    private LineState GainCoins(LineState state, PlayerSide side, int amount)
    {
        var resources = side == PlayerSide.User ? state.Position.User : state.Position.Opponent;
        if (resources.Coins is null) return state.Unknown("Coin count unknown");
        var gained = Math.Min(9, resources.Coins.Value + amount) > resources.Coins.Value;
        resources = resources with { Coins = Math.Min(9, resources.Coins.Value + amount) };
        state = state with { Position = side == PlayerSide.User ? state.Position with { User = resources } : state.Position with { Opponent = resources } };
        var beforeListeners = state;
        if (gained)
            foreach (var unit in Board(state.Position, side).Where(card => Rule(card.CardId)?.Reaction == "townsfolk" &&
                         card.Statuses?.Contains(CardStatus.Locked) != true).ToArray()) state = Boost(state, unit.InstanceId, 1);
        return TrackReaction(beforeListeners, state);
    }
    private static LineState SetCoins(LineState state, PlayerSide side, int amount)
    {
        var resources = side == PlayerSide.User ? state.Position.User : state.Position.Opponent;
        resources = resources with { Coins = Math.Clamp(amount, 0, 9) };
        return state with { Position = side == PlayerSide.User ? state.Position with { User = resources } : state.Position with { Opponent = resources } };
    }
    private LineState Duration(LineState state, string id, CardStatus status, int amount)
    {
        var card = Find(state.Position, id); if (card is null || card.Statuses!.Contains(CardStatus.Veil)) return state;
        var opposite = status == CardStatus.Vitality ? CardStatus.Bleeding : CardStatus.Vitality;
        if ((card.Statuses.Contains(status) || card.Statuses.Contains(opposite)) && card.StatusTurns is null) return state.Unknown("Existing status duration unknown");
        var turns = card.StatusTurns ?? ImmutableDictionary<CardStatus, int>.Empty;
        var balance = turns.GetValueOrDefault(status) + amount - turns.GetValueOrDefault(opposite);
        var active = balance < 0 ? opposite : status;
        var changed = state with { Position = Change(state.Position, card with
        {
            Statuses = balance == 0 ? card.Statuses.Remove(status).Remove(opposite) : card.Statuses.Remove(status).Remove(opposite).Add(active),
            StatusTurns = turns.Remove(status).Remove(opposite).SetItem(active, Math.Abs(balance))
        }) };
        return amount > 0 && balance > 0 ? ReceivedStatus(changed, id) : changed;
    }
}
