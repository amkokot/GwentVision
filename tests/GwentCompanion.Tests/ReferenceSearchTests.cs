using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

internal static class ReferenceSearchTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static void Run(string root)
    {
        var date = DateTimeOffset.Parse("2026-08-20T00:00:00Z");
        var card = new CardDefinition("a", "Roach", "Neutral", CardKind.Unit, 9);
        var older = new DeckDefinition("old", "Test old", "Skellige", "Battle Trance", 15, [new(card)],
            LastEdited: date.AddDays(10), SourceUpdatedAt: date, CachedAt: date.AddDays(10));
        var newer = older with { Id = "new", Name = "Test new", SourceUpdatedAt = date.AddDays(1), LastEdited = date.AddDays(-5) };
        var local = older with { Id = "local", Name = "Test local", SourceUpdatedAt = null, CachedAt = date.AddDays(2) };
        var exact = older with { Id = "exact", Name = "Roach", SourceUpdatedAt = date.AddYears(-1) };
        var other = newer with { Id = "other", Faction = "Monsters", Leader = "Fruits of Ysgith" };
        var source = new[] { older, newer, local, exact, other };
        var result = ReferenceDeckSearch.Search(source, "Roach", "Skellige", "Battle Trance");
        Check(result.Select(deck => deck.Id).SequenceEqual(new[] { "exact", "local", "new", "old" }), "Query relevance first; then website date or cache date, never spreadsheet dates/hash order.");
        Check(ReferenceDeckSearch.Search(source, "zzzzz").Count == 0, "Empty results remain empty.");
        Check(ReferenceDeckSearch.Search(source, null, "Monsters", "Battle Trance").Count == 0, "Faction and leader filters are both enforced.");
        Check(ReferenceDeckSearch.DateLabel(local).StartsWith("Cached") && ReferenceDeckSearch.DateLabel(newer).StartsWith("PlayGWENT"), "Date origin is explicit.");
        var library = new DeckLibrary(); library.Merge([local]); library.Merge([local with { CachedAt = date.AddDays(20) }]);
        library.Rename(local.Id, "Renamed");
        Check(library.Find(local.Id)!.Deck.CachedAt == local.CachedAt, "Rename/reimport must not make an old deck newly cached.");
        var file = System.IO.Path.Combine(root, "GwentCompanion", "diagnostics", "reference-date-tests", Guid.NewGuid().ToString("N") + ".json");
        library.Save(file);
        Check(DeckLibrary.Load(file).Find(local.Id)!.Deck.CachedAt == local.CachedAt, "Cache dates survive persistence.");
    }
}
