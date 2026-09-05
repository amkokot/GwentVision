namespace GwentCompanion.Core.Domain;

public enum PlayerSide
{
    User,
    Opponent,
}

public static class FactionCompatibility
{
    public static bool IsPlayableBy(CardDefinition card, string deckFaction)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentException.ThrowIfNullOrWhiteSpace(deckFaction);
        return string.Equals(card.Faction, "Neutral", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(card.Faction, deckFaction, StringComparison.OrdinalIgnoreCase) ||
               card.SecondaryFactions.Contains(deckFaction);
    }

    public static IReadOnlySet<string> PlayableFactions(CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var result = new HashSet<string>(card.SecondaryFactions, StringComparer.OrdinalIgnoreCase);
        if (!string.Equals(card.Faction, "Neutral", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(card.Faction);
        }

        return result;
    }
}
