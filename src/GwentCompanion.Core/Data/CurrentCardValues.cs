using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

/// <summary>Current-value views, never an edit to the source deck or its historical evidence.</summary>
public sealed class CurrentCardValues(IEnumerable<CardDefinition> catalog)
{
    private readonly Dictionary<string, CardDefinition> _cards = catalog.DistinctBy(c => c.Id)
        .ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CardDefinition> _leaders = GwentOneCardCatalog.StartingLeaders(catalog)
        .GroupBy(c => LeaderKey(c.Faction, c.Name)).ToDictionary(g => g.Key, g => g.First());

    private static string LeaderKey(string faction, string name) => DeckSearchCatalog.Normalize(faction) + "|" + DeckSearchCatalog.Normalize(name);
    public CardDefinition Card(CardDefinition card) => _cards.GetValueOrDefault(card.Id) ?? card;
    public DeckDefinition Deck(DeckDefinition deck) => deck with
    {
        Cards = deck.Cards.Select(c => c with { Card = Card(c.Card) }).ToArray(),
        Stratagem = deck.Stratagem is { } stratagem ? Card(stratagem) : null,
        LeaderProvisionBonus = _leaders.GetValueOrDefault(LeaderKey(deck.Faction, deck.Leader))?.Provision ?? deck.LeaderProvisionBonus,
    };
}
