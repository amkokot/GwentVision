using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

public sealed record ReturningDeckPattern(string PreviousPatch, string LatestPatch, int GapPatches,
    int InterveningLists, int RecentLists)
{
    public string Explanation => $"Returning pattern: {PreviousPatch} → {LatestPatch}; {InterveningLists} similar cached lists " +
        $"in the {GapPatches} intervening patches. Exact/one-card neighbours with compatible leaders. " +
        "Sparse cache coverage is not proof the archetype disappeared, and older substitutions remain uncertain.";
}

/// <summary>Detect a recent reappearance after a sparse interval, without assuming a buff caused it.</summary>
public static class ReturningDeckPatterns
{
    public const double HistoricalTransfer = .25;

    public static ReturningDeckPattern?[] Find(IReadOnlyList<DeckDefinition> decks, int[] families, PatchPredictionContext context)
    {
        PatchRecency.TryIndex(context.TargetPatch, out var target);
        var result = new ReturningDeckPattern?[decks.Count];
        var dates = decks.Select(d => PatchRecency.SupportedPatchIndices(d, context)).ToArray();
        var counts = decks.Select(d => d.Cards.GroupBy(c => c.Card.Id).ToDictionary(g => g.Key, g => g.Sum(c => c.Count))).ToArray();
        bool Near(int a, int b) => decks[a].CardCount == decks[b].CardCount &&
            (string.IsNullOrWhiteSpace(decks[a].Leader) || string.IsNullOrWhiteSpace(decks[b].Leader) ||
             decks[a].Leader.Equals(decks[b].Leader, StringComparison.OrdinalIgnoreCase)) &&
            counts[a].Sum(c => Math.Min(c.Value, counts[b].GetValueOrDefault(c.Key))) >= decks[a].CardCount - 1;
        foreach (var anchor in Enumerable.Range(0, decks.Count).Where(i => dates[i].Any(p => target - p <= 1)))
        {
            // Direct neighbours only: do not rejuvenate a remote list through a chain
            // of one-card variants that cumulatively changes the entire strategy.
            var neighbours = Enumerable.Range(0, decks.Count).Where(i => families[i] == families[anchor] && Near(anchor, i)).ToArray();
            var latest = dates[anchor].Max();
            var older = neighbours.SelectMany(i => dates[i]).Where(p => p <= latest - 3).Distinct().Order();
            foreach (var previous in older)
            {
                var between = neighbours.Where(i => dates[i].Any(p => p > previous && p < latest)).ToArray();
                var occupiedPatches = between.SelectMany(i => dates[i]).Where(p => p > previous && p < latest).Distinct().Count();
                if (between.Length > 1 || occupiedPatches > 1) continue;
                var pattern = new ReturningDeckPattern(PatchRecency.Label(previous), PatchRecency.Label(latest),
                    latest - previous - 1, between.Length, neighbours.Count(i => dates[i].Contains(latest)));
                foreach (var i in neighbours)
                    if (result[i] is not { } existing || PatchRecency.Start(existing.LatestPatch) < PatchRecency.Start(pattern.LatestPatch))
                        result[i] = pattern;
                break;
            }
        }
        return result;
    }
}
