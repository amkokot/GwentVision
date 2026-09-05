using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Vision;

/// <summary>Identical artwork cannot identify an ability variant. Keep a visual family,
/// with no executable ability, until separate text/effect evidence resolves it.</summary>
public sealed class CardAppearanceFamilies
{
    private readonly Dictionary<string, CardDefinition> _families = [];
    public CardAppearanceFamilies(IEnumerable<CardDefinition> catalog)
    {
        foreach (var group in catalog.DistinctBy(c => c.Id).Where(c => !c.CanBeInStartingDeck && c.ArtUri is not null && c.Kind is CardKind.Unit or CardKind.Special)
                     .GroupBy(c => (c.Name, Art: c.ArtUri!.AbsoluteUri)).Where(g => g.Count() > 1 && g.Any(c => c.Kind == CardKind.Unit)))
        {
            var first = group.Where(c => c.Kind == CardKind.Unit).OrderBy(c => c.Id, StringComparer.Ordinal).First();
            var powers = group.Where(c => c.Power > 0).Select(c => c.Power).Distinct().ToArray();
            var family = first with { Id = "visual-family:" + first.Id, Power = powers.Length == 1 ? powers[0] : 0,
                AbilityText = null, CanBeInStartingDeck = false };
            foreach (var card in group) _families[card.Id] = family;
        }
    }
    public CardDefinition Normalize(CardDefinition card) => _families.GetValueOrDefault(card.Id, card);
    public CardSighting Normalize(CardSighting sight)
    {
        var card = Normalize(sight.Card);
        return card.Id == sight.Card.Id ? sight : sight with { Card = card,
            Evidence = sight.Evidence + " Shared-art family; exact ability variant unresolved." };
    }
}
