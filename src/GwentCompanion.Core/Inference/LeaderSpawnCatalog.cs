using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

public sealed record LeaderStartingDeckAssumption(CardDefinition Card, int Copies, string Reason);
public sealed record LeaderHandPlayRule(int MaximumPlays, CardKind? Kind, bool BronzeOnly, bool GoldOnly, int DrawsAfterward)
{
    public bool Matches(CardDefinition card) => card.CanBeInStartingDeck &&
        (!BronzeOnly || !card.IsGold) && (!GoldOnly || card.IsGold) &&
        (Kind is null || card.Kind == Kind);
}

/// <summary>Recognition/reference coverage, not proof of a spawn or starting-deck membership.</summary>
public static class LeaderSpawnCatalog
{
    private static readonly Regex HandPlayClause = new(
        @"\bOrder(?:\s*\([^)]*\))?:\s*Play\s+(?:(?:up to\s+)?(?<count>[1-9]\d*)|an?)\s+(?:(?<color>bronze|gold)\s+)?(?<kind>unit|special(?: card)?|artifact|card)\s+from your hand\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// True when the source's printed rules name this exact card as the output of
    /// a Spawn clause. This intentionally works for leaders and stratagems as
    /// well as ordinary cards, and covers specials/artifacts as well as units:
    /// collectible output cards are not necessarily
    /// starting-deck cards in the match where they were observed.
    /// </summary>
    public static bool NamesSpawnedCard(CardDefinition source, CardDefinition target)
    {
        if (target.Kind is CardKind.Unknown or CardKind.Leader or CardKind.Stratagem ||
            source.AbilityText?.Contains("Spawn", StringComparison.OrdinalIgnoreCase) != true)
            return false;

        return Regex.IsMatch(source.AbilityText,
            @"\bSpawn(?: and play)?\b[^.\n]*\b" + Regex.Escape(target.Name) + @"(?:s)?\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool NamesSpawnedUnit(CardDefinition source, CardDefinition target) =>
        target.Kind == CardKind.Unit && NamesSpawnedCard(source, target);

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

    /// <summary>
    /// Strong deck-construction priors implied by a fixed leader output whose own
    /// payoff explicitly summons every natural copy from the deck. The spawned
    /// leader body is still separate from these starting copies.
    /// </summary>
    public static IReadOnlyList<LeaderStartingDeckAssumption> StartingDeckAssumptions(
        CardDefinition leader, IEnumerable<CardDefinition> catalog) =>
        NamedUnits(leader, catalog)
            .Where(card => card.CanBeInStartingDeck && !card.IsGold && NamesSpawnedUnit(leader, card))
            .Where(card => Regex.IsMatch(card.AbilityText ?? "",
                @"\bSummon all copies of self from your deck\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Select(card => new LeaderStartingDeckAssumption(card, 2,
                $"{leader.Name} deck-construction prior: its generated {card.Name} summons all natural copies from the deck; assume the legal two-copy bronze package."))
            .ToArray();

    public static bool VariableCopy(CardDefinition leader) => leader.Kind == CardKind.Leader &&
        Regex.IsMatch(leader.AbilityText ?? "", @"Spawn (?:a |its )?base copy", RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses a bounded leader Order that plays a real card from hand. This is
    /// deliberately capability metadata: merely seeing the leader never proves
    /// that the Order was activated or that a particular card was played.
    /// </summary>
    public static LeaderHandPlayRule? HandPlayRule(CardDefinition leader)
    {
        if (leader.Kind != CardKind.Leader) return null;
        var text = leader.AbilityText ?? "";
        var match = HandPlayClause.Match(text);
        if (!match.Success) return null;
        var maximum = match.Groups["count"].Success ? int.Parse(match.Groups["count"].Value) : 1;
        var kind = match.Groups["kind"].Value.ToLowerInvariant() switch
        {
            "unit" => CardKind.Unit,
            "special" or "special card" => CardKind.Special,
            "artifact" => CardKind.Artifact,
            _ => (CardKind?)null,
        };
        var color = match.Groups["color"].Value;
        var tail = text[match.Index..];
        var draw = Regex.Match(tail, @"\bthen\b[^.\n]*\bdraw\s+(?<count>[1-9]\d*|as many cards)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var draws = !draw.Success ? 0 : draw.Groups["count"].Value.StartsWith("as many", StringComparison.OrdinalIgnoreCase)
            ? maximum : int.Parse(draw.Groups["count"].Value);
        return new(maximum, kind, color.Equals("bronze", StringComparison.OrdinalIgnoreCase),
            color.Equals("gold", StringComparison.OrdinalIgnoreCase), draws);
    }
}
