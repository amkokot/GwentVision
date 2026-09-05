using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

/// <summary>Keyword grouping, not a claim that the complete ability is executable.</summary>
public static class TacticalSynergies
{
    private static readonly string[] Conditional = ["Dominance", "Bloodthirst", "Berserk", "Hoard", "Tribute", "Crew", "Inspired", "Grace", "Barricade", "Deathwish"];
    private static readonly string[] Intrinsic = ["Thrive", "Assimilate", "Flanking", "Harmony", "Symbiosis", "Intimidate"];
    public static IEnumerable<TacticalRule> Rules(IEnumerable<CardDefinition> cards)
    {
        foreach (var card in cards.Where(card => card.CanBeInStartingDeck && card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact))
        foreach (var keyword in Conditional.Concat(Intrinsic))
        {
            var pattern = Conditional.Contains(keyword)
                ? @"\b" + keyword + @"(?:\s+(?<threshold>\d+))?\s*[:)]"
                : @"(?:^|[.\n]\s*)" + keyword + @"(?:\s+(?<threshold>\d+))?\s*[.\n]";
            var match = Regex.Match(card.AbilityText ?? "", pattern, RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var condition = keyword switch
            {
                "Dominance" => TacticalCondition.Dominance,
                "Bloodthirst" when match.Groups["threshold"].Value == "3" => TacticalCondition.Bloodthirst3,
                "Hoard" when match.Groups["threshold"].Value == "4" => TacticalCondition.Hoard4,
                "Hoard" when match.Groups["threshold"].Value == "9" => TacticalCondition.Hoard9,
                _ => TacticalCondition.Unknown
            };
            // Different thresholds/targets must never inherit another card's reviewed condition.
            yield return new(card, (match.Groups["threshold"].Success ? keyword + " " + match.Groups["threshold"].Value : keyword) +
                " · check the relevant target, timing and current board state.\n" + card.AbilityText,
                condition, false, Synergy: keyword, LocalCondition: condition == TacticalCondition.Unknown);
        }
    }
}

public sealed record TacticalSynergyGroup(string Name, IReadOnlyList<TacticalWatchRow> Cards)
{
    public int Active => Cards.Count(row => row.State == TacticalState.Ready);
}
public sealed record PlaysSections(IReadOnlyList<TacticalWatchRow> Summons,
    IReadOnlyList<TacticalSynergyGroup> Synergies, IReadOnlyList<TacticalWatchRow> Bonuses);

public static class PlaysSectionBuilder
{
    public static PlaysSections Build(TacticalReport report, string? faction, IEnumerable<ObservedCard> observations,
        DeckDefinition? pin = null, IEnumerable<CardDefinition>? assumed = null)
    {
        var observed = observations.Select(item => item.Card.Id).ToHashSet();
        var supported = observed.Concat(pin?.Cards.Select(item => item.Card.Id) ?? [])
            .Concat((assumed ?? []).Select(card => card.Id)).ToHashSet();
        bool Relevant(TacticalWatchRow row) => observed.Contains(row.Rule.Card.Id) ||
            (string.IsNullOrWhiteSpace(faction) ? row.Rule.Card.Faction == "Neutral" : FactionCompatibility.IsPlayableBy(row.Rule.Card, faction));
        var summons = report.Summons.Where(Relevant)
            .Where(row => SummonWatchPresentation.ShouldDisplay(row, supported)).ToArray();
        var plays = report.Plays.Where(Relevant).ToArray();
        var groups = plays.Where(row => row.Rule.Synergy is not null).GroupBy(row => row.Rule.Synergy!)
            .Where(group => pin is null || group.Any(row => supported.Contains(row.Rule.Card.Id)))
            .Select(group => new TacticalSynergyGroup(group.Key, group.ToArray()))
            .OrderByDescending(group => group.Active > 0).ThenBy(group => group.Name).ToArray();
        return new(summons, groups, plays.Where(row => row.Rule.Synergy is null).ToArray());
    }
}

/// <summary>Expand for newly seen arrivals, not every redraw; respect subsequent manual collapse.</summary>
public sealed class SummonSectionExpansion
{
    private readonly HashSet<string> _notified = [];
    public bool Observe(IEnumerable<TacticalWatchRow> rows)
    {
        var added = false;
        foreach (var row in rows.Where(row => row.State == TacticalState.Seen)) added |= _notified.Add(row.Rule.Card.Id);
        return added;
    }
    public void Reset() => _notified.Clear();
}
