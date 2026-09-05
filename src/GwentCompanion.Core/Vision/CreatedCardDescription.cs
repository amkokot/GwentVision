using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Vision;

public sealed record CreatedCardDescription(string SourceId, string SourceName, IReadOnlyList<CardDefinition> Candidates, string Text, bool IsDeckReveal = false);
public static class CreatedCardDescriptionReader
{
    public static CreatedCardDescription? Parse(string text, IEnumerable<CardDefinition> catalog)
    {
        var cards = catalog.ToArray();
        static string Words(string value) => " " + Regex.Replace(value.ToUpperInvariant(), @"[^\p{L}\p{N}]+", " ").Trim() + " ";
        var words = Words(text);
        var sources = cards.Where(card => card.Id is "203275" or "203198" or "200022" or "202192").Where(card => words.Contains(Words(card.Name), StringComparison.Ordinal)).ToArray();
        if (sources.Length != 1) return null;
        var source = sources[0];
        words = words.Replace(Words(source.Name), " ", StringComparison.Ordinal);
        var printed = Words(source.AbilityText ?? "");
        var matches = cards.Where(card => card.Id != source.Id && card.CanBeInStartingDeck && card.Kind == CardKind.Unit &&
                (source.Id is "200022" or "202192" || card.IsGold) && card.Name.Length >= 5)
            .Where(card => words.Contains(Words(card.Name), StringComparison.Ordinal) && !printed.Contains(Words(card.Name), StringComparison.Ordinal))
            .GroupBy(card => Words(card.Name)).Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
        // Prefer the longest exact name (e.g. a named variant, not its short base card).
        matches = matches.Where(card => !matches.Any(other => other.Id != card.Id && Words(other.Name).Contains(Words(card.Name), StringComparison.Ordinal))).ToArray();
        return matches.Length == 0 ? null : new(source.Id, source.Name, matches, text, source.Id == "200022");
    }
}
