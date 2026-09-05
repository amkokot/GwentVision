using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

/// <summary>Recognition/reference coverage, not proof of a spawn or starting-deck membership.</summary>
public static class LeaderSpawnCatalog
{
    public static IReadOnlyList<CardDefinition> NamedUnits(CardDefinition leader, IEnumerable<CardDefinition> catalog)
    {
        if (leader.Kind != CardKind.Leader || leader.AbilityText?.Contains("Spawn", StringComparison.OrdinalIgnoreCase) != true) return [];
        var units = catalog.Where(card => card.Kind == CardKind.Unit).ToArray();
        // Longest name first: "Deafening Siren" must not also request the unrelated "Siren".
        var pattern = @"(?<![\p{L}\p{N}])(?<card>" + string.Join('|', units.Select(card => card.Name).Distinct()
            .OrderByDescending(name => name.Length).Select(Regex.Escape)) + @")(?:s)?(?![\p{L}\p{N}]|['’]s)";
        var names = Regex.Matches(leader.AbilityText, pattern, RegexOptions.IgnoreCase).Select(match => match.Groups["card"].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return units.Where(card => names.Contains(card.Name)).DistinctBy(card => card.Id).ToArray();
    }
    public static bool VariableCopy(CardDefinition leader) => leader.Kind == CardKind.Leader &&
        Regex.IsMatch(leader.AbilityText ?? "", @"Spawn (?:a |its )?base copy", RegexOptions.IgnoreCase);
}
