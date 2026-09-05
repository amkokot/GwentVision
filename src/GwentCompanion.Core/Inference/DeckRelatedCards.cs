using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

public sealed record RelatedCardReport(IReadOnlyDictionary<string, double> Scores, int Lists, double BestOverlap, bool ExactCore);

/// <summary>Builder alternatives, not auto-fill constraints or evidence about a played deck.</summary>
public static class DeckRelatedCards
{
    public static RelatedCardReport Rank(IEnumerable<DeckDefinition> library, IEnumerable<CardDefinition> catalog,
        IReadOnlyList<DeckCard> current, IReadOnlyList<DeckCard> focus, string? faction, string? leader, string? stratagem,
        IReadOnlySet<DeckCopyKey> excluded)
    {
        var prices = catalog.DistinctBy(c => c.Id).ToDictionary(c => c.Id, c => Math.Max(1, c.Provision));
        var owned = current.GroupBy(c => c.Card.Id).ToDictionary(g => g.Key, g => g.Sum(c => c.Count));
        var anchors = focus.GroupBy(c => c.Card.Id).Select(g => new DeckCard(g.First().Card, g.Sum(c => c.Count))).ToArray();
        var spend = anchors.Sum(c => c.Count * prices.GetValueOrDefault(c.Card.Id, Math.Max(1, c.Card.Provision)));
        if (spend == 0) return new(new Dictionary<string, double>(), 0, 0, false);
        var donors = library.Where(d => d.CardCount >= 25 && (faction is null || d.Faction == faction) &&
                (leader is null || d.Leader == leader) && (stratagem is null || d.Stratagem?.Id == stratagem) &&
                !excluded.Any(key => d.CountOf(key.CardId) >= key.Copy))
            .DistinctBy(DeckMetaAnalyzer.CompositionKey)
            .Select(d => (Deck: d, Overlap: anchors.Sum(c => Math.Min(c.Count, d.CountOf(c.Card.Id)) *
                prices.GetValueOrDefault(c.Card.Id, Math.Max(1, c.Card.Provision))) / (double)spend,
                Extras: d.Cards.Where(c => c.Count > owned.GetValueOrDefault(c.Card.Id) && owned.GetValueOrDefault(c.Card.Id) < (c.Card.IsGold ? 1 : 2)).ToArray()))
            .Where(d => d.Overlap > 0 && d.Extras.Length > 0).ToArray();
        if (donors.Length == 0) return new(new Dictionary<string, double>(), 0, 0, false);
        var best = donors.Max(d => d.Overlap);
        // Small explicit cores retain exact co-occurrence when available. Full decks use nearby
        // lists with actual alternatives, so an exact full-list match cannot flatten all scores.
        var exact = anchors.Sum(c => c.Count) <= 6 && best >= 1 - 1e-12;
        var related = donors.Where(d => exact ? d.Overlap >= 1 - 1e-12 : d.Overlap >= Math.Max(.2, best - .15)).ToArray();
        var scores = new Dictionary<string, double>();
        foreach (var donor in related)
        {
            var weight = exact ? 1 : Math.Pow(donor.Overlap, 6);
            foreach (var card in donor.Extras)
                scores[card.Card.Id] = scores.GetValueOrDefault(card.Card.Id) + weight;
        }
        return new(scores, related.Length, best, exact);
    }
}
