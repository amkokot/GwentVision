namespace GwentCompanion.Core.GameState;

/// <summary>
/// Curated faction routing for the compact Live synergy meters. This is
/// deliberately narrower than every keyword the rules engine understands: a
/// meter earns permanent UI space only when it represents a substantial faction
/// package, not one or two isolated cards.
/// </summary>
public static class FactionSynergyCatalog
{
    private static readonly IReadOnlyDictionary<string, string[]> ByFaction =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Monsters"] = ["Deathwish", "Dominance", "Organic", "Thrive"],
            ["Nilfgaard"] = ["Assimilate", "Flanking", "NG Cultist", "Tactic"],
            ["Northern Realms"] = ["Crew", "Grace", "Inspired"],
            ["Scoia'tael"] = ["Barricade", "Harmony", "Symbiosis"],
            ["Skellige"] = ["Alchemy", "Berserk", "Bloodthirst", "Pirate Armor", "Raid"],
            ["Syndicate"] = ["Crime", "Hoard", "Intimidate", "Tribute"],
        };

    public static IReadOnlyList<string> All { get; } = ByFaction.Values
        .SelectMany(names => names)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static IReadOnlyList<string> ForFaction(string? faction) =>
        !string.IsNullOrWhiteSpace(faction) && ByFaction.TryGetValue(faction, out var names)
            ? names
            : [];
}
