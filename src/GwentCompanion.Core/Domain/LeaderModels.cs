namespace GwentCompanion.Core.Domain;

public enum CurrentLeaderSource
{
    StartingDeck,
    AnnaHenrietta,
    Renfri,
}

public sealed record LeaderAbilitySnapshot(
    string? StartingName,
    string? StartingFaction,
    double StartingConfidence,
    string CurrentName,
    string? CurrentFaction,
    CurrentLeaderSource CurrentSource,
    string Reason)
{
    public bool WasReplaced => CurrentSource != CurrentLeaderSource.StartingDeck;
}

public static class LeaderStateInference
{
    public static LeaderAbilitySnapshot Evaluate(
        string? startingName,
        string? startingFaction,
        double startingConfidence,
        IEnumerable<ObservedCard> observations,
        string? opponentStartingName,
        string? opponentStartingFaction)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var currentName = string.IsNullOrWhiteSpace(startingName) ? "Unknown starting ability" : startingName;
        var currentFaction = startingFaction;
        var source = CurrentLeaderSource.StartingDeck;
        var reason = "No leader-replacement card has been observed.";

        foreach (var observation in observations.OrderBy(item => item.ObservedAt))
        {
            if (string.Equals(observation.Card.Name, "Renfri", StringComparison.OrdinalIgnoreCase))
            {
                currentName = "Renfri curse + blessing";
                currentFaction = "Neutral";
                source = CurrentLeaderSource.Renfri;
                reason = "Renfri was observed replacing the active ability; the original leader still governs deck construction.";
            }
            else if (string.Equals(observation.Card.Name, "Anna Henrietta", StringComparison.OrdinalIgnoreCase))
            {
                currentName = string.IsNullOrWhiteSpace(opponentStartingName)
                    ? "Opponent's base leader ability"
                    : opponentStartingName;
                currentFaction = opponentStartingFaction;
                source = CurrentLeaderSource.AnnaHenrietta;
                reason = "Anna Henrietta was observed replacing the active ability with the opponent's base ability; the starting faction remains unchanged.";
            }
        }

        return new LeaderAbilitySnapshot(
            startingName,
            startingFaction,
            Math.Clamp(startingConfidence, 0, 1),
            currentName!,
            currentFaction,
            source,
            reason);
    }
}
