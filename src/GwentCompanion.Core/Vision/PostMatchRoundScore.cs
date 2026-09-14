namespace GwentCompanion.Core.Vision;

/// <summary>Repeated readings of the final score table; user is the left column.</summary>
public sealed record PostMatchRoundScore(int Round, int UserScore, int OpponentScore);
