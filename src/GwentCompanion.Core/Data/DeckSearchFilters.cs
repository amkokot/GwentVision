using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

[Flags] public enum DeckCategoryFilter { None = 0, Devotion = 1, Singleton = 2, Renfri = 4, GoldenNekker = 8, Musicians = 16 }
public enum DeckCompletenessFilter { All, Complete, Incomplete, Uncached }
public sealed record DeckSearchFilters(DeckCategoryFilter Categories = DeckCategoryFilter.None, string? Patch = null,
    DeckCompletenessFilter Completeness = DeckCompletenessFilter.All, bool IncludeUnknown = false)
{
    public bool Matches(DeckDefinition? deck, DeckIndexEntry? entry = null, bool incomplete = false)
    {
        if (Completeness == DeckCompletenessFilter.Complete && (deck is null || incomplete || deck.CardCount < 25)) return false;
        if (Completeness == DeckCompletenessFilter.Incomplete && !incomplete) return false;
        if (Completeness == DeckCompletenessFilter.Uncached && (deck is not null || incomplete)) return false;
        var patches = deck?.Patches ?? entry?.Patches;
        if (Patch is not null && !(patches?.Any(p => p.Label == Patch) == true || IncludeUnknown && (patches is null || patches.Count == 0))) return false;
        if (Categories == DeckCategoryFilter.None) return true;
        if (deck is null || incomplete || deck.CardCount < 25) return IncludeUnknown;
        var rules = StartingDeckRules.EvaluateExactDeck(deck);
        return (!Categories.HasFlag(DeckCategoryFilter.Devotion) || rules.Devotion.State == ConstraintState.Confirmed) &&
            (!Categories.HasFlag(DeckCategoryFilter.Singleton) || rules.Shupe.State == ConstraintState.Confirmed) &&
            (!Categories.HasFlag(DeckCategoryFilter.Renfri) || rules.Renfri.State == ConstraintState.Confirmed) &&
            (!Categories.HasFlag(DeckCategoryFilter.GoldenNekker) || rules.GoldenNekker.State == ConstraintState.Confirmed) &&
            (!Categories.HasFlag(DeckCategoryFilter.Musicians) || !deck.Cards.Any(c => c.Card.Provision == 4 && c.Card.Id != "202200"));
    }
}
