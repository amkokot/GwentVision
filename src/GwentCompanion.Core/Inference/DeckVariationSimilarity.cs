using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

public sealed record DeckOverlap(int SharedProvisions, int LeftProvisions, int RightProvisions, int SharedCopies)
{
    public double Ratio => Math.Max(LeftProvisions, RightProvisions) == 0 ? 0 : SharedProvisions / (double)Math.Max(LeftProvisions, RightProvisions);
}

/// <summary>Copy-aware overlap using one common price for each card, not historical patch price differences.</summary>
public sealed class DeckVariationSimilarity
{
    public const double Threshold = .90;
    private readonly IReadOnlyDictionary<string, int> _prices;
    public IReadOnlyDictionary<string, int> Prices => _prices;
    public DeckVariationSimilarity(IReadOnlyDictionary<string, int> prices) => _prices = prices;
    public DeckVariationSimilarity(IEnumerable<DeckDefinition> decks, IEnumerable<CardDefinition>? catalog = null)
    {
        var prices = decks.SelectMany(d => d.Cards).GroupBy(c => c.Card.Id)
            .ToDictionary(g => g.Key, g => Math.Max(1, g.Max(c => c.Card.Provision)), StringComparer.Ordinal);
        foreach (var card in catalog ?? []) prices[card.Id] = Math.Max(1, card.Provision);
        _prices = prices;
    }
    public DeckOverlap Compare(DeckDefinition left, DeckDefinition right) => Compare(left.Cards, right.Cards);
    public DeckOverlap Compare(IEnumerable<DeckCard> left, IEnumerable<DeckCard> right)
    {
        var a = left.GroupBy(c => c.Card.Id).ToDictionary(g => g.Key, g => g.Sum(c => c.Count));
        var b = right.GroupBy(c => c.Card.Id).ToDictionary(g => g.Key, g => g.Sum(c => c.Count));
        int Price(string id) => _prices.GetValueOrDefault(id, 4);
        return new(a.Sum(c => Math.Min(c.Value, b.GetValueOrDefault(c.Key)) * Price(c.Key)),
            a.Sum(c => c.Value * Price(c.Key)), b.Sum(c => c.Value * Price(c.Key)),
            a.Sum(c => Math.Min(c.Value, b.GetValueOrDefault(c.Key))));
    }
    public static bool HeaderCompatible(DeckDefinition a, DeckDefinition b) =>
        a.CardCount >= 25 && b.CardCount >= 25 && a.CardCount == b.CardCount && a.Faction == b.Faction && a.Leader == b.Leader;
    public bool IsVariation(DeckDefinition a, DeckDefinition b, double threshold = Threshold) =>
        HeaderCompatible(a, b) && ConstructionKey(a) == ConstructionKey(b) && Compare(a, b).Ratio + 1e-10 >= threshold;
    // These cards change the construction rules, not just the tuning of a package.
    private static string ConstructionKey(DeckDefinition deck) => string.Join('|',
        deck.ContainsName("Golden Nekker") || deck.ContainsName("Ciri: Nova"), deck.ContainsName("Renfri"),
        deck.ContainsName("Shupe's Day Off") || deck.ContainsName("Radeyah"));
}
