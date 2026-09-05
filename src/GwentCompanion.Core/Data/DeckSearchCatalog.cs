using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

public sealed record SearchableDeck(string Name, string Context, Uri? SourceUri,
    DeckDefinition? Deck, DeckIndexEntry? IndexEntry, string SearchText);

/// <summary>Search the whole index, enriching cached entries instead of replacing the index.</summary>
public static class DeckSearchCatalog
{
    public static IReadOnlyList<SearchableDeck> Build(IEnumerable<DeckIndexEntry> index, IEnumerable<DeckDefinition> cached,
        DeckLibrary? library = null)
    {
        var decks = cached.GroupBy(deck => deck.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var records = new Dictionary<string, LibraryDeck>(StringComparer.OrdinalIgnoreCase);
        var sourceDecks = new Dictionary<string, DeckDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in library?.Records ?? [])
        {
            records.TryAdd(record.Deck.Id, record);
            foreach (var alias in record.Aliases) records.TryAdd(alias, record);
            foreach (var source in record.Sources)
                if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && DeckLinkFileReader.CanonicalUrl(uri) is not null)
                    sourceDecks.TryAdd(Key(uri), record.Deck);
        }
        foreach (var deck in decks.Values)
            if (deck.SourceUri is { } uri && DeckLinkFileReader.CanonicalUrl(uri) is not null) sourceDecks.TryAdd(Key(uri), deck);
        var result = new List<SearchableDeck>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in index.OrderByDescending(entry => entry.LastEdited).ThenBy(entry => entry.RecencyRank)
                     .GroupBy(entry => Key(entry.DeckUri), StringComparer.OrdinalIgnoreCase))
        {
            var entry = group.First();
            decks.TryGetValue(group.Key, out var deck);
            deck ??= records.GetValueOrDefault(group.Key)?.Deck ?? sourceDecks.GetValueOrDefault(group.Key);
            if (!seen.Add(deck?.Id ?? group.Key)) continue;
            var context = deck is null
                ? $"{entry.Faction} · {entry.Leader} · {entry.Workbook} · {entry.Sheet} · not cached"
                : $"{deck.Faction} · {deck.Leader} · {deck.CardCount} cards · cached";
            var text = string.Join(" ", group.Select(item =>
                $"{item.Name} {item.Faction} {item.Leader} {item.Workbook} {item.Sheet} {item.Notes} {item.DeckUri}"));
            var patchText = DeckPatchMetadata.Describe(deck?.Patches ?? entry.Patches);
            result.Add(new SearchableDeck(deck?.Name ?? entry.Name, context + " · " + patchText, entry.DeckUri, deck, entry,
                Normalize(text + " " + patchText + " " + DeckText(deck) + " " + LibraryText(deck is null ? null : records.GetValueOrDefault(deck.Id)))));
        }
        // A locally cached deck need not still appear in a refreshed workbook.
        foreach (var deck in decks.Values.Where(deck => !seen.Contains(deck.Id)).OrderBy(deck => deck.RecencyRank))
            result.Add(new SearchableDeck(deck.Name, $"{deck.Faction} · {deck.Leader} · {deck.CardCount} cards · cached · {DeckPatchMetadata.Describe(deck.Patches)}",
                deck.SourceUri, deck, null, Normalize(DeckText(deck) + " " + LibraryText(records.GetValueOrDefault(deck.Id)))));
        return result;
    }

    public static string[] Terms(string? query) => Normalize(query ?? string.Empty)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static bool Matches(string normalizedText, IReadOnlyList<string> terms) =>
        terms.All(term => normalizedText.Contains(term, StringComparison.Ordinal));

    // Comma-separated card names/phrases are ANDed; names in deck titles do not qualify.
    public static bool ContainsCards(DeckDefinition? deck, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (deck is null) return false;
        return query.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(phrase => MatchesCardRequirement(deck, phrase));
    }

    // Excluding a card asserts absence, which cannot be established from an incomplete observation.
    public static bool ExcludesCards(DeckDefinition? deck, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (deck is null || deck.CardCount < 25) return false;
        return !query.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(phrase => MatchesCardRequirement(deck, phrase));
    }
    private static bool MatchesCardRequirement(DeckDefinition deck, string phrase)
    {
        var match = Regex.Match(phrase, @"^\s*(\d+)\s*[x×]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var copies = 1;
        if (match.Success && (!int.TryParse(match.Groups[1].Value, out copies) || copies < 1)) return false;
        var terms = Terms(match.Success ? match.Groups[2].Value : phrase);
        if (terms.Length == 0) return false;
        // A phrase must match enough copies of one identity, not several different names combined.
        return deck.Cards.GroupBy(c => c.Card.Id).Any(group => group.Sum(c => c.Count) >= copies && Matches(Normalize(group.First().Card.Name), terms));
    }

    private static string LibraryText(LibraryDeck? record) => record is null ? "" :
        string.Join(' ', record.OriginalNames.Concat(record.Sources));

    public static string Normalize(string value)
    {
        var result = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (character is '\'' or '\u2019' or '\u2018') continue;
            result.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        return string.Join(" ", result.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string DeckText(DeckDefinition? deck) => deck is null ? string.Empty :
        $"{deck.Name} {deck.Faction} {deck.Leader} {deck.Stratagem?.Name} {DeckPatchMetadata.Describe(deck.Patches)} {string.Join(' ', deck.Cards.Select(item => item.Card.Name))}";
    private static string Key(Uri uri) => PlayGwentDeckCacheService.CacheKey(uri);
}
