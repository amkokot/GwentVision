using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

/// <summary>Explicit reviewed choices, keyed by instance ID, never by display name. Missing choices are not optimized during replay.</summary>
public sealed record PlaySelection(BoardRow? Row = null, int? Slot = null, string? TargetId = null,
    string? TutorId = null, string? CreatedCardId = null, string? RandomTargetId = null, string? AlliedTargetId = null);
public sealed record ResolvedAction(int? Points, GamePosition? After, IReadOnlyList<string> Line,
    IReadOnlyList<string> Missing, IReadOnlyList<string> Assumptions, bool Complete, bool FavorableRandomness = false,
    int? MinimumPoints = null);

public sealed partial class TacticalPlayEngine
{
    private PlaySelection? Selection(string source) => _selections?.GetValueOrDefault(source);
    private IEnumerable<T> SelectOptions<T>(string source, string kind, IEnumerable<T> options, Func<T, string> key, string? selected)
    {
        if (_selections is null) return options;
        var values = options.ToArray();
        if (selected is not null)
        {
            var matches = values.Where(item => key(item) == selected).ToArray();
            if (matches.Length == 0) _selectionErrors.Add($"Illegal {kind} choice for {source}: {selected}");
            return matches;
        }
        if (values.Length > 1) _selectionErrors.Add($"Recorded {kind} choice missing for {source}");
        return values.Take(1);
    }

    public ResolvedAction ResolvePlay(GamePosition position, string instanceId, PlayerSide side,
        IReadOnlyDictionary<string, PlaySelection> selections, bool endTurn = false, int branchLimit = 4096)
    {
        var result = SearchPlay(position, instanceId, side, endTurn, branchLimit, default, selections, false);
        return new(result.MaximumPoints, result.After, result.Line, result.Missing, result.Assumptions, result.SearchComplete, result.FavorableRandomness);
    }

    public ResolvedAction ResolveOrder(GamePosition position, string instanceId, PlayerSide side, string? targetId = null,
        IReadOnlyDictionary<string, string>? randomTargets = null)
    {
        var choices = (randomTargets ?? new Dictionary<string, string>()).ToDictionary(p => p.Key, p => new PlaySelection(RandomTargetId: p.Value));
        choices[instanceId] = choices.GetValueOrDefault(instanceId, new()) with { TargetId = targetId };
        return ResolveAction(position, side, choices, state => OrderOnce(state, instanceId, side, 0));
    }

    /// <summary>Greedy prospective Order, distinct from replaying a player's recorded target.</summary>
    public ResolvedAction MaximumOrder(GamePosition position, string instanceId, PlayerSide side) =>
        ResolveAction(position, side, null, state => OrderOnce(state, instanceId, side, 0), 512);

    public ResolvedAction AutomaticReach(GamePosition position, PlayerSide side) =>
        ResolveAction(position, side, null, state => EndTurn(state, side, 0), 512);

    public ResolvedAction ResolveLeader(GamePosition position, PlayerSide side, string? targetId = null, BoardRow? row = null)
        => ResolveAction(position, side, new Dictionary<string, PlaySelection> { ["leader"] = new(Row: row, TargetId: targetId) }, state =>
        {
            var owner = side == PlayerSide.User ? position.User : position.Opponent;
            return owner.CurrentLeaderId is { } id && Leader(id) is { } profile && owner.LeaderCharges is > 0
                ? UseLeader(state, side, profile, owner.LeaderCharges.Value, 0)
                : [state.Unknown("Leader ability/remaining charge unavailable")];
        });

    public ResolvedAction ResolveEndTurn(GamePosition position, PlayerSide side, IReadOnlyDictionary<string, string>? randomTargets = null) =>
        ResolveAction(position, side, (randomTargets ?? new Dictionary<string, string>()).ToDictionary(p => p.Key, p => new PlaySelection(RandomTargetId: p.Value)), state => EndTurn(state, side, 0));

    public PlaySearchResult MaximumOrders(GamePosition position, PlayerSide side, bool endTurn = true, int branchLimit = 4096)
    {
        var result = ResolveAction(position, side, null, state => endTurn
            ? Orders(state, side, 0).SelectMany(item => EndTurn(item, side, 0)) : Orders(state, side, 0), branchLimit);
        return new(result.Points, result.After is null ? null : Gap(result.After, side) - Gap(position, side), result.After,
            result.Line, result.Missing, result.Assumptions, result.Complete, result.FavorableRandomness, _branches);
    }

    private ResolvedAction ResolveAction(GamePosition position, PlayerSide side, IReadOnlyDictionary<string, PlaySelection>? selections,
        Func<LineState, IEnumerable<LineState>> operation, int branchLimit = 4096)
    {
        _selections = selections; _selectionErrors.Clear(); _orderVisits.Clear(); _branches = 0; _limit = Math.Clamp(branchLimit, 32, 20000); _truncated = false; _cancellation = default;
        _agentObjectivePosition = position;
        _reachActor = side; _rootPlayId = null;
        _playedCardLimit = int.MaxValue;
        var assumptions = new List<string>();
        if (position.Zones.Any(zone => !zone.Complete)) assumptions.Add("Partial position: unknown cards/effects excluded");
        if (!position.RowEffectsKnownInactive && position.RowEffects is null) assumptions.Add("No unobserved row effects assumed");
        if (ActionPositionIssue(position) is { } invalid)
            return new(null, null, [], [invalid], assumptions, false);
        var input = new LineState(position, [], ImmutableHashSet<string>.Empty);
        foreach (var zone in position.Zones)
        foreach (var card in zone.Cards)
            if (Rule(card.CardId) is not { } rule || UnmodeledInZone(rule, zone.Zone, card)) input = input.Unknown("Unmodeled reaction: " + (Rule(card.CardId)?.Card.Name ?? card.CardId));
        input = CheckActionEffects(input, assumptions);
        var outputs = operation(input).ToArray();
        var missing = outputs.SelectMany(item => item.Missing).Concat(input.Missing).Concat(_selectionErrors).Distinct().ToList();
        if (_truncated) missing.Add("Action search budget reached");
        if (outputs.Length == 0) missing.Add("No legal action for supplied choices");
        if (selections is not null && outputs.Select(item => PositionNotation.Write(item.Position)).Distinct().Count() > 1)
            missing.Add("Unresolved random/target outcome in recorded action");
        var best = outputs.MaxBy(item => ReachSwing(position, item.Position, side));
        return new(missing.Count == 0 && best is not null ? ReachSwing(position, best.Position, side) : null,
            best?.Position, best?.Log ?? [], missing, assumptions, !_truncated, best?.Random ?? false,
            missing.Count == 0 && outputs.Length > 0 ? outputs.Min(item => ReachSwing(position, item.Position, side)) : null);
    }

    private static string? ActionPositionIssue(GamePosition position)
    {
        var expected = GamePosition.EmptyKnown().Zones;
        if (position.Zones.Length != 12 || expected.Any(zone => position.Zones.Count(item => item.Side == zone.Side && item.Zone == zone.Zone && item.Row == zone.Row) != 1))
            return "Malformed position zones";
        if (position.Zones.SelectMany(zone => zone.Cards).GroupBy(card => card.InstanceId).Any(group => group.Count() != 1) ||
            position.Zones.Any(zone => zone.Cards.Length > (zone.Zone == CardZone.Board ? 9 : 200))) return "Invalid instance identity or zone capacity";
        if (Board(position).Any(card => card.Power is null || card.BasePower is null || card.Armor is null || card.Statuses is null)) return "Unread board stats/statuses";
        if (position.Zones.SelectMany(zone => zone.Cards).Any(card => card.Power < 0 || card.BasePower < 0 || card.Armor < 0 || card.ExtraThrive < 0)) return "Invalid negative stats";
        return null;
    }
    private LineState CheckActionEffects(LineState input, List<string> assumptions)
    {
        foreach (var card in Board(input.Position))
            if (card.Statuses?.Any(status => status == CardStatus.Infused && card.ExtraThrive == 0) == true)
                input = input.Unknown("Infusion effect unresolved: " + card.InstanceId);
        foreach (var owner in new[] { input.Position.User, input.Position.Opponent })
            if (owner.CurrentLeaderId is { } current && !LeaderPassiveSupported(current)) input = input.Unknown("Unmodeled leader passive");
            else if (owner.CurrentLeaderId is null && !owner.PassiveEffectsKnownInactive) assumptions.Add(owner.Side + " leader/passive unknown");
        return input;
    }
}
