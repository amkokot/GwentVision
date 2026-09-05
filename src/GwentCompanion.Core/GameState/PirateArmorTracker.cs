using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.GameState;

public sealed record PirateArmorEstimate(int ObservedTriggers, string Reason);

/// <summary>
/// Keeps the match-persistent lower bound for Onslaught's hand-Armor passive.
/// A trigger is recorded only when two reliable readings of the same visible
/// enemy unit establish an undamaged-to-damaged transition.
/// </summary>
public sealed class PirateArmorTracker
{
    private readonly Dictionary<PlayerSide, int> _triggers = [];
    private string? _session;
    private long _lastRevision = -1;

    public void Reset()
    {
        _triggers.Clear();
        _session = null;
        _lastRevision = -1;
    }

    public PirateArmorEstimate Read(PlayerSide side)
    {
        var count = _triggers.GetValueOrDefault(side);
        return new(count,
            $"{count} observed Onslaught trigger{(count == 1 ? "" : "s")}; each gave 1 Armor to every Pirate and Ship then in hand. " +
            "This is a vision-backed lower bound: missed damage transitions are not invented, and cards drawn later did not receive earlier Armor.");
    }

    public bool Observe(GameStateUpdate update)
    {
        if (!update.Accepted || update.After.At is not { } at) return false;
        if (_session != update.After.SessionId)
        {
            Reset();
            _session = update.After.SessionId;
        }
        if (update.After.Revision <= _lastRevision) return false;
        _lastRevision = update.After.Revision;

        var changed = false;
        foreach (var current in update.After.Cards.Where(card => card.Card.Kind == CardKind.Unit &&
                     card.Location.Value.Zone == CardZone.Board && card.Presence == CardPresence.Visible &&
                     (card.Damaged?.At == at && Reliable(card.Damaged, at) ||
                      card.Power?.At == at && Reliable(card.Power, at) && Reliable(card.BasePower, at))))
        {
            var previous = update.Before.Cards.FirstOrDefault(card => card.InstanceId == current.InstanceId);
            if (previous is null || previous.Card.Id != current.Card.Id ||
                previous.Location.Value.Controller != current.Location.Value.Controller ||
                previous.Location.Value.Zone != CardZone.Board ||
                !KnownDamaged(previous, at, out var wasDamaged) || !KnownDamaged(current, at, out var isDamaged)) continue;

            if (wasDamaged || !isDamaged) continue;

            var beneficiary = current.Location.Value.Controller == PlayerSide.User
                ? PlayerSide.Opponent
                : PlayerSide.User;
            var player = update.After.Player(beneficiary);
            var effectiveLeader = player.CurrentLeader?.Value ?? player.StartingLeader?.Value;
            if (effectiveLeader?.Equals("Onslaught", StringComparison.OrdinalIgnoreCase) != true) continue;
            _triggers[beneficiary] = _triggers.GetValueOrDefault(beneficiary) + 1;
            changed = true;
        }
        return changed;
    }

    private static bool Reliable(StateFact<int>? fact, DateTimeOffset at) =>
        fact is { Confidence: >= .8 } && fact.At <= at && at - fact.At <= GwentRules.DynamicFactLifetime;

    private static bool Reliable(StateFact<bool>? fact, DateTimeOffset at) =>
        fact is { Confidence: >= .8 } && fact.At <= at && at - fact.At <= GwentRules.DynamicFactLifetime;

    private static bool KnownDamaged(GameCardInstance card, DateTimeOffset at, out bool damaged)
    {
        if (Reliable(card.Damaged, at)) { damaged = card.Damaged!.Value; return true; }
        if (Reliable(card.Power, at) && Reliable(card.BasePower, at))
        { damaged = card.Power!.Value < card.BasePower!.Value; return true; }
        damaged = false; return false;
    }
}
