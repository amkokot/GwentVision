using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

public sealed record IndexedDeckInteraction(string Id, string Name, IReadOnlyList<string> Tags, string Ability,
    bool ManuallyReviewed, string DetectionStatus);
public static class DeckInteractionIndex
{
    // Exhaustive catalog pass, not an assertion that text matching simulates effects.
    private static readonly (string Tag, string Pattern)[] Patterns =
    [
        ("Summon", @"\bSummon\b"), ("Setup", @"start of the game|starts? (in|on|the game)"),
        ("Deck mutation", @"(?:Spawn|Shuffle|Banish|move|add|put)[^\n.]*\bdeck\b"),
        ("Creation", @"\bCreate\b|Spawn[^\n.]*cop(?:y|ies)"), ("Reveal / inspect", @"\bReveal\b|Look at[^\n.]*\bdeck\b"),
        ("Replay / transform / transfer", @"\breplay\b|\btransform\b|\bSeize\b|opposite side"),
        ("Starting-deck condition", @"\bstarting deck\b"),
        ("Tutor / draw / discard", @"\bPlay[^\n.]*\bfrom[^\n.]*\bdeck\b|\bDraw\b|\bDiscard\b"),
        ("Scenario / generated payoff", @"\bScenario:|\bSpawn\b"),
    ];
    public static IReadOnlyList<IndexedDeckInteraction> Scan(IEnumerable<CardDefinition> catalog) => catalog
        .Where(card => card.AbilityText is not null).DistinctBy(card => card.Id).Select(card =>
        {
            var tags = Patterns.Where(item => Regex.IsMatch(card.AbilityText!, item.Pattern, RegexOptions.IgnoreCase)).Select(item => item.Tag).ToArray();
            var reviewed = DeckInteractionCatalog.All.Any(item => item.CardId == card.Id);
            if (reviewed && tags.Length == 0) tags = ["Reviewed interaction"]; // e.g. Knickers' deliberately nontechnical wording
            return new IndexedDeckInteraction(card.Id, card.Name, tags, card.AbilityText!, reviewed,
                reviewed ? "Reviewed semantics; automatic evidence only where observable context is supported" : "Catalog watchlist; no automatic effect-resolution claim");
        }).Where(item => item.Tags.Count > 0).OrderBy(item => item.Name).ThenBy(item => item.Id).ToArray();
}
