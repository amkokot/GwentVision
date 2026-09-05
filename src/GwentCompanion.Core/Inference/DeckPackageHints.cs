using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

public sealed record DeckPackageHint(CardDefinition Card, string Package, string Source, bool FromUserPick,
    int SupportingLists, int SourceLists, bool AutoFill)
{
    public string Explanation => $"{Package} package from {(FromUserPick ? "your pick" : "observed")} {Source}; " +
        (SourceLists == 0 ? "no complete source lists in cache" : $"{SupportingLists}/{SourceLists} cached source lists") +
        ". Curated synergy hint, not a measured probability or sighting.";
}

/// <summary>Small, explicit no-data fallback. Never creates observations or empirical samples.</summary>
public static class DeckPackageHints
{
    private static readonly string[] Crones = ["132206", "132207", "132208"];
    private static IEnumerable<(string Source, string Target, string Package, bool Auto)> Links()
    {
        foreach (var source in Crones)
        foreach (var target in Crones.Where(id => id != source))
            yield return (source, target, "Crones", true);
        foreach (var target in new[] { "202277", "202456", "113320", "203246" })
            yield return ("202282", target, "Sigvald / self-wound", false);
    }

    public static IReadOnlyList<DeckPackageHint> Build(IEnumerable<DeckDefinition> decks,
        IEnumerable<CardDefinition> catalog, IEnumerable<ObservedCard> observations, IEnumerable<DeckCard> assumptions,
        string? faction, ObservedStartingDeckAssessment? rules)
    {
        if (string.IsNullOrWhiteSpace(faction)) return [];
        var cards = catalog.DistinctBy(c => c.Id).ToDictionary(c => c.Id);
        var seen = observations.Where(o => StartingDeckRules.CountsAgainstStartingDeck(o.Provenance) &&
            o.Card.CanBeInStartingDeck && o.Confidence >= .8).Select(o => o.Card.Id).ToHashSet();
        var picks = assumptions.Where(c => c.Count > 0 && c.Card.CanBeInStartingDeck &&
            FactionCompatibility.IsPlayableBy(c.Card, faction) && DeckMetaAnalyzer.CardAllowed(c.Card, c.Count, rules))
            .Select(c => c.Card.Id).ToHashSet();
        var corpus = decks.Where(d => d.Faction.Equals(faction, StringComparison.OrdinalIgnoreCase) && DeckMetaAnalyzer.IsComplete(d))
            .GroupBy(DeckMetaAnalyzer.CompositionKey).Select(g => g.First()).ToArray();
        return Links().Where(l => seen.Contains(l.Source) || picks.Contains(l.Source)).Select(l =>
        {
            if (!cards.TryGetValue(l.Source, out var source) || !cards.TryGetValue(l.Target, out var target) ||
                !target.CanBeInStartingDeck || !FactionCompatibility.IsPlayableBy(target, faction) ||
                !DeckMetaAnalyzer.CardAllowed(target, 1, rules)) return null;
            var sourceLists = corpus.Where(d => d.CountOf(l.Source) > 0).ToArray();
            var supporting = sourceLists.Count(d => d.CountOf(l.Target) > 0);
            // Once three source lists are available, use the measured recommender, not
            // this fallback. Even one counterexample prevents curated automatic filling.
            if (sourceLists.Length >= 3) return null;
            return new DeckPackageHint(target, l.Package, source.Name, !seen.Contains(l.Source), supporting,
                sourceLists.Length, l.Auto && supporting == sourceLists.Length);
        }).OfType<DeckPackageHint>().GroupBy(h => h.Card.Id)
            .Select(g => g.OrderByDescending(h => h.AutoFill).ThenBy(h => h.FromUserPick).First()).ToArray();
    }
}
