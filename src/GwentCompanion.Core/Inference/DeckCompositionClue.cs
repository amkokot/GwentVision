using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

/// <summary>A repeatedly read printed/dynamic deck-building total, not a card sighting.</summary>
public sealed record DeckCompositionClue(string SourceCardId, string SourceName, string Category, int Count,
    string? ExcludedCardId, DateTimeOffset At, double Confidence, string Evidence)
{
    public int CountIn(DeckDefinition deck) => deck.Cards
        .Where(item => item.Card.Id != ExcludedCardId && item.Card.HasCategory(Category))
        .Sum(item => item.Count);
    public double Weight(DeckDefinition deck)
    {
        var distance = Math.Abs(CountIn(deck) - Count);
        var exactWeight = 4d;
        var measured = distance == 0 ? exactWeight : Math.Pow(.18, Math.Min(4, distance));
        return 1 + Math.Clamp(Confidence, 0, 1) * (measured - 1);
    }
}
