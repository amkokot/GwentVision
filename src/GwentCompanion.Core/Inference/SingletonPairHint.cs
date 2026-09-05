using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

/// <summary>A red, explicitly uncertain badge hint, not fabricated second-copy evidence.</summary>
public static class SingletonPairHint
{
    public static string? Build(IEnumerable<DeckDefinition> decks, IReadOnlyList<ObservedCard> evidence,
        ObservedStartingDeckAssessment rules, DeckProjectionEdits? edits = null)
    {
        if (rules.Shupe.State != ConstraintState.Possible || rules.Radeyah.State is ConstraintState.Likely or ConstraintState.Confirmed) return null;
        foreach (var observed in evidence.Where(o => o.Confidence >= .8 && o.ObservedCopies == 1 &&
            StartingDeckRules.CountsAgainstStartingDeck(o.Provenance) && CompanionCardRules.ThinningPairs.Contains(o.Card.Id)))
        {
            if (edits?.Excluded.Contains(new(observed.Card.Id,2)) == true) continue;
            var lists = decks.Where(d => DeckMetaAnalyzer.IsComplete(d) && d.CountOf(observed.Card.Id)>0)
                .DistinctBy(DeckMetaAnalyzer.CompositionKey).ToArray();
            var families = DeckCompositionFamilies.Build(lists);
            var groups = Enumerable.Range(0,lists.Length).GroupBy(i=>families[i]).ToArray();
            var paired = lists.Count(d=>d.CountOf(observed.Card.Id)>=2);
            var successes = groups.Count(g=>g.All(i=>lists[i].CountOf(observed.Card.Id)>=2));
            if (DeckMetaAnalyzer.WilsonLower(successes,groups.Length)<.9 || paired<.98*lists.Length) continue;
            return $"Shupe unlikely: {observed.Card.Name} is paired in {paired}/{lists.Length} distinct complete cached lists ({groups.Length} variant families). The second copy is inferred, not seen; a singleton exception or generated card remains possible.";
        }
        return null;
    }
}
