using GwentCompanion.Core.Inference;

namespace GwentCompanion.Core.Domain;

public static class DeckBuildValidation
{
    public static IReadOnlyList<string> Errors(DeckDefinition deck, bool complete = true, bool archetypes = false)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(deck.Leader)) errors.Add("Choose a leader ability.");
        if (complete && deck.CardCount < 25) errors.Add($"Add {25 - deck.CardCount} more cards (25 minimum).");
        if (complete && deck.UnitCount < 13) errors.Add($"Add {13 - deck.UnitCount} more units (13 minimum).");
        if (deck.ProvisionTotal > 150 + deck.LeaderProvisionBonus)
            errors.Add($"Over the provision limit by {deck.ProvisionTotal - 150 - deck.LeaderProvisionBonus}.");
        foreach (var group in deck.Cards.GroupBy(c => c.Card.Id))
        {
            var card = group.First().Card;
            if (group.Any(c => c.Count < 1) || group.Sum(c => c.Count) > (card.IsGold ? 1 : 2)) errors.Add($"Invalid copy count: {card.Name}.");
            if (!card.CanBeInStartingDeck || card.Kind is not (CardKind.Unit or CardKind.Special or CardKind.Artifact)) errors.Add($"Not collectible: {card.Name}.");
            if (!FactionCompatibility.IsPlayableBy(card, deck.Faction)) errors.Add($"Wrong faction: {card.Name}.");
        }
        if (deck.Stratagem is { } stratagem && (stratagem.Kind != CardKind.Stratagem || !FactionCompatibility.IsPlayableBy(stratagem, deck.Faction)))
            errors.Add("The stratagem is not available to this faction.");
        if (archetypes)
        {
            var rules = StartingDeckRules.EvaluateExactDeck(deck);
            if (deck.Cards.Any(c => c.Card.Name is "Golden Nekker" or "Ciri: Nova") && rules.GoldenNekker.State == ConstraintState.RuledOut)
                errors.Add("Golden Nekker / Nova: " + rules.GoldenNekker.Reason);
            if (deck.Cards.Any(c => c.Card.Name is "Shupe's Day Off" or "Radeyah") && rules.Shupe.State == ConstraintState.RuledOut)
                errors.Add("Singleton payoff: remove duplicate cards.");
            if (complete && deck.ContainsName("Renfri") && deck.UnitCount < 25) errors.Add("Renfri requires 25 units.");
        }
        return errors;
    }
}
