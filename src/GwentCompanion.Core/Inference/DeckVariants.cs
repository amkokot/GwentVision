using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

public static class DeckVariants
{
    public static int Replacements(DeckDefinition left, DeckDefinition right) => Math.Max(
        left.Cards.Sum(item => Math.Max(0, item.Count - right.CountOf(item.Card.Id))),
        right.Cards.Sum(item => Math.Max(0, item.Count - left.CountOf(item.Card.Id))));

    public static IReadOnlyList<DeckDefinition> CompatibleFamily(DeckDefinition anchor, IEnumerable<DeckDefinition> library,
        IEnumerable<ObservedCard> evidence, ObservedStartingDeckAssessment? constraints = null)
    {
        var cards = evidence.Where(item => StartingDeckRules.CountsAgainstStartingDeck(item.Provenance)).ToArray();
        return library.Append(anchor).DistinctBy(deck => deck.Id).Where(deck =>
            deck.Faction == anchor.Faction && deck.Leader == anchor.Leader && deck.CardCount == anchor.CardCount &&
            (deck.Stratagem is null || anchor.Stratagem is null || deck.Stratagem.Id == anchor.Stratagem.Id) && Replacements(anchor, deck) <= 2)
            .Where(deck => cards.All(card => deck.CountOf(card.Card.Id) >= card.ObservedCopies) && DeckMetaAnalyzer.Allowed(deck, constraints))
            .OrderBy(deck => Replacements(anchor, deck)).ThenByDescending(deck => deck.LastEdited).ThenBy(deck => deck.RecencyRank).ToArray();
    }
}
