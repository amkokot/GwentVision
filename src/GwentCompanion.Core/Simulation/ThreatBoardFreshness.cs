using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

/// <summary>Cheap invalidation for a cached calculation board. A counter change is not a replacement board.</summary>
public static class ThreatBoardFreshness
{
    public static bool MissingScoringSide(GameStateSnapshot state) => Enum.GetValues<Domain.PlayerSide>().Any(side =>
        state.Player(side).Score is { Value: > 0 } score && state.At is { } at && score.IsFresh(at, GwentRules.DynamicFactLifetime) &&
        !state.Cards.Any(card => card.Presence == CardPresence.Visible && card.Card.Kind == Domain.CardKind.Unit &&
            card.Location.Value is { Zone: CardZone.Board } location && location.Controller == side));

    public static bool Invalidated(GameStateSnapshot saved, GameStateSnapshot current)
    {
        if (saved.SessionId != current.SessionId || current.Phase is GamePhase.Ended or GamePhase.RoundTransition ||
            saved.Round?.Value != current.Round?.Value ||
            current.ActivePlayer is { } active && saved.ActivePlayer?.Value != active.Value) return true;
        bool Changed<T>(StateFact<T>? before, StateFact<T>? after) => after is not null &&
            (before is null || !EqualityComparer<T>.Default.Equals(before.Value, after.Value));
        foreach (var side in Enum.GetValues<Domain.PlayerSide>())
        {
            var before = saved.Player(side); var after = current.Player(side);
            if (Changed(before.Score, after.Score) || Changed(before.LeaderCharges, after.LeaderCharges) ||
                Changed(before.CurrentLeader, after.CurrentLeader) || Changed(before.HandCount, after.HandCount) ||
                Changed(before.Coins, after.Coins) || Changed(before.Passed, after.Passed) ||
                Changed(before.DeckCount, after.DeckCount) || Changed(before.GraveyardCount, after.GraveyardCount)) return true;
        }
        return false;
    }
}
