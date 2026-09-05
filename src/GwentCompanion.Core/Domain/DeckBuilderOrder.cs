namespace GwentCompanion.Core.Domain;

/// <summary>Builder-list ordering calibrated against the recorded decks, not draw order.</summary>
public static class DeckBuilderOrder
{
    public static IComparer<CardDefinition> Comparer { get; } = System.Collections.Generic.Comparer<CardDefinition>.Create((a, b) =>
    {
        var result = b.Provision.CompareTo(a.Provision);
        if (result != 0) return result;
        result = b.IsGold.CompareTo(a.IsGold);
        if (result != 0) return result;
        result = (a.Kind == CardKind.Unit).CompareTo(b.Kind == CardKind.Unit);
        if (result != 0) return result;
        if (a.Kind == CardKind.Unit && b.Kind == CardKind.Unit)
        {
            result = b.Power.CompareTo(a.Power);
            if (result != 0) return result;
        }
        // Equal-power/type ties follow stable card identity in the recordings, not rarity/name:
        // Alp precedes Incubus; Mahakam Volunteers precedes Dwarf Berserker.
        if (int.TryParse(a.Id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var aId) &&
            int.TryParse(b.Id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bId))
        {
            result = aId.CompareTo(bId);
            if (result != 0) return result;
        }
        result = StringComparer.InvariantCultureIgnoreCase.Compare(a.Name.Trim(), b.Name.Trim());
        return result != 0 ? result : StringComparer.Ordinal.Compare(a.Id, b.Id);
    });
    public static IOrderedEnumerable<DeckCard> Sort(IEnumerable<DeckCard> cards) => cards.OrderBy(item => item.Card, Comparer);
}
