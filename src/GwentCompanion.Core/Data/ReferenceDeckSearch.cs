using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

/// <summary>Reference browsing order is distinct from the inference model's probability order.</summary>
public static class ReferenceDeckSearch
{
    public static IReadOnlyList<DeckDefinition> Search(IEnumerable<DeckDefinition> source, string? query,
        string? faction = null, string? leader = null, IEnumerable<ObservedCard>? observed = null)
    {
        var phrase = DeckSearchCatalog.Normalize(query ?? "");
        var terms = DeckSearchCatalog.Terms(query);
        var evidence = (observed ?? []).Where(item => StartingDeckRules.CountsAgainstStartingDeck(item.Provenance))
            .GroupBy(item => item.Card.Id).Select(group => group.MaxBy(item => item.ObservedCopies)!).ToArray();
        return source.DistinctBy(deck => deck.Id)
            .Where(deck => (string.IsNullOrEmpty(faction) || Same(deck.Faction, faction)) &&
                (string.IsNullOrEmpty(leader) || Same(deck.Leader, leader)))
            .Where(deck => DeckSearchCatalog.Matches(DeckSearchCatalog.Normalize(deck.Name + " " + deck.Faction + " " + deck.Leader + " " +
                string.Join(' ', deck.Cards.Select(item => item.Card.Name))), terms))
            .OrderByDescending(deck => Relevance(deck, phrase))
            // With an explicit query, chronology breaks relevance ties. With no query, keep best observed overlaps first.
            .ThenByDescending(deck => terms.Length == 0 ? evidence.Sum(item => Math.Min(item.ObservedCopies, deck.CountOf(item.Card.Id))) : 0)
            .ThenByDescending(Date)
            .ThenBy(deck => deck.RecencyRank)
            .ThenBy(deck => deck.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(deck => deck.Id, StringComparer.Ordinal)
            .ToArray();
    }
    private static bool Same(string a, string b) => DeckSearchCatalog.Normalize(a) == DeckSearchCatalog.Normalize(b);
    private static int Relevance(DeckDefinition deck, string phrase)
    {
        if (phrase.Length == 0) return 0;
        var name = DeckSearchCatalog.Normalize(deck.Name);
        if (name == phrase) return 3;
        if (name.StartsWith(phrase, StringComparison.Ordinal)) return 2;
        return name.Contains(phrase, StringComparison.Ordinal) ? 1 : 0;
    }
    public static DateTimeOffset? Date(DeckDefinition deck) => deck.SourceUpdatedAt ?? deck.CachedAt;
    public static string DateLabel(DeckDefinition deck) => Date(deck) is { } date
        ? $"{(deck.SourceUpdatedAt is not null ? "PlayGWENT" : "Cached")} {date:yyyy-MM-dd}" : "Date unknown";
}
